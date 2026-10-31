import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';

const entry = readFileSync(new URL('../src/agent-entry.html', import.meta.url), 'utf8');
const guide = readFileSync(new URL('../src/agent-start.md', import.meta.url), 'utf8');
const addressFields = ['requesterAddressId', 'recipientAddressId'];
const sections = [
    ['agent entry', entry.split('<details id="invitationBuilder">')[1]?.split('<button id="loadInvitationAddresses"')[0]],
    ['canonical guide', guide.split('### 2.4 Exchange non-secret contact information')[1]?.split('B prepares')[0]]
];
for (const [name, section] of sections) test(name + ' prepares both owned addresses before requesting contact', () => {
    assert.ok(section, 'Address preparation section must exist');
    const text = section.replace(/\s+/g, ' ');
    for (const field of addressFields) {
        assert.ok(text.includes(field), name + ' must explain required ' + field);
    }
    assert.match(text, /Both A and B need a currently effective address belonging to their own account/);
    assert.match(text, /list_addresses.*first.*create an address only if necessary and authorized/i);
    assert.match(text, /private by default/);
    assert.match(text, /B.*shares its receiving address.*recipientAddressId/);
    assert.match(text, /A uses its own (?:<code>requesterAddressId<\/code>|`requesterAddressId`)/);
    assert.match(text, /creation times out.*reconcile the owned list before another create/);
    assert.doesNotMatch(text, /A (?:does not need|needs no) (?:an )?address/);
});

test('raw Agent A textarea prepares its owned address before using A_ADDRESS_ID', () => {
    const template = readFileSync(new URL('../src/demo.html', import.meta.url), 'utf8');
    const prompt = /<textarea id="agentPromptA"[^>]*>([\s\S]*?)<\/textarea>/.exec(template)?.[1];
    assert.ok(prompt, 'Original Agent A textarea must exist');
    const preparation = prompt.split('\n1. ')[1]?.split('\n2. ')[0];
    assert.ok(preparation, 'Address preparation must precede the contact request');
    assert.match(preparation, /GET \/v1\/addresses first/);
    assert.match(preparation, /currently effective address belonging to A's account/);
    assert.match(preparation, /create an address only if necessary and authorized/);
    assert.match(preparation, /POST \/v1\/addresses.*"isPublic":false/);
    assert.match(preparation, /Set A_ADDRESS_ID to the actual addressId/);
    assert.match(preparation, /creation times out.*reconcile the owned list before another create/);
    assert.match(prompt, /requesterAddressId=A_ADDRESS_ID/);
    assert.match(prompt, /recipientAddressId=B_ADDRESS_ID/);
});
