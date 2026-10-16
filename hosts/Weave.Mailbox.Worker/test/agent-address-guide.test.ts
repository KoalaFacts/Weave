import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { tools, valid } from '../../../examples/mailbox-demo-mcp/tools.ts';

const requestSchema = tools.find(tool => tool.name === 'request_contact').inputSchema;
const addressFields = ['requesterAddressId', 'recipientAddressId'];

test('canonical MCP contact requests require both agents addresses', () => {
    const id = '01991341-0000-7000-8000-000000000001';
    const request = {
        requesterAccountId: id, requesterAddressId: id,
        recipientAccountId: '01991341-0000-7000-8000-000000000002',
        recipientAddressId: '01991341-0000-7000-8000-000000000003',
        payload: { messageId: id, createdAt: '2026-10-16T00:00:00Z', expiresAt: '2026-10-16T01:00:00Z', payloadEncoding: 'text/plain', bytes: 'aGk=' }
    };
    assert.ok(valid(request, requestSchema), 'Complete canonical request must satisfy the actual tool schema');
    for (const field of addressFields) {
        assert.ok(requestSchema.required.includes(field), field + ' must remain required');
        const missing = { ...request }; delete missing[field];
        assert.equal(valid(missing, requestSchema), false, 'Missing ' + field + ' must be rejected');
    }
});

const entry = readFileSync(new URL('../src/agent-entry.html', import.meta.url), 'utf8');
const guide = readFileSync(new URL('../src/agent-start.md', import.meta.url), 'utf8');
const sections = [
    ['agent entry', entry.split('<details id="invitationBuilder">')[1]?.split('<button id="loadInvitationAddresses"')[0]],
    ['canonical guide', guide.split('### 2.4 Exchange non-secret contact information')[1]?.split('B prepares')[0]]
];
for (const [name, section] of sections) test(name + ' prepares both owned addresses before requesting contact', () => {
    assert.ok(section, 'Address preparation section must exist');
    const text = section.replace(/<[^>]+>/g, '').replaceAll('`', '').replace(/\s+/g, ' ');
    for (const field of addressFields) {
        assert.ok(requestSchema.required.includes(field), field + ' must remain required');
        assert.ok(text.includes(field), name + ' must explain required ' + field);
    }
    assert.match(text, /Both A and B need a currently effective address belonging to their own account/);
    assert.match(text, /list_addresses.*first.*create an address only if necessary and authorized/i);
    assert.match(text, /private by default/);
    assert.match(text, /B.*shares its receiving address.*recipientAddressId/);
    assert.match(text, /A uses its own requesterAddressId/);
    assert.match(text, /creation times out.*reconcile the owned list before another create/);
    assert.doesNotMatch(text, /A (?:does not need|needs no) (?:an )?address/);
});
