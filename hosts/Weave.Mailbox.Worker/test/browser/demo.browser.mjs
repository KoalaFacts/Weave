import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { start } from '../harness.mjs';
import { Chrome } from './chrome.mjs';

// Deliberately public test controls, usable only in this isolated temporary D1.
const aliceControl = 'browser-local-alice-'.padEnd(64, 'a');
const bobControl = 'browser-local-bob-'.padEnd(64, 'b');
const publicLabel = 'Browser Bob <img src=x onerror=alert(1)>';
const privateLabel = 'Browser Alice private address';
const message = 'Browser acceptance message — 你好 🌿';

const text = (page, id) => page.evaluate(`document.getElementById(${JSON.stringify(id)}).textContent`);
const waitText = (page, id, expected) => page.wait(`document.getElementById(${JSON.stringify(id)}).textContent.includes(${JSON.stringify(expected)})`);

async function register(page, control, name, firstRegistration = true) {
    await page.fill('control', control);
    await page.fill('displayName', name);
    await page.fill('description', name + ' private profile');
    await page.click('#register');
    await waitText(page, 'status', 'Agent connected.');
    if (firstRegistration) assert.equal(await text(page, 'addresses'), 'No addresses yet.');
    return (await text(page, 'identity')).replace('Agent ID: ', '');
}

async function layout(page, name, width, height, mobile) {
    await page.resize(width, height, mobile);
    const measurement = await page.evaluate(`(() => {
        const controls = [...document.querySelectorAll('input, textarea, button')];
        return {
            width: innerWidth, documentWidth: document.documentElement.scrollWidth,
            grid: document.querySelector('.grid') ? getComputedStyle(document.querySelector('.grid')).display : null,
            clippedControls: controls.filter(e => { const r = e.getBoundingClientRect(); return r.width && (r.left < 0 || r.right > innerWidth + 1); }).map(e => e.id || e.textContent)
        };
    })()`);
    assert.equal(measurement.width, width);
    assert.ok(measurement.documentWidth <= width + 1, JSON.stringify(measurement));
    assert.deepEqual(measurement.clippedControls, []);
    if (measurement.grid) assert.equal(measurement.grid, mobile ? 'block' : 'grid');
    await writeFile(join(page.browser.evidence, name + '-layout.json'), JSON.stringify(measurement, null, 2));
    await page.screenshot(name);
}

test('real Worker UI: isolated sessions, public/private discovery, acceptance, recovery and ACK', { timeout: 180000 }, async () => {
    const evidence = resolve(process.env.WEAVE_BROWSER_EVIDENCE ?? 'test-results/browser');
    await mkdir(evidence, { recursive: true });
    const root = await mkdtemp(join(tmpdir(), 'weave-worker-browser-'));
    const checkpoints = [];
    let h, chrome;
    try {
        h = await start({ persist: join(root, 'd1') });
        const origin = (await h.mf.ready).origin;
        assert.equal(new URL(origin).hostname, '127.0.0.1', 'Browser fixture must remain on loopback.');
        chrome = new Chrome(root, evidence, origin);
        await chrome.start();
        const sandbox = await chrome.page('sandbox', 'chrome://sandbox/');
        const sandboxStatus = await sandbox.evaluate('document.body.innerText');
        await writeFile(join(evidence, 'sandbox.txt'), sandboxStatus);
        assert.match(sandboxStatus, /(?:Namespace sandbox|PID namespaces)\s+Yes/);
        assert.match(sandboxStatus, /Seccomp-BPF sandbox\s+Yes/);
        checkpoints.push('Chrome namespace and seccomp sandbox reported enabled');

        const registry = await chrome.page('registry', '/registry');
        await waitText(registry, 'status', 'No current public addresses.');
        await registry.screenshot('registry-empty-desktop');
        const alice = await chrome.page('alice');
        const bob = await chrome.page('bob');
        await alice.fill('control', 'too-short');
        await alice.click('#register');
        await waitText(alice, 'status', 'Control must be at least 64 characters.');
        const aliceId = await register(alice, aliceControl, 'Alice private display');
        const bobId = await register(bob, bobControl, 'Bob private display');
        assert.notEqual(aliceId, bobId);
        assert.equal(await alice.evaluate('localStorage.length + sessionStorage.length'), 0);
        assert.equal(await bob.evaluate('localStorage.length + sessionStorage.length'), 0);
        checkpoints.push('Two independent browser contexts registered through UI; invalid-control recovery and empty addresses verified');

        await alice.fill('label', privateLabel);
        await alice.click('#createAddress');
        await waitText(alice, 'addresses', privateLabel);
        const privateId = (await h.ok('/v1/addresses', aliceControl)).items[0].addressId;
        await bob.fill('label', publicLabel);
        await bob.click('#isPublic');
        await bob.fill('directUri', 'https://browser-metadata.invalid/never-fetch');
        await bob.click('#createAddress');
        await waitText(bob, 'addresses', publicLabel);
        const publicId = (await h.ok('/v1/addresses', bobControl)).items[0].addressId;
        await registry.click('#find');
        await waitText(registry, 'results', publicLabel);
        assert.equal(await registry.evaluate('document.querySelectorAll("#results article").length'), 1);
        const publicText = await text(registry, 'results');
        for (const hidden of [privateLabel, privateId, 'Alice private display', 'Bob private display', 'private profile']) {
            assert.ok(!publicText.includes(hidden), 'Private data appeared in registry: ' + hidden);
        }
        assert.equal(await registry.evaluate('document.querySelectorAll("#results img, #results a").length'), 0);
        await registry.fill('address', privateId);
        await registry.click('#lookup');
        await waitText(registry, 'status', 'Public address unavailable.');
        assert.equal(await text(registry, 'results'), '');
        await registry.fill('address', publicId);
        await registry.click('#lookup');
        await waitText(registry, 'results', publicLabel);
        await layout(registry, 'registry-public-desktop', 1440, 1000, false);
        await layout(registry, 'registry-public-mobile', 390, 844, true);
        checkpoints.push('Anonymous registry exposes only public address; private exact lookup unavailable, label and direct URI rendered as inert text');

        await alice.fill('search', 'Browser Bob');
        await alice.click('#discover');
        await waitText(alice, 'directory', publicLabel);
        await alice.clickText('#directory', 'Use address');
        assert.equal(await alice.evaluate('document.getElementById("addressId").value'), publicId);
        await alice.fill('intro', 'Browser contact introduction');
        await alice.click('#requestContact');
        await waitText(alice, 'status', 'Contact request submitted.');
        await alice.clickText('#contacts', 'Select peer');
        assert.equal(await alice.evaluate('document.getElementById("peer").value'), bobId);
        assert.equal(await alice.evaluate('[...document.querySelectorAll("#contacts button")].some(e => e.textContent === "accepted")'), false);
        await alice.fill('message', message);
        await alice.click('#send');
        await waitText(alice, 'status', 'Resource unavailable (404)');
        assert.equal(await alice.evaluate('document.getElementById("send").textContent'), 'Send message');
        assert.equal(await alice.evaluate('document.getElementById("message").readOnly'), false);
        await alice.screenshot('demo-before-acceptance-denied');
        await bob.click('#loadContacts');
        await waitText(bob, 'contacts', 'pending');
        await bob.clickText('#contacts', 'accepted');
        await bob.wait('document.querySelector("#contacts .row strong")?.textContent.startsWith("accepted")');
        await bob.clickText('#contacts', 'Select peer');
        await bob.click('#loadInbox');
        await waitText(bob, 'inbox', 'Browser contact introduction');
        await bob.clickText('#inbox', 'Acknowledge');
        await waitText(bob, 'inbox', 'No pending messages.');
        checkpoints.push('Sender cannot accept own request or send before recipient acceptance; recipient accepted and acknowledged introduction');

        alice.failNextSendResponse = true;
        await alice.click('#send');
        await waitText(alice, 'send', 'Retry same message');
        assert.equal(alice.failNextSendResponse, false, 'Response failure was injected after a real successful Worker send.');
        assert.equal(await alice.evaluate('document.getElementById("message").readOnly'), true);
        await alice.screenshot('demo-unknown-send-recovery');
        await alice.click('#send');
        await waitText(alice, 'status', 'Message sent.');
        assert.equal(alice.sentMessages.length, 3, 'Denied send, committed send with lost response, and retry');
        assert.deepEqual(alice.sentMessages[1], alice.sentMessages[2], 'Retry must preserve the exact envelope.');
        assert.equal(await alice.evaluate('document.getElementById("message").value'), '');
        const messageId = alice.sentMessages[1].payload.messageId;
        await bob.click('#loadInbox');
        await waitText(bob, 'inbox', message);
        assert.equal(await bob.evaluate('document.querySelectorAll("#inbox .row").length'), 1);
        // Reading again must not acknowledge or duplicate the pending body.
        await bob.click('#loadInbox');
        await bob.wait('!document.getElementById("loadInbox").disabled');
        assert.equal(await bob.evaluate('document.querySelectorAll("#inbox .row").length'), 1);
        assert.ok((await text(bob, 'inbox')).includes(message));
        await layout(bob, 'demo-inbox-desktop', 1440, 1000, false);
        await layout(bob, 'demo-inbox-mobile', 390, 844, true);
        // Exercise the ACK button with the mobile layout active.
        await bob.clickText('#inbox', 'Acknowledge');
        await waitText(bob, 'inbox', 'No pending messages.');
        await alice.click('#loadOutbox');
        await alice.wait(`JSON.parse(document.getElementById('outbox').textContent).some(r => r.messageId === ${JSON.stringify(messageId)} && r.state === 'acknowledged')`);
        const receipt = (await h.ok('/v1/outbox', aliceControl)).items.filter(r => r.messageId === messageId);
        assert.equal(receipt.length, 1);
        assert.equal(receipt[0].state, 'acknowledged');
        const stored = await h.db.prepare("SELECT data FROM mailbox_records WHERE kind='message' AND owner=? AND id=?").bind(aliceId, messageId).first();
        assert.equal(JSON.parse(stored.data).payload, null, 'ACK must remove the stored pending body.');
        await layout(alice, 'demo-acknowledged-desktop', 1440, 1000, false);
        await layout(bob, 'demo-acknowledged-mobile', 390, 844, true);
        checkpoints.push('Lost successful response retried exact envelope once; one pending body; reads do not ACK; mobile ACK removed body and sender sees one acknowledged receipt');

        await alice.click('#disconnect');
        await waitText(alice, 'identity', 'No agent connected.');
        assert.equal(await alice.evaluate('document.getElementById("control").value'), '');
        assert.equal(await text(alice, 'outbox'), '');
        assert.equal(await text(bob, 'identity'), 'Agent ID: ' + bobId);
        assert.equal(await register(alice, aliceControl, 'Alice private display', false), aliceId);
        assert.equal((await h.ok('/v1/addresses', aliceControl)).items.length, 1);
        checkpoints.push('Clear session erases page state, does not affect peer context, and same control resumes same account');
        assert.deepEqual(chrome.errors, []);
        await writeFile(join(evidence, 'result.json'), JSON.stringify({ status: 'passed', checkpoints }, null, 2));
    } catch (error) {
        for (const page of chrome?.pages ?? []) {
            if (page.name !== 'sandbox') await page.screenshot('failure-' + page.name)
                .catch(captureError => chrome.errors.push('Failure screenshot ' + page.name + ': ' + captureError.message));
        }
        await writeFile(join(evidence, 'result.json'), JSON.stringify({ status: 'failed', checkpoints, error: error.stack }, null, 2));
        throw error;
    } finally {
        try { await chrome?.close(); }
        finally {
            try { await h?.close(); }
            finally { await rm(root, { recursive: true, force: true }); }
        }
    }
});
