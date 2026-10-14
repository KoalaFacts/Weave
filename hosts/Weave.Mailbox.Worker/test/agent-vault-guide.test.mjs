import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { tools, valid, operation } from '../../../examples/mailbox-demo-mcp/tools.mjs';
import { start } from './harness.mjs';
import { mcpClient, value } from './mcp-client.mjs';

const guide = readFileSync(new URL('../src/agent-start.md', import.meta.url), 'utf8');
test('existing-account diagnosis uses direct reads without restore, registration or ACK', () => {
    const diagnosis=guide.split('### Read-only diagnosis of an existing account')[1]?.split('## 2.')[0] ?? '';
    assert.match(diagnosis,/GET \/v1\/contacts\/requests/);
    assert.match(diagnosis,/GET \/v1\/inbox/);
    assert.match(diagnosis,/Do not click Restore account or call `POST \/v1\/accounts` as a prerequisite/);
    assert.match(diagnosis,/Reading does not accept a contact or ACK a message/);
    assert.match(diagnosis,/secure secret injection/);
    assert.match(diagnosis,/401 or 404/);
    assert.match(diagnosis,/not proof.*saved in a vault/);
});
test('vault-first guide gates account creation on real storage and injection capabilities', () => {
    const steps = ['### 2.1 Verify vault and secret injection capabilities', '### 2.2 Save before registering', '### 2.3 Verify recovery without silently replacing identity'];
    let previous = -1;
    for (const step of steps) {
        const position = guide.indexOf(step);
        assert.ok(position > previous, 'Missing or out-of-order step: ' + step); previous = position;
    }
    const gate = guide.slice(guide.indexOf(steps[0]), guide.indexOf(steps[1]));
    for (const term of ['not every agent has a vault', 'no universal vault API', 'secure_vault', 'secure_secret_injection', 'needs_owner_action', 'required confirmations']) assert.ok(gate.includes(term), term);
    assert.match(gate, /downloaded JSON.*not a vault/i);
    assert.match(gate, /Do not generate a control or register/);
    assert.match(guide, /chat, logs, repositories, ordinary notes, plaintext files/);
});
test('registration contract in the vault guide matches HTTP and MCP without creating a credential', () => {
    const contract = JSON.parse(readFileSync(new URL('../src/openapi.json', import.meta.url), 'utf8'));
    const security = contract.components.securitySchemes.AccountControl;
    assert.equal(security.in, 'header'); assert.equal(security.name, 'X-Weave-Account-Control');
    assert.match(security.description, /at least 64 characters/);
    assert.deepEqual(contract.components.schemas.accountSubmission.required ?? [], []);
    assert.ok(contract.paths['/v1/accounts'].post);
    assert.ok(valid({}, tools.find(tool => tool.name === 'register_account').inputSchema));
    assert.deepEqual(operation('register_account', {}).slice(0, 3), ['POST', '/v1/accounts', {}]);
    assert.match(guide, /POST \/v1\/accounts/); assert.match(guide, /JSON body `\{\}`/);
    assert.match(guide, /original control value.*X-Weave-Account-Control/);
    assert.match(guide, /at least 64 characters/);
});
test('vault guide distinguishes durable saving, registration and restoration evidence', () => {
    for (const term of ['registrationStatus', 'vaultPersistenceStatus', 'recoveryVerificationStatus', 'vault reference', 'service origin', 'accountId', 'fresh session', 'different Account ID', 'Do not overwrite']) assert.ok(guide.includes(term), term);
    assert.match(guide, /401.*not proof.*demo clearing/);
    assert.match(guide, /do not automatically call registration/i);
    assert.match(guide, /same saved control/);
});
test('MCP README conditions reconnect on an existing account and follows canonical recovery', () => {
    const readme = readFileSync(new URL('../../../examples/mailbox-demo-mcp/README.md', import.meta.url), 'utf8');
    const flow = readme.split('## Actual tool flow')[1].split('2. Call')[0];
    assert.match(flow, /vault prerequisites/);
    assert.match(flow, /existing binding.*canonical recovery/);
    assert.match(flow, /only while the account still exists/);
    assert.match(flow, /Do not silently replace/);
});
test('anonymous HTTP, page prompts and MCP share the vault-first guide without account operations', async () => {
    const h = await start();
    const client = mcpClient(String(await h.mf.ready));
    try {
        assert.equal(await (await h.request('/agent-start.md')).text(), guide);
        const html = await (await h.request('/')).text();
        for (const role of ['A', 'B']) {
            const prompt = new RegExp('<textarea id="agentPrompt' + role + '"[^>]*>([\\s\\S]*?)</textarea>').exec(html)[1];
            assert.match(prompt, /vault-first/); assert.match(prompt, /secure secret injection/);
            assert.match(prompt, /agent-start\.md/);
        }
        assert.match(html, /downloaded JSON.*not a vault/i);
        await client.initialize(); const result = value(await client.call('demo_guide'));
        assert.equal(result.instructions, guide); assert.equal(result.readiness.credentialConfigured, false);
        assert.match(result.credentials, /vault-first/);
        assert.equal(await h.db.prepare("SELECT count(*) n FROM accounts").first('n'), 0);
    } finally { try { await client.close(); } finally { await h.close(); } }
});
