import assert from 'node:assert/strict';
import { test } from 'node:test';
import { start, secret } from './harness.ts';
import { mcpClient, value } from './mcp-client.ts';
test('real stdio MCP discovers account and contact workflow without side effects or credential disclosure',async()=>{
    const h=await start(),control=secret(),client=mcpClient(String(await h.mf.ready),control);
    try{
        const before=await client.request('tools/list');assert.equal(before.error.code,-32002);
        const init=await client.initialize();assert.equal(init.result.protocolVersion,'2024-11-05');
        const tools=(await client.request('tools/list')).result.tools;
        for(const name of ['demo_guide','search_registry','lookup_address','register_account','create_address','prepare_payload','request_contact','decide_contact','send_message','read_inbox','read_outbox','ack_message'])assert.ok(tools.some(x=>x.name===name),name);
        const guide=value(await client.call('demo_guide'));assert.ok(guide.boundary.includes('does not grant permission'));
        assert.equal(await h.db.prepare("SELECT count(*) n FROM accounts").first('n'),0);
        assert.ok(!client.logs.includes(control));
    }finally{await client.close();await h.close();}
});
test('two real MCP processes register, discover, accept, send, replay and ACK through actual Worker D1',async()=>{
    const h=await start(),aControl=secret(),bControl=secret(),a=mcpClient(String(await h.mf.ready),aControl),b=mcpClient(String(await h.mf.ready),bControl);
    try{
        await a.initialize();await b.initialize();
        const alice=value(await a.call('register_account',{displayName:'Private Alice'})),bob=value(await b.call('register_account',{}));
        assert.equal(value(await a.call('list_addresses')).items.length,0);
        const publicAddress=value(await b.call('create_address',{label:'Public Bob',isPublic:true}));
        const privateAddress=value(await b.call('create_address',{label:'Private Bob'}));
        const registry=value(await a.call('search_registry'));assert.deepEqual(registry.items.map(x=>x.addressId),[publicAddress.addressId]);
        assert.equal((await a.call('lookup_address',{addressId:privateAddress.addressId})).isError,true);
        const p=value(await a.call('prepare_payload',{text:'hello recipient'}));
        assert.equal((await a.call('send_message',{recipientMailboxId:bob.agentId,contactGeneration:'1',payload:p})).isError,true);
        const request=value(await a.call('request_contact',{addressId:publicAddress.addressId,payload:p}));
        const requests=value(await b.call('list_contact_requests'));assert.equal(requests.items[0].requesterMailboxId,alice.agentId);
        value(await b.call('decide_contact',{requestId:p.messageId,requesterMailboxId:alice.agentId,expectedGeneration:String(request.generation),status:'accepted'}));
        const channel=value(await a.call('get_channel',{peerMailboxId:bob.agentId})),message=value(await a.call('prepare_payload',{text:'actual demo message'}));
        const args={recipientMailboxId:bob.agentId,contactGeneration:String(channel.generation),payload:message};
        assert.equal(value(await a.call('send_message',args)).state,'pending');assert.equal(value(await a.call('send_message',args)).messageId,message.messageId);
        const inbox=value(await b.call('read_inbox'));assert.ok(inbox.items.some(x=>x.envelope.payload.messageId===message.messageId));
        assert.equal((await a.call('ack_message',{messageId:message.messageId,senderMailboxId:alice.agentId})).isError,true);
        assert.equal(value(await b.call('ack_message',{messageId:message.messageId,senderMailboxId:alice.agentId})).state,'acknowledged');
        assert.ok(!value(await b.call('read_inbox')).items.some(x=>x.envelope.payload.messageId===message.messageId));
        assert.equal(value(await a.call('read_outbox')).items.find(x=>x.messageId===message.messageId).state,'acknowledged');
        for(const control of [aControl,bControl])assert.ok(!a.logs.includes(control)&&!b.logs.includes(control));
    }finally{await a.close();await b.close();await h.close();}
});
test('anonymous MCP can query registry while account operations require a supplied 64-character control',async()=>{
    const h=await start(),client=mcpClient(String(await h.mf.ready));
    try{await client.initialize();assert.deepEqual(value(await client.call('search_registry')).items,[]);assert.equal((await client.call('register_account')).isError,true);assert.equal(await h.db.prepare("SELECT count(*) n FROM accounts").first('n'),0);}finally{await client.close();await h.close();}
});
test('canonical MCP workflow binds two addresses and uses reciprocal contact tokens through send, ACK and owner removal',async()=>{
    const h=await start(),a=mcpClient(String(await h.mf.ready),secret()),b=mcpClient(String(await h.mf.ready),secret());
    try{
        await a.initialize();await b.initialize();const alice=value(await a.call('register_account')),bob=value(await b.call('register_account'));
        const source=value(await a.call('create_address')),target=value(await b.call('create_address'));
        const intro=value(await a.call('prepare_payload',{text:'canonical introduction'}));
        const request=value(await a.call('request_contact',{requesterAccountId:alice.accountId,requesterAddressId:source.addressId,recipientAccountId:bob.accountId,recipientAddressId:target.addressId,payload:intro}));
        assert.equal(request.request.requestVersion,2);assert.equal(request.request.requesterAddressId,source.addressId);
        value(await b.call('decide_contact',{requestId:intro.messageId,requesterAccountId:alice.accountId,expectedGeneration:String(request.generation),status:'accepted'}));
        const contact=value(await a.call('get_contact',{accountId:bob.accountId}));assert.equal(contact.canSend,true);assert.match(contact.connectionToken,/^[0-9a-f]{64}$/);
        const p=value(await a.call('prepare_payload',{text:''}));const args={recipientAccountId:bob.accountId,contactGeneration:String(request.generation),connectionToken:contact.connectionToken,payload:p};
        value(await a.call('send_message',args));const inbox=value(await b.call('read_inbox'));assert.equal(inbox.items.find(x=>x.envelope.payload.messageId===p.messageId).envelope.payload.bytes,'');
        value(await b.call('ack_message',{messageId:p.messageId,senderAccountId:alice.accountId}));assert.equal(value(await a.call('read_outbox')).items.find(x=>x.messageId===p.messageId).state,'acknowledged');
        value(await a.call('remove_contact',{accountId:bob.accountId}));assert.equal(value(await b.call('get_contact',{accountId:alice.accountId})).canSend,false);
        assert.equal((await a.call('send_message',{...args,payload:value(await a.call('prepare_payload',{text:'stale contact'}))})).isError,true);
        assert.equal(value(await a.call('list_contact_requests')).items.length,1);
    }finally{await a.close();await b.close();await h.close();}
});
