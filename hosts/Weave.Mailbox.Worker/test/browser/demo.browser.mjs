import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { start } from '../harness.mjs';
import { BrowserFixture } from './playwright.mjs';

// Controls created by this UI test are usable only in its isolated temporary D1.
const publicLabel = 'Browser Bob <img src=x onerror=alert(1)>';
const privateLabel = 'Browser Alice private address';
const message = 'Browser acceptance message — 你好 🌿';

const text = (page, id) => page.locator('#' + id).textContent();
const waitText = (page, id, expected) => page.waitForFunction(([id, expected]) => document.getElementById(id).textContent.includes(expected), [id, expected]);

async function verifyLiteralArguments(page) {
    const id = 'probe-";globalThis.__weaveBrowserInjected=true;//\\\u2028\u2029';
    const value = '";globalThis.__weaveBrowserInjected=true;//\n</script><script>globalThis.__weaveBrowserInjected=true</script>\n` ${globalThis.__weaveBrowserInjected=true} \\ \u2028\u2029';
    const handle = await page.evaluateHandle(id => {
        const probe = document.createElement('textarea');
        probe.id = id;
        document.body.append(probe);
        return document.getElementById(id);
    }, id);
    try {
        await handle.asElement().fill(value);
        assert.equal(await page.evaluate(id => document.getElementById(id).value, id), value);
        assert.equal(await page.evaluate(() => globalThis.__weaveBrowserInjected), undefined);
    } finally {
        await handle.evaluate(probe => probe.remove());
        await handle.dispose();
    }
}

async function resume(page, control) {
    if (!await page.locator('#recovery').evaluate(element => element.open)) await page.locator('#recovery summary').click();
    await page.locator('#control').fill(control);
    await page.locator('#register').click();
    await waitText(page, 'status', 'Agent connected.');
    await page.locator('#recovery summary').click();
    return (await text(page, 'identity')).replace('Agent ID: ', '');
}

async function register(page, name) {
    await page.locator('#profileOptions summary').click();
    await page.locator('#displayName').fill(name);
    await page.locator('#description').fill(name + ' private profile');
    const registration = page.waitForRequest(request => new URL(request.url()).pathname === '/v1/accounts' && request.method() === 'POST');
    await page.locator('#createAccount').click();
    const control = await (await registration).headerValue('X-Weave-Mailbox-Control');
    await waitText(page, 'status', 'Agent connected.');
    assert.match(control, /^[0-9a-f]{64}$/);
    assert.equal(await text(page, 'addresses'), 'No addresses yet.');
    assert.equal(await page.locator('#credentialBackup').isVisible(), true);
    assert.equal(await page.locator('#createAccount').isDisabled(), true);
    assert.ok((await text(page, 'status')).includes('Back up your credential'));
    await page.locator('#profileOptions summary').click();
    return { id: (await text(page, 'identity')).replace('Agent ID: ', ''), control };
}

async function layout(fixture, page, name, width, height, mobile) {
    await page.setViewportSize({ width, height });
    const measurement = await page.evaluate(() => {
        const controls = [...document.querySelectorAll('input, textarea, button')];
        return {
            width: innerWidth, documentWidth: document.documentElement.scrollWidth,
            touch: navigator.maxTouchPoints > 0,
            grid: document.querySelector('.grid') ? getComputedStyle(document.querySelector('.grid')).display : null,
            clippedControls: controls.filter(e => { const r = e.getBoundingClientRect(); return r.width && (r.left < 0 || r.right > innerWidth + 1); }).map(e => e.id || e.textContent)
        };
    });
    assert.equal(measurement.width, width);
    assert.equal(measurement.touch, mobile, name + ': native touch capability must match the context.');
    assert.ok(measurement.documentWidth <= width + 1, JSON.stringify(measurement));
    assert.deepEqual(measurement.clippedControls, []);
    if (measurement.grid) assert.equal(measurement.grid, mobile ? 'block' : 'grid');
    await writeFile(join(fixture.evidence, name + '-layout.json'), JSON.stringify(measurement, null, 2));
    await fixture.screenshot(page, name);
}

test('real Worker UI: isolated sessions, public/private discovery, acceptance, recovery and ACK', { timeout: 180000 }, async () => {
    const evidence = resolve(process.env.WEAVE_BROWSER_EVIDENCE ?? 'test-results/browser');
    await mkdir(evidence, { recursive: true });
    await writeFile(join(evidence, 'result.json'), JSON.stringify({ status: 'running', checkpoints: [] }, null, 2));
    const root = await mkdtemp(join(tmpdir(), 'weave-worker-browser-'));
    const checkpoints = [];
    let h, fixture;
    try {
        h = await start({ persist: join(root, 'd1') });
        const origin = (await h.mf.ready).origin;
        assert.equal(new URL(origin).hostname, '127.0.0.1', 'Browser fixture must remain on loopback.');
        fixture = new BrowserFixture(evidence, origin);
        await fixture.start();
        const sandbox = await fixture.page('sandbox', 'chrome://sandbox/');
        const sandboxStatus = await sandbox.evaluate(() => document.body.innerText);
        await writeFile(join(evidence, 'sandbox.txt'), sandboxStatus.slice(0, 32768));
        assert.match(sandboxStatus, /(?:Namespace sandbox|PID namespaces)\s+Yes/);
        assert.match(sandboxStatus, /Seccomp-BPF sandbox\s+Yes/);
        checkpoints.push('Chrome namespace and seccomp sandbox reported enabled');

        const registry = await fixture.page('registry', '/registry');
        await waitText(registry, 'status', 'No current public addresses.');
        await fixture.screenshot(registry, 'registry-empty-desktop');
        const alice = await fixture.page('alice');
        const bobDesktop = await fixture.page('bob');
        let bob = bobDesktop;
        assert.notEqual(alice.context(), bob.context());
        await verifyLiteralArguments(alice);
        checkpoints.push('Playwright passes special characters in textarea ID and value literally without executing the injected marker');
        assert.equal(await h.db.prepare("SELECT count(*) n FROM mailbox_records WHERE kind='account'").first('n'), 0);
        for (const page of [alice, bob]) {
            assert.equal(await page.locator('#control').inputValue(), '');
            assert.equal(await page.locator('#credentialBackup').isVisible(), false);
        }
        const downloads = [];
        alice.on('download', download => downloads.push(download));
        await alice.locator('#recovery summary').click();
        await alice.locator('#control').fill('too-short');
        await alice.locator('#register').click();
        await waitText(alice, 'status', 'Control must be at least 64 characters.');
        await alice.locator('#recovery summary').click();
        const { id: aliceId, control: aliceControl } = await register(alice, 'Alice private display');
        const { id: bobId, control: bobControl } = await register(bob, 'Bob private display');
        assert.notEqual(aliceId, bobId);
        assert.notEqual(aliceControl, bobControl);
        assert.equal(downloads.length, 0, 'Registration must not start a credential download.');
        const downloadStarted = alice.waitForEvent('download');
        await alice.locator('#downloadCredential').click();
        const download = await downloadStarted;
        assert.equal(download.suggestedFilename(), 'weave-demo-credential.json');
        await download.cancel(); // The fixture deliberately keeps acceptDownloads:false.
        assert.equal(await alice.evaluate(() => localStorage.length + sessionStorage.length), 0);
        assert.equal(await bob.evaluate(() => localStorage.length + sessionStorage.length), 0);
        assert.equal(await h.db.prepare("SELECT count(*) n FROM mailbox_records WHERE kind='account'").first('n'), 2);
        checkpoints.push('Page load creates no account; two one-click registrations stay in memory with zero addresses; backup download requires a separate click');

        await alice.locator('#label').fill(privateLabel);
        await alice.locator('#createAddress').click();
        await waitText(alice, 'addresses', privateLabel);
        const privateId = (await h.ok('/v1/addresses', aliceControl)).items[0].addressId;
        await bob.locator('#label').fill(publicLabel);
        await bob.locator('#isPublic').click();
        await bob.locator('#addressDeveloper summary').click();
        await bob.locator('#directUri').fill('https://browser-metadata.invalid/never-fetch');
        await bob.locator('#addressDeveloper summary').click();
        await bob.locator('#createAddress').click();
        await waitText(bob, 'addresses', publicLabel);
        const publicId = (await h.ok('/v1/addresses', bobControl)).items[0].addressId;
        await registry.locator('#find').click();
        await waitText(registry, 'results', publicLabel);
        assert.equal(await registry.evaluate(() => document.querySelectorAll("#results article").length), 1);
        const publicText = await text(registry, 'results');
        for (const hidden of [privateLabel, privateId, 'Alice private display', 'Bob private display', 'private profile']) {
            assert.ok(!publicText.includes(hidden), 'Private data appeared in registry: ' + hidden);
        }
        assert.equal(await registry.evaluate(() => document.querySelectorAll("#results img, #results a").length), 0);
        await registry.locator('#address').fill(privateId);
        await registry.locator('#lookup').click();
        await waitText(registry, 'status', 'Public address unavailable.');
        assert.equal(await text(registry, 'results'), '');
        await registry.locator('#address').fill(publicId);
        await registry.locator('#lookup').click();
        await waitText(registry, 'results', publicLabel);
        await layout(fixture, registry, 'registry-public-desktop', 1440, 1000, false);
        const mobileOptions = { viewport: { width: 390, height: 844 }, isMobile: true };
        const registryMobile = await fixture.page('registry-mobile', '/registry', mobileOptions);
        await waitText(registryMobile, 'results', publicLabel);
        await registryMobile.locator('#address').fill(publicId);
        await registryMobile.locator('#lookup').tap();
        await waitText(registryMobile, 'status', 'Public addresses shown below.');
        await waitText(registryMobile, 'results', publicLabel);
        await layout(fixture, registryMobile, 'registry-public-mobile', 390, 844, true);
        checkpoints.push('Anonymous registry exposes only public address; private exact lookup unavailable, label and direct URI rendered as inert text');

        await alice.locator('#search').fill('Browser Bob');
        await alice.locator('#discover').click();
        await waitText(alice, 'directory', publicLabel);
        await alice.locator('#directory').getByRole('button', { name: 'Use address', exact: true }).click();
        assert.equal(await alice.evaluate(() => document.getElementById("addressId").value), publicId);
        await alice.locator('#intro').fill('Browser contact introduction');
        await alice.locator('#requestContact').click();
        await waitText(alice, 'status', 'Contact request submitted.');
        await alice.locator('#contacts').getByRole('button', { name: 'Select peer', exact: true }).click();
        assert.equal(await alice.evaluate(() => document.getElementById("peer").value), bobId);
        assert.equal(await alice.evaluate(() => [...document.querySelectorAll("#contacts button")].some(e => e.textContent === "accepted")), false);
        await alice.locator('#message').fill(message);
        await alice.locator('#send').click();
        await waitText(alice, 'status', 'Resource unavailable (404)');
        assert.equal(await alice.evaluate(() => document.getElementById("send").textContent), 'Send message');
        assert.equal(await alice.evaluate(() => document.getElementById("message").readOnly), false);
        await fixture.screenshot(alice, 'demo-before-acceptance-denied');
        await bob.locator('#loadContacts').click();
        await waitText(bob, 'contacts', 'pending');
        await bob.locator('#contacts').getByRole('button', { name: 'accepted', exact: true }).click();
        await bob.waitForFunction(() => document.querySelector('#contacts .row strong')?.textContent.startsWith('accepted'));
        await bob.locator('#contacts').getByRole('button', { name: 'Select peer', exact: true }).click();
        await bob.locator('#loadInbox').click();
        await waitText(bob, 'inbox', 'Browser contact introduction');
        await bob.locator('#inbox').getByRole('button', { name: 'Acknowledge', exact: true }).click();
        await waitText(bob, 'inbox', 'No pending messages.');
        checkpoints.push('Sender cannot accept own request or send before recipient acceptance; recipient accepted and acknowledged introduction');

        fixture.pendingSendLoss.add(alice);
        await alice.locator('#send').click();
        await waitText(alice, 'send', 'Retry same message');
        assert.equal(fixture.pendingSendLoss.has(alice), false, 'Response failure was injected after a real successful Worker send.');
        assert.equal(await alice.evaluate(() => document.getElementById("message").readOnly), true);
        await fixture.screenshot(alice, 'demo-unknown-send-recovery');
        await alice.locator('#send').click();
        await waitText(alice, 'status', 'Message sent.');
        assert.equal(fixture.sentMessages.get(alice).length, 3, 'Denied send, committed send with lost response, and retry');
        assert.deepEqual(fixture.sentMessages.get(alice)[1], fixture.sentMessages.get(alice)[2], 'Retry must preserve the exact envelope.');
        assert.equal(await alice.evaluate(() => document.getElementById("message").value), '');
        const messageId = fixture.sentMessages.get(alice)[1].payload.messageId;
        await bob.locator('#loadInbox').click();
        await waitText(bob, 'inbox', message);
        assert.equal(await bob.evaluate(() => document.querySelectorAll("#inbox .row").length), 1);
        // Reading again must not acknowledge or duplicate the pending body.
        await bob.locator('#loadInbox').click();
        await bob.waitForFunction(() => !document.getElementById('loadInbox').disabled);
        assert.equal(await bob.evaluate(() => document.querySelectorAll("#inbox .row").length), 1);
        assert.ok((await text(bob, 'inbox')).includes(message));
        await layout(fixture, bob, 'demo-inbox-desktop', 1440, 1000, false);
        const bobMobile = await fixture.page('bob-mobile', '/', mobileOptions);
        assert.notEqual(bobMobile.context(), bobDesktop.context());
        assert.equal(await resume(bobMobile, bobControl), bobId);
        await bobMobile.locator('#loadInbox').click();
        await waitText(bobMobile, 'inbox', message);
        assert.equal(await bobMobile.locator('#inbox .row').count(), 1);
        bob = bobMobile;
        await layout(fixture, bob, 'demo-inbox-mobile', 390, 844, true);
        // Exercise ACK with native mobile viewport and touch emulation active.
        assert.ok(await bob.evaluate(() => navigator.maxTouchPoints > 0), 'Mobile screenshot must retain native touch before ACK.');
        await bob.locator('#inbox').getByRole('button', { name: 'Acknowledge', exact: true }).tap();
        await waitText(bob, 'inbox', 'No pending messages.');
        await alice.locator('#loadOutbox').click();
        await alice.waitForFunction(messageId => JSON.parse(document.getElementById('outbox').textContent).some(r => r.messageId === messageId && r.state === 'acknowledged'), messageId);
        const receipt = (await h.ok('/v1/outbox', aliceControl)).items.filter(r => r.messageId === messageId);
        assert.equal(receipt.length, 1);
        assert.equal(receipt[0].state, 'acknowledged');
        const stored = await h.db.prepare("SELECT data FROM mailbox_records WHERE kind='message' AND owner=? AND id=?").bind(aliceId, messageId).first();
        assert.equal(JSON.parse(stored.data).payload, null, 'ACK must remove the stored pending body.');
        await layout(fixture, alice, 'demo-acknowledged-desktop', 1440, 1000, false);
        await layout(fixture, bob, 'demo-acknowledged-mobile', 390, 844, true);
        checkpoints.push('Lost successful response retried exact envelope once; one pending body; reads do not ACK; mobile ACK removed body and sender sees one acknowledged receipt');

        await alice.locator('#disconnect').click();
        await waitText(alice, 'identity', 'No agent connected.');
        assert.equal(await alice.evaluate(() => document.getElementById("control").value), '');
        assert.equal(await text(alice, 'outbox'), '');
        assert.equal(await text(bob, 'identity'), 'Agent ID: ' + bobId);
        assert.equal(await text(bobDesktop, 'identity'), 'Agent ID: ' + bobId);
        assert.equal(await resume(alice, aliceControl), aliceId);
        assert.equal((await h.ok('/v1/addresses', aliceControl)).items.length, 1);
        checkpoints.push('Clear session erases page state, does not affect peer context, and same control resumes same account');
        assert.deepEqual(fixture.errors, []);
    } catch (error) {
        for (const [name, page] of fixture?.pages ?? []) {
            if (name !== 'sandbox' && !page.isClosed()) await fixture.screenshot(page, 'failure-' + name)
                .catch(() => fixture.recordError('Failure screenshot: ' + name));
        }
        await writeFile(join(evidence, 'result.json'), JSON.stringify({ status: 'failed', checkpoints, error: String(error.stack).slice(-32768) }, null, 2));
        throw error;
    } finally {
        try {
            try { await fixture?.close(); }
            finally {
                try { await h?.close(); }
                finally { await rm(root, { recursive: true, force: true }); }
            }
        } catch (error) {
            await writeFile(join(evidence, 'result.json'), JSON.stringify({ status: 'failed', checkpoints, cleanupError: String(error.stack).slice(-32768) }, null, 2));
            throw error;
        }
    }
    assert.deepEqual(fixture.errors, []);
    await writeFile(join(evidence, 'result.json'), JSON.stringify({ status: 'passed', checkpoints }, null, 2));
});
