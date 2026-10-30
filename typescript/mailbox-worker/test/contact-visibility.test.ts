import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { start, payload } from './harness.ts';

test('pending contact persists for both participants across Worker restart and read-only inbox access', async t => {
    const persist=await mkdtemp(join(tmpdir(),'weave-contact-visibility-'));
    const bindings={CLEANUP_MAX_MESSAGES:'0',CLEANUP_TIME_BUDGET_MS:'0'};
    let h=await start({persist,bindings});
    try {
        const a=await h.register(),b=await h.participant(),outsider=await h.register();
        const p=payload('Local fixture introduction');
        const submission={requestId:p.messageId,cardId:b.cardId,methodId:'relay',payload:p};
        const relation=await h.ok('/v1/contacts/requests',a.control,'POST',submission);
        assert.equal(relation.request.status,'pending');
        assert.equal(relation.request.recipientMailboxId,b.agentId);
        const stored=await h.db.prepare("SELECT requester_account_id,recipient_account_id,request_id,status FROM contact_requests WHERE request_id=?").bind(p.messageId).all();
        assert.equal(stored.results.length,1);
        assert.equal(stored.results[0].requester_account_id,a.agentId);
        assert.equal(stored.results[0].recipient_account_id,b.agentId);assert.equal(stored.results[0].request_id,p.messageId);assert.equal(stored.results[0].status,'pending');
        const exact='/v1/contacts/requests/'+p.messageId+'?requesterMailboxId='+a.agentId;
        for(const participant of [a,b]) {
            assert.deepEqual(await h.ok(exact,participant.control),relation.request);
            assert.deepEqual(await h.ok('/v1/contacts/requests',participant.control),{items:[relation.request],nextCursor:null});
        }
        assert.deepEqual(await h.ok('/v1/contacts/requests',outsider.control),{items:[],nextCursor:null});
        assert.equal((await h.request(exact,outsider.control)).status,404);
        t.diagnostic('POST 200; D1 request row 1; sender list 1; recipient list 1; unrelated account list 0, exact lookup 404.');
        await h.close();h=await start({persist,migrate:false,bindings});
        // Use the saved local fixture credential directly, without any registration/restore call.
        for(const participant of [a,b]) {
            assert.deepEqual(await h.ok('/v1/contacts/requests',participant.control),{items:[relation.request],nextCursor:null});
            assert.deepEqual(await h.ok(exact,participant.control),relation.request);
        }
        const revision=await h.db.prepare('SELECT token FROM mailbox_revision WHERE singleton=1').first('token');
        for(let i=0;i<2;i++) {
            const inbox=await h.ok('/v1/inbox',b.control);
            assert.equal(inbox.items.length,1);
            assert.equal(inbox.items[0].senderMailboxId,a.agentId);
            assert.deepEqual(inbox.items[0].envelope.payload,p);
        }
        assert.equal(await h.db.prepare('SELECT token FROM mailbox_revision WHERE singleton=1').first('token'),revision);
        assert.equal((await h.ok('/v1/outbox/'+p.messageId,a.control)).state,'pending');
        assert.equal((await h.ok(exact,b.control)).status,'pending');
        t.diagnostic('Fresh Worker: sender and recipient list 1, exact lookups agree; direct GET inbox twice returns same introduction; D1 revision unchanged; receipt and contact remain pending. No ACK or decision.');
    } finally {await h.close();await rm(persist,{recursive:true,force:true});}
});
