import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { start } from '../harness.ts';
import { agentKey } from '../agent-key.ts';
import { BrowserFixture } from './playwright.ts';

// Agent keys created by this UI test are registered only in its isolated temporary D1.
const publicLabel = 'Browser Bob <img src=x onerror=alert(1)>';
const privateLabel = 'Browser Alice private address';
const message = 'Browser acceptance message — 你好 🌿';
const ping = 'WEAVE-DEMO A to B: ping 001.';
const reply = 'WEAVE-DEMO B to A: pong 001.';

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

async function resume(page, keyFile) {
    if (!await page.locator('#recovery').evaluate(element => element.open)) await page.locator('#recovery summary').click();
    await page.locator('#agentKey').fill(keyFile);
    await page.locator('#register').click();
    await waitText(page, 'status', 'Account connected.');
    assert.equal(await page.locator('#workspace').isVisible(), true);
    return (await text(page, 'identity')).replace('Account ID: ', '');
}

async function register(page, name) {
    await page.locator('#profileOptions summary').click();
    await page.locator('#displayName').fill(name);
    await page.locator('#description').fill(name + ' private profile');
    const registration = page.waitForRequest(request => new URL(request.url()).pathname === '/v1/accounts' && request.method() === 'POST');
    await page.locator('#createAccount').click();
    const request = await registration;
    await waitText(page, 'status', 'Account connected.');
    assert.equal(await request.headerValue('X-Weave-Account-Control'), null);
    assert.equal(await request.headerValue('Authorization'), null);
    const proof = JSON.parse(Buffer.from((await request.headerValue('DPoP')).split('.')[0], 'base64url').toString());
    assert.equal(proof.typ, 'dpop+jwt');
    assert.deepEqual(Object.keys(proof.jwk).sort(), ['crv', 'kty', 'x']);
    assert.equal(await text(page, 'addresses'), 'No addresses yet. Create one to introduce yourself or receive a request.');
    assert.equal(await page.locator('#credentialBackup').isVisible(), true);
    assert.equal(await page.locator('#workspace').isVisible(), false);
    assert.equal(await page.locator('#accountStart').isVisible(), false);
    assert.ok((await text(page, 'status')).includes('Back up your agent key'));
    return { id: (await text(page, 'identity')).replace('Account ID: ', ''), x: proof.jwk.x };
}

// Downloads the page's key backup and returns it with a Node-side signer for server checks.
async function backup(page, id, x) {
    const downloadStarted = page.waitForEvent('download');
    await page.locator('#downloadCredential').click();
    const download = await downloadStarted;
    assert.equal(download.suggestedFilename(), 'weave-demo-agent-key.json');
    const keyFile = await readFile(await download.path(), 'utf8'), saved = JSON.parse(keyFile);
    assert.equal(saved.accountId, id); assert.equal(saved.key.x, x); assert.match(saved.key.d, /^[A-Za-z0-9_-]{43}$/);
    await download.delete();
    return { keyFile, agent: { accountId: id, key: agentKey(saved.key) } };
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
        const alice = await fixture.page('alice', '/', { acceptDownloads: true });
        const bobDesktop = await fixture.page('bob', '/', { acceptDownloads: true });
        let bob = bobDesktop;
        assert.notEqual(alice.context(), bob.context());
        await verifyLiteralArguments(alice);
        checkpoints.push('Playwright passes special characters in textarea ID and value literally without executing the injected marker');
        assert.equal(await h.db.prepare("SELECT count(*) n FROM accounts").first('n'), 0);
        for (const page of [alice, bob]) {
            assert.equal(await page.locator('#agentKey').inputValue(), '');
            assert.equal(await page.locator('#credentialBackup').isVisible(), false);
        }
        assert.equal(await alice.locator('#workspace').isVisible(), false);
        assert.equal(await alice.getByRole('button', { name: 'Create account', exact: true }).isVisible(), true);
        await layout(fixture, alice, 'demo-start-desktop', 1440, 1000, false);
        const startMobile = await fixture.page('start-mobile', '/', { viewport: { width: 390, height: 844 }, isMobile: true });
        await layout(fixture, startMobile, 'demo-start-mobile', 390, 844, true);
        await startMobile.context().close();
        await alice.locator('#browserGuide summary').click();
        assert.ok((await text(alice, 'browserGuide')).includes('acknowledged'));
        await fixture.screenshot(alice, 'demo-browser-walkthrough');
        await alice.locator('#browserGuide summary').click();
        await alice.locator('#agentGuide summary').click();
        const prompt = await alice.locator('#agentPromptA').inputValue();
        assert.match(prompt, /agent-start\.md/);
        assert.match(prompt, /Operate only your own account/);
        assert.match(prompt, /runId and replyTo/);
        assert.doesNotMatch(prompt, /ping 001|pong 001/);
        const promptOrigins = [...prompt.matchAll(/https?:\/\/\S+/g)].map(([url]) => new URL(url.replace(/\.$/, '')).origin);
        assert.deepEqual([...new Set(promptOrigins)], [origin]);
        assert.ok(!prompt.includes('[DEMO_ORIGIN]'));
        await alice.context().grantPermissions(['clipboard-read', 'clipboard-write'], { origin });
        await alice.locator('#copyPromptA').click();
        await waitText(alice, 'status', 'Agent A instruction copied.');
        assert.equal(await alice.evaluate(() => navigator.clipboard.readText()), prompt);
        await alice.locator('#copyPromptB').click();
        await waitText(alice, 'status', 'Agent B instruction copied.');
        assert.equal(await alice.evaluate(() => navigator.clipboard.readText()), await alice.locator('#agentPromptB').inputValue());
        await alice.context().clearPermissions();
        await alice.locator('#agentGuide summary').click();
        assert.equal(await h.db.prepare("SELECT count(*) n FROM accounts").first('n'), 0);
        const downloads = [];
        alice.on('download', download => downloads.push(download));
        await alice.locator('#recovery summary').click();
        await alice.locator('#agentKey').fill('not a saved key file');
        await alice.locator('#register').click();
        await waitText(alice, 'status', 'Paste the complete saved agent key file');
        assert.equal(await h.db.prepare("SELECT count(*) n FROM accounts").first('n'), 0);
        await alice.locator('#recovery summary').click();
        const { id: aliceId, x: aliceX } = await register(alice, 'Alice private display');
        const { id: bobId, x: bobX } = await register(bob, 'Bob private display');
        assert.notEqual(aliceId, bobId);
        assert.notEqual(aliceX, bobX);
        assert.equal(downloads.length, 0, 'Registration must not start a key download.');
        await fixture.screenshot(alice, 'demo-backup-before-continue');
        const { keyFile: aliceKeyFile, agent: aliceAgent } = await backup(alice, aliceId, aliceX);
        const { keyFile: bobKeyFile, agent: bobAgent } = await backup(bob, bobId, bobX);
        assert.equal(await alice.evaluate(() => localStorage.length + sessionStorage.length), 0);
        assert.equal(await bob.evaluate(() => localStorage.length + sessionStorage.length), 0);
        assert.equal(await h.db.prepare("SELECT count(*) n FROM accounts").first('n'), 2);
        checkpoints.push('Page load creates no account; two one-click key registrations send only DPoP proofs and keep keys in memory with zero addresses; key backup download requires a separate click');

        for (const page of [alice, bob]) {
            await page.locator('#continueToContacts').click();
            assert.equal(await page.locator('#workspace').isVisible(), true);
            await page.locator('#addressScenario > summary').click();
        }
        await alice.locator('#label').fill(privateLabel);
        await alice.locator('#createAddress').click();
        await waitText(alice, 'addresses', privateLabel);
        const privateId = (await h.ok('/v1/addresses',aliceAgent)).items[0].addressId;
        await bob.locator('#label').fill(publicLabel);
        await bob.locator('#isPublic').click();
        await bob.locator('#addressDeveloper summary').click();
        await bob.locator('#directUri').fill('https://browser-metadata.invalid/never-fetch');
        await bob.locator('#addressDeveloper summary').click();
        await bob.locator('#createAddress').click();
        await waitText(bob, 'addresses', publicLabel);
        const publicId = (await h.ok('/v1/addresses',bobAgent)).items[0].addressId;
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

        await alice.locator('#discoverScenario > summary').click();
        await alice.locator('#search').fill('Browser Bob');
        await alice.locator('#discover').click();
        await waitText(alice, 'directory', publicLabel);
        await alice.locator('#directory').getByRole('button', { name: 'Use address', exact: true }).click();
        assert.equal(await alice.evaluate(() => document.getElementById("addressId").value), publicId);
        await alice.locator('#intro').fill('WEAVE-DEMO contact A to B.');
        await alice.locator('#requestContact').click();
        await waitText(alice, 'status', 'Contact request submitted.');
        await alice.locator('#contacts').getByRole('button', { name: 'Open conversation', exact: true }).click();
        assert.equal(await alice.evaluate(() => document.getElementById("peer").value), bobId);
        assert.equal(await alice.evaluate(() => [...document.querySelectorAll("#contacts button")].some(e => e.textContent === "Accept contact")), false);
        await alice.locator('#message').fill(message);
        await alice.locator('#send').click();
        await waitText(alice, 'status', 'Resource unavailable (404)');
        assert.equal(await alice.evaluate(() => document.getElementById("send").textContent), 'Send message');
        assert.equal(await alice.evaluate(() => document.getElementById("message").readOnly), false);
        assert.equal(fixture.sentMessages.get(alice).length, 0, 'Missing reciprocal contact must prevent message dispatch.');
        await fixture.screenshot(alice, 'demo-before-acceptance-denied');
        await bob.locator('#contactScenario > summary').click();
        await bob.locator('#loadContacts').click();
        await waitText(bob, 'contacts', 'pending');
        await waitText(bob, 'contacts', 'WEAVE-DEMO contact A to B.');
        assert.ok((await text(bob, 'contacts')).includes(aliceId));
        assert.equal(await bob.locator('#mailboxScenario').evaluate(el => el.open), false);
        assert.equal((await h.ok('/v1/inbox',bobAgent)).items.length, 1, 'Reading the inline introduction must not ACK it.');
        await fixture.screenshot(bob, 'demo-incoming-introduction');
        await bob.locator('#contacts').getByRole('button', { name: 'Accept contact', exact: true }).click();
        await bob.waitForFunction(() => document.querySelector('#contacts .row strong')?.textContent.startsWith('accepted'));
        await bob.locator('#contacts').getByRole('button', { name: 'Open conversation', exact: true }).click();
        await bob.locator('#loadInbox').click();
        await waitText(bob, 'inbox', 'WEAVE-DEMO contact A to B.');
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
        assert.equal(fixture.sentMessages.get(alice).length, 2, 'Committed send with lost response and exact retry');
        assert.deepEqual(fixture.sentMessages.get(alice)[0], fixture.sentMessages.get(alice)[1], 'Retry must preserve the exact envelope.');
        assert.equal(await alice.evaluate(() => document.getElementById("message").value), '');
        const messageId = fixture.sentMessages.get(alice)[0].payload.messageId;
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
        assert.equal(await resume(bobMobile, bobKeyFile), bobId);
        await bobMobile.locator('#mailboxScenario > summary').click();
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
        const receipt = (await h.ok('/v1/outbox',aliceAgent)).items.filter(r => r.messageId === messageId);
        assert.equal(receipt.length, 1);
        assert.equal(receipt[0].state, 'acknowledged');
        const stored = await h.db.prepare("SELECT payload FROM mailbox_records WHERE sender_account_id=? AND message_id=?").bind(aliceId, messageId).first();
        assert.equal(stored.payload, null, 'ACK must remove the stored pending body.');
        await layout(fixture, alice, 'demo-acknowledged-desktop', 1440, 1000, false);
        await layout(fixture, bob, 'demo-acknowledged-mobile', 390, 844, true);
        checkpoints.push('Lost successful response retried exact envelope once; one pending body; reads do not ACK; mobile ACK removed body and sender sees one acknowledged receipt');

        await alice.locator('#message').fill(ping);
        await alice.locator('#send').click();
        await waitText(alice, 'status', 'Message sent.');
        const pingId = fixture.sentMessages.get(alice).at(-1).payload.messageId;
        await bob.locator('#loadInbox').tap();
        await waitText(bob, 'inbox', ping);
        assert.equal(await bob.locator('#inbox .row').count(), 1);
        await bob.locator('#inbox').getByRole('button', { name: 'Acknowledge', exact: true }).tap();
        await waitText(bob, 'inbox', 'No pending messages.');
        await alice.locator('#loadOutbox').click();
        await alice.waitForFunction(id => JSON.parse(document.getElementById('outbox').textContent).some(r => r.messageId === id && r.state === 'acknowledged'), pingId);
        checkpoints.push('Walkthrough ping reached B and B explicitly ACKed; A sees the exact ping receipt acknowledged');

        await bob.locator('#contactScenario > summary').click();
        await bob.locator('#loadContacts').click();
        await waitText(bob, 'contacts', 'accepted');
        await bob.locator('#contacts').getByRole('button', { name: 'Open conversation', exact: true }).click();
        await bob.locator('#message').fill(reply);
        await bob.locator('#send').tap();
        await waitText(bob, 'status', 'Message sent.');
        const replyId = fixture.sentMessages.get(bob).at(-1).payload.messageId;
        await alice.locator('#loadInbox').click();
        await waitText(alice, 'inbox', reply);
        assert.equal(await alice.locator('#inbox .row').count(), 1);
        await alice.locator('#inbox').getByRole('button', { name: 'Acknowledge', exact: true }).click();
        await waitText(alice, 'inbox', 'No pending messages.');
        await bob.locator('#loadOutbox').tap();
        await bob.waitForFunction(id => JSON.parse(document.getElementById('outbox').textContent).some(r => r.messageId === id && r.state === 'acknowledged'), replyId);
        assert.equal((await h.ok('/v1/outbox/' + replyId,bobAgent)).state, 'acknowledged');
        await layout(fixture, bob, 'demo-reply-acknowledged-mobile', 390, 844, true);
        checkpoints.push('B replied with the walkthrough pong; A read and ACKed it; B sees the exact pong receipt acknowledged');

        await alice.locator('#disconnect').click();
        await waitText(alice, 'identity', 'No account connected.');
        assert.equal(await alice.evaluate(() => document.getElementById("agentKey").value), '');
        assert.equal(await text(alice, 'outbox'), '');
        assert.equal(await text(bob, 'identity'), 'Account ID: ' + bobId);
        assert.equal(await text(bobDesktop, 'identity'), 'Account ID: ' + bobId);
        assert.equal(await resume(alice, aliceKeyFile), aliceId);
        assert.equal((await h.ok('/v1/addresses',aliceAgent)).items.length, 1);
        checkpoints.push('Clear session erases page state, does not affect peer context, and the downloaded key file resumes the same account');
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