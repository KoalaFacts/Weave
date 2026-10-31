import assert from 'node:assert/strict';
import { test } from 'node:test';
import { start, payload } from './harness.ts';

test('v1 contact acceptance gates delivery, request replay and ACK survive aliases', async () => {
    const h = await start();
    try {
        const a = await h.participant(), b = await h.participant(), outsider = await h.participant();
        const p = payload('contact');
        const submission = {requestId:p.messageId,cardId:b.cardId,methodId:'relay',payload:p};
        const relation = await h.ok('/v1/contacts/requests',a,'POST',submission);
        assert.equal(relation.request.status,'pending'); assert.equal(relation.generation,1);
        assert.deepEqual(await h.ok('/v1/contacts/requests',a,'POST',submission),relation);
        const msg = {version:1,recipientMailboxId:b.accountId,contactGeneration:1,payload:payload('message')};
        assert.equal((await h.request('/v1/messages',a,'POST',msg)).status,404);
        assert.equal((await h.request('/v1/contacts/requests/'+p.messageId+'?requesterMailboxId='+a.accountId,outsider)).status,404);
        const decision = {requesterMailboxId:a.accountId,expectedGeneration:1,status:'accepted',reply:null};
        assert.equal((await h.request('/v1/contacts/requests/'+p.messageId+'/decision',a,'POST',decision)).status,404);
        const accepted = await h.ok('/v1/contacts/requests/'+p.messageId+'/decision',b,'POST',decision);
        assert.equal(accepted.isConnected,true);
        assert.deepEqual(await h.ok('/v1/contacts/requests/'+p.messageId+'/decision',b,'POST',decision),accepted);
        const receipt = await h.ok('/outbox/'+a.accountId,a,'POST',msg);
        assert.equal(receipt.state,'pending');
        const inbox = await h.ok('/inbox/'+b.accountId,b);
        assert.equal(inbox.items.length,2); assert.deepEqual(inbox,await h.ok('/v1/inbox',b));
        const ack = await h.ok('/v1/inbox/'+msg.payload.messageId+'/ack',b,'POST',{senderMailboxId:a.accountId});
        assert.equal(ack.state,'acknowledged');
        assert.deepEqual(await h.ok('/v1/messages',a,'POST',msg),ack);
        assert.equal((await h.ok('/v1/inbox',b)).items.length,1);
        assert.equal((await h.request('/v1/messages',a,'POST',{...msg,payload:{...msg.payload,bytes:'Y2hhbmdlZA=='}})).status,409);
        assert.equal((await h.request('/v1/inbox/'+p.messageId+'/ack',outsider,'POST',{senderMailboxId:a.accountId})).status,404);
        const channel = await h.ok('/v1/contacts/blocks',b,'PUT',{peerMailboxId:a.accountId,expectedGeneration:1});
        assert.equal(channel.generation,2); assert.equal(channel.isBlockedByMailbox,true);
        assert.equal((await h.ok('/v1/outbox/'+p.messageId,a)).state,'blocked');
        assert.equal((await h.ok('/v1/inbox',b)).items.length,0);
        assert.equal((await h.request('/v1/contacts/blocks',b,'DELETE',{peerMailboxId:a.accountId,expectedGeneration:1})).status,409);
        const unblocked = await h.ok('/v1/contacts/blocks',b,'DELETE',{peerMailboxId:a.accountId,expectedGeneration:2});
        assert.equal(unblocked.generation,3); assert.equal(unblocked.currentRequest,null);
        assert.equal((await h.request('/v1/messages',a,'POST',{...msg,contactGeneration:3,payload:payload()})).status,404);
    } finally { await h.close(); }
});
