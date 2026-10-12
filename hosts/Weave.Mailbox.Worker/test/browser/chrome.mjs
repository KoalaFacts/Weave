import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { access, mkdir, readFile, writeFile } from 'node:fs/promises';
import { delimiter, join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';

export async function until(check, description, timeout = 15000) {
    const deadline = Date.now() + timeout;
    while (Date.now() < deadline) {
        if (await check()) return;
        await delay(50);
    }
    throw Error('Timed out: ' + description);
}

async function chromePath() {
    const candidates = process.env.CHROME_BIN ? [process.env.CHROME_BIN] :
        (process.env.PATH ?? '').split(delimiter).flatMap(path =>
            ['google-chrome', 'google-chrome-stable', 'chromium', 'chromium-browser'].map(name => join(path, name)));
    for (const candidate of candidates) {
        try { await access(candidate); return candidate; } catch (error) { if (error.code !== 'ENOENT') throw error; }
    }
    throw Error('Install Chrome/Chromium or set CHROME_BIN. Tests never download a browser or disable its sandbox.');
}

export class Chrome {
    pending = new Map();
    pages = [];
    nextId = 0;
    log = '';
    trace = [];
    errors = [];

    constructor(root, evidence, origin) { Object.assign(this, { root, evidence, origin }); }

    async start() {
        assert.equal(process.platform, 'linux', 'Browser fixture requires Linux process/profile isolation.');
        const profile = join(this.root, 'chrome-profile');
        await mkdir(profile);
        const executable = await chromePath();
        this.child = spawn(executable, [
            '--headless=new', '--remote-debugging-address=127.0.0.1', '--remote-debugging-port=0',
            '--user-data-dir=' + profile, '--no-first-run', '--no-default-browser-check',
            '--disable-background-networking', '--disable-component-update', '--disable-sync',
            '--disable-default-apps', '--disable-extensions', '--disable-dev-shm-usage', '--no-proxy-server',
            '--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE 127.0.0.1', 'about:blank'
        ], { detached: true, stdio: ['ignore', 'pipe', 'pipe'] });
        const retain = data => { this.log = (this.log + data).slice(-32768); };
        this.child.stdout.on('data', retain);
        this.child.stderr.on('data', retain);
        this.exit = new Promise(resolve => {
            this.child.once('error', error => { this.startError = error; resolve(); });
            this.child.once('exit', resolve);
        });
        let portInfo;
        await until(async () => {
            if (this.startError) throw this.startError;
            assert.equal(this.child.exitCode, null, 'Chrome exited before CDP readiness: ' + this.log);
            assert.equal(this.child.signalCode, null, 'Chrome failed with its normal sandbox: ' + this.log);
            try { portInfo = await readFile(join(profile, 'DevToolsActivePort'), 'utf8'); return true; }
            catch (error) { if (error.code !== 'ENOENT') throw error; return false; }
        }, 'protected Chrome startup');
        const [port, path] = portInfo.trim().split('\n');
        this.socket = new WebSocket('ws://127.0.0.1:' + port + path);
        await new Promise((resolve, reject) => {
            const timer = setTimeout(() => reject(Error('Timed out connecting to Chrome CDP.')), 10000);
            this.socket.addEventListener('open', () => { clearTimeout(timer); resolve(); }, { once: true });
            this.socket.addEventListener('error', error => { clearTimeout(timer); reject(error); }, { once: true });
        });
        this.socket.addEventListener('message', event => {
            const value = JSON.parse(event.data);
            if (value.id) {
                const pending = this.pending.get(value.id);
                if (!pending) return;
                this.pending.delete(value.id);
                clearTimeout(pending.timer);
                if (value.error) pending.reject(Error(JSON.stringify(value.error)));
                else pending.resolve(value.result);
            } else {
                const page = this.pages.find(page => page.sessionId === value.sessionId);
                if (page) page.event(value).catch(error => this.errors.push(error.message));
            }
        });
        this.socket.addEventListener('close', () => {
            for (const pending of this.pending.values()) {
                clearTimeout(pending.timer);
                pending.reject(Error('Chrome CDP connection closed.'));
            }
            this.pending.clear();
        });
        const version = await this.command('Browser.getVersion');
        await writeFile(join(this.evidence, 'browser-version.json'), JSON.stringify({ executable, ...version }, null, 2));
    }

    command(method, params = {}, sessionId) {
        const id = ++this.nextId;
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => {
                this.pending.delete(id);
                reject(Error('CDP timed out: ' + method));
            }, 10000);
            this.pending.set(id, { resolve, reject, timer });
            this.socket.send(JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) }));
        });
    }

    async page(name, path = '/') {
        const { browserContextId } = await this.command('Target.createBrowserContext');
        const { targetId } = await this.command('Target.createTarget', { url: 'about:blank', browserContextId });
        const { sessionId } = await this.command('Target.attachToTarget', { targetId, flatten: true });
        const page = new Page(this, name, sessionId);
        this.pages.push(page);
        await page.command('Page.enable');
        await page.command('Page.setLifecycleEventsEnabled', { enabled: true });
        await page.command('Runtime.enable');
        await page.command('Network.enable');
        await page.command('Fetch.enable', { patterns: [
            { urlPattern: '*', requestStage: 'Request' },
            { urlPattern: this.origin + '/outbox/*', requestStage: 'Response' }
        ] });
        await page.resize(1440, 1000);
        await page.navigate(path);
        return page;
    }

    async close() {
        try {
            if (this.socket?.readyState === WebSocket.OPEN) {
                await this.command('Browser.close').catch(error => this.errors.push(error.message));
                this.socket.close();
            }
            if (this.child) {
                const exited = await Promise.race([this.exit.then(() => true), delay(3000).then(() => false)]);
                if (!exited) { this.child.kill('SIGKILL'); await this.exit; }
                // Chrome children share this fixture-owned process group; clean any survivor too.
                if (this.child.pid) {
                    try { process.kill(-this.child.pid, 'SIGKILL'); }
                    catch (error) { if (error.code !== 'ESRCH') throw error; }
                }
            }
        } finally {
            await writeFile(join(this.evidence, 'chrome.log'), this.log);
            await writeFile(join(this.evidence, 'network.json'), JSON.stringify(this.trace, null, 2));
            await writeFile(join(this.evidence, 'browser-errors.json'), JSON.stringify(this.errors, null, 2));
        }
    }
}

class Page {
    loadedDocuments = new Set();
    sentMessages = [];
    failNextSendResponse = false;
    constructor(browser, name, sessionId) { Object.assign(this, { browser, name, sessionId }); }
    command(method, params) { return this.browser.command(method, params, this.sessionId); }
    async event({ method, params }) {
        if (method === 'Page.lifecycleEvent' && params.name === 'load') this.loadedDocuments.add(params.loaderId);
        if (method === 'Runtime.exceptionThrown') this.browser.errors.push(this.name + ': ' + params.exceptionDetails.text);
        if (method !== 'Fetch.requestPaused') return;
        const { requestId, request, responseStatusCode } = params;
        const url = new URL(request.url);
        // Record only paths and statuses, never credential headers or request bodies.
        this.browser.trace.push({ page: this.name, method: request.method, path: url.pathname, status: responseStatusCode });
        if (url.origin !== this.browser.origin && !['about:', 'chrome:'].includes(url.protocol)) {
            this.browser.errors.push('Blocked unexpected browser request: ' + url.origin);
            await this.command('Fetch.failRequest', { requestId, errorReason: 'AccessDenied' });
            return;
        }
        if (request.method === 'POST' && url.pathname.startsWith('/outbox/')) {
            if (responseStatusCode === undefined) this.sentMessages.push(JSON.parse(request.postData));
            else if (this.failNextSendResponse) {
                this.failNextSendResponse = false;
                assert.equal(responseStatusCode, 200, 'Lost-response test requires a committed successful send.');
                await this.command('Fetch.failRequest', { requestId, errorReason: 'ConnectionClosed' });
                return;
            }
        }
        await this.command('Fetch.continueRequest', { requestId });
    }
    async evaluate(expression) {
        const result = await this.command('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
        assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails));
        return result.result.value;
    }
    async wait(expression, description = expression) {
        await until(() => this.evaluate(expression), this.name + ': ' + description);
    }
    async navigate(path) {
        const url = path.startsWith('chrome:') ? path : this.browser.origin + path;
        const navigation = await this.command('Page.navigate', { url });
        assert.equal(navigation.errorText, undefined, 'Navigation failed: ' + navigation.errorText);
        assert.ok(navigation.loaderId, 'Expected a new document navigation.');
        // Observe the matching document load before evaluating JavaScript, so a destroyed
        // about:blank execution context is never mistaken for an application failure.
        await until(() => this.loadedDocuments.has(navigation.loaderId), this.name + ': document load');
        await this.wait(`location.href === ${JSON.stringify(url)} && document.readyState === 'complete'`);
    }
    resize(width, height, mobile = false) {
        return this.command('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile });
    }
    fill(id, value) {
        return this.evaluate(`(() => { const e = document.getElementById(${JSON.stringify(id)}); e.value = ${JSON.stringify(value)}; e.dispatchEvent(new Event('input', { bubbles: true })); })()`);
    }
    async click(selector) {
        const point = await this.evaluate(`(() => {
            const e = document.querySelector(${JSON.stringify(selector)});
            if (!e || e.disabled) throw Error('Button unavailable: ' + ${JSON.stringify(selector)});
            e.scrollIntoView({ block: 'center' });
            const r = e.getBoundingClientRect(), x = r.x + r.width / 2, y = r.y + r.height / 2;
            if (!e.contains(document.elementFromPoint(x, y))) throw Error('Button obscured');
            return { x, y };
        })()`);
        await this.command('Input.dispatchMouseEvent', { type: 'mousePressed', ...point, button: 'left', clickCount: 1 });
        await this.command('Input.dispatchMouseEvent', { type: 'mouseReleased', ...point, button: 'left', clickCount: 1 });
    }
    async clickText(container, text) {
        const index = await this.evaluate(`Array.from(document.querySelectorAll(${JSON.stringify(container + ' button')})).findIndex(e => e.textContent === ${JSON.stringify(text)})`);
        assert.notEqual(index, -1, 'Button not found: ' + text);
        // Mark the existing button only to obtain an unambiguous pointer target.
        await this.evaluate(`document.querySelectorAll(${JSON.stringify(container + ' button')})[${index}].setAttribute('data-browser-target', 'true')`);
        try { await this.click('[data-browser-target]'); }
        finally { await this.evaluate(`document.querySelector('[data-browser-target]')?.removeAttribute('data-browser-target')`); }
    }
    async screenshot(name) {
        await this.evaluate('scrollTo(0, 0)');
        const { cssContentSize } = await this.command('Page.getLayoutMetrics');
        const { data } = await this.command('Page.captureScreenshot', {
            format: 'png', captureBeyondViewport: true,
            clip: { x: 0, y: 0, width: cssContentSize.width, height: cssContentSize.height, scale: 1 }
        });
        await writeFile(join(this.browser.evidence, name + '.png'), Buffer.from(data, 'base64'));
    }
}
