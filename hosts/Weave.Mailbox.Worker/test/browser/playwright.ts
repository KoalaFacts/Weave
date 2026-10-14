import assert from 'node:assert/strict';
import { rm, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { chromium } from 'playwright';

const screenshotLimit = 33;

// Only fixed synthetic controls are used. Retain metadata, never headers or bodies.
export class BrowserFixture {
    pages = new Map();
    mobilePages = new WeakSet();
    sentMessages = new Map();
    pendingSendLoss = new Set();
    trace = [];
    errors = [];
    screenshots = 0;

    constructor(evidence, origin) { Object.assign(this, { evidence, origin }); }

    async start() {
        assert.equal(process.platform, 'linux', 'Browser verification requires the Linux namespace and seccomp sandbox.');
        try {
            this.browser = await chromium.launch({ channel: 'chromium', chromiumSandbox: true, timeout: 15000 });
            await writeFile(join(this.evidence, 'browser-version.json'), JSON.stringify({ version: this.browser.version(), channel: 'chromium', chromiumSandbox: true }, null, 2));
        } catch (error) {
            await writeFile(join(this.evidence, 'browser-startup.log'), String(error).slice(-32768));
            throw error;
        }
    }

    async page(name, path = '/', { viewport = { width: 1440, height: 1000 }, isMobile = false } = {}) {
        const context = await this.browser.newContext({ viewport, isMobile, hasTouch: isMobile, serviceWorkers: 'block', acceptDownloads: false });
        context.setDefaultTimeout(15000);
        context.setDefaultNavigationTimeout(15000);
        const messages = [];
        let page;
        await context.routeWebSocket('**/*', async socket => {
            this.recordError(name + ': blocked WebSocket');
            await socket.close();
        });
        await context.route('**/*', async route => {
            const request = route.request();
            const url = new URL(request.url());
            const entry = { page: name, method: request.method(), path: url.pathname.slice(0, 256) };
            this.trace.push(entry);
            this.trace.splice(0, Math.max(0, this.trace.length - 256));
            if (url.origin !== this.origin) {
                this.recordError(name + ': blocked unexpected origin');
                await route.abort('accessdenied');
                return;
            }
            let response;
            try {
                const send = request.method() === 'POST' && url.pathname.startsWith('/outbox/');
                if (send) {
                    assert.ok(messages.length < 10, 'Unexpected send loop.');
                    messages.push(request.postDataJSON());
                }
                // Fetch exactly once. Reject redirects so the allowlist also covers their targets.
                response = await route.fetch({ maxRedirects: 0, maxRetries: 0, timeout: 10000 });
                entry.status = response.status();
                if (entry.status >= 300 && entry.status < 400) {
                    this.recordError(name + ': blocked redirect');
                    await route.abort('accessdenied');
                } else if (send && this.pendingSendLoss.has(page)) {
                    assert.equal(entry.status, 200, 'Lost-response test requires a committed successful send.');
                    this.pendingSendLoss.delete(page);
                    await route.abort('connectionclosed');
                } else {
                    await route.fulfill({ response });
                }
            } catch (error) {
                this.recordError(name + ': request routing failed (' + error.name + ')');
                await route.abort('failed').catch(error => this.recordError(name + ': request abort failed (' + error.name + ')'));
            } finally { await response?.dispose(); }
        });
        page = await context.newPage();
        if (isMobile) this.mobilePages.add(page);
        this.pages.set(name, page);
        this.sentMessages.set(page, messages);
        page.on('pageerror', () => this.recordError(name + ': uncaught page exception'));
        await page.goto(path.startsWith('chrome:') ? path : this.origin + path, { waitUntil: 'load' });
        return page;
    }

    recordError(message) {
        this.errors.push(message.slice(0, 512));
        this.errors.splice(0, Math.max(0, this.errors.length - 64));
    }

    async screenshot(page, name) {
        const mobile = this.mobilePages.has(page);
        if (mobile) await rm(join(this.evidence, name + '-tiles.json'), { force: true });
        const measure = () => page.evaluate(() => ({ width: document.documentElement.scrollWidth, height: document.documentElement.scrollHeight, x: scrollX, y: scrollY }));
        const size = await measure();
        const viewport = page.viewportSize();
        const count = mobile ? Math.max(1, Math.ceil(size.height / viewport.height)) : 1;
        await writeFile(join(this.evidence, name + '-capture-plan.json'), JSON.stringify({
            mobile, viewport, document: { width: size.width, height: size.height },
            screenshotsBefore: this.screenshots, screenshotsRequired: count,
            screenshotsAfter: this.screenshots + count, screenshotLimit
        }, null, 2));
        assert.ok(size.width <= 2000 && size.height <= 20000, 'Page exceeded screenshot dimension budget.');
        if (!mobile) return this.captureScreenshot(page, name, true);

        // Chromium full-page capture reapplies WebPreferences and resets maxTouchPoints.
        // Viewport tiles preserve native mobile emulation and the original geometry.
        assert.ok(viewport.width <= 2000 && viewport.height <= 20000 && size.width <= viewport.width, 'Page exceeded screenshot dimension budget.');
        assert.ok(this.screenshots + count <= screenshotLimit, 'Screenshot count exceeded evidence budget before full mobile coverage.');
        const tiles = [];
        let captureError;
        try {
            for (let index = 0; index < count; index++) {
                const y = Math.min(index * viewport.height, Math.max(0, size.height - viewport.height));
                await page.evaluate(({ x, y }) => scrollTo({ left: x, top: y, behavior: 'instant' }), { x: 0, y });
                const file = name + '-tile-' + String(index + 1).padStart(2, '0');
                await this.captureScreenshot(page, file, false);
                const after = await measure();
                assert.deepEqual(after, { ...size, x: 0, y }, 'Page dimensions or scroll changed during screenshot; mobile coverage is incomplete.');
                tiles.push({ file: file + '.png', x: 0, y });
            }
        } catch (error) {
            captureError = error;
            throw error;
        } finally {
            try {
                await page.evaluate(({ x, y }) => scrollTo({ left: x, top: y, behavior: 'instant' }), { x: size.x, y: size.y });
            } catch (restoreError) {
                if (captureError) throw new AggregateError([captureError, restoreError], 'Mobile capture failed and its original scroll could not be restored.', { cause: captureError });
                throw restoreError;
            }
        }
        await writeFile(join(this.evidence, name + '-tiles.json'), JSON.stringify({ viewport, document: { width: size.width, height: size.height }, tiles }, null, 2));
    }

    async captureScreenshot(page, name, fullPage) {
        assert.ok(++this.screenshots <= screenshotLimit, 'Screenshot count exceeded evidence budget.');
        const png = await page.screenshot({ fullPage, timeout: 10000, mask: [page.locator('#control')] });
        assert.ok(png.length <= 8 * 1024 * 1024, 'Screenshot exceeded evidence byte budget.');
        await writeFile(join(this.evidence, name + '.png'), png);
    }

    async close() {
        try {
            if (this.browser?.isConnected()) {
                try { await Promise.all(this.browser.contexts().map(context => context.close())); }
                finally { await this.browser.close(); }
            }
        } finally {
            this.pendingSendLoss.clear();
            this.sentMessages.clear();
            await writeFile(join(this.evidence, 'network.json'), JSON.stringify(this.trace.slice(-256), null, 2));
            await writeFile(join(this.evidence, 'browser-errors.json'), JSON.stringify(this.errors.slice(-64), null, 2));
        }
    }
}
