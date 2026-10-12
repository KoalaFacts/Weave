import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { start, payload } from './harness.mjs';

test('fresh Worker reload retains decisions, receipt cursors and ACK tombstones without resurrection', async () => {
    const persist = await mkdtemp(join(tmpdir(),'weave-message-d1-')); let h = await start({persist});
    try {
        const a=await h.participant(),b=await h.participant(),p=payload('request');
        const submission={requestId:p.messageId,cardId:b.cardId,methodId:'relay',payload:p};
        await h.ok('/v1/contacts/requests',a.control,'POST',submission);
        const decision={requesterMailboxId:a.accountId,expectedGeneration:1,status:'accepted',reply:payload('accepted')};
        const accepted=await h.ok('/v1/contacts/requests/'+p.messageId+'/decision',b.control,'POST',decision);
        const envelope={version:1,recipientMailboxId:b.accountId,contactGeneration:1,payload:payload('durable')};
        const results=await Promise.all([h.ok('/v1/messages',a.control,'POST',envelope),h.ok('/v1/messages',a.control,'POST',envelope)]);
        assert.deepEqual(results[0],results[1]);
        const ack=await h.ok('/v1/inbox/'+p.messageId+'/ack',b.control,'POST',{senderMailboxId:a.accountId});
        const first=await h.ok('/v1/outbox?limit=1',a.control); assert.equal(first.items[0].state,'acknowledged'); assert.ok(first.nextCursor);
        await h.close(); h=await start({persist,migrate:false});
        assert.deepEqual(await h.ok('/v1/contacts/requests/'+p.messageId+'/decision',b.control,'POST',decision),accepted);
        assert.deepEqual(await h.ok('/v1/outbox/'+p.messageId,a.control),ack);
        const remaining=await h.ok('/v1/outbox?limit=1&afterCursor='+encodeURIComponent(first.nextCursor),a.control);
        assert.equal(remaining.items.length,1); assert.equal(remaining.items[0].messageId,envelope.payload.messageId);
        const inbox=await h.ok('/v1/inbox',b.control); assert.equal(inbox.items.length,1); assert.deepEqual(inbox.items[0].envelope,envelope);
        assert.equal((await h.ok('/v1/inbox',a.control)).items[0].envelope.payload.messageId,decision.reply.messageId);
        assert.equal((await h.request('/v1/outbox?afterCursor='+encodeURIComponent(first.nextCursor),b.control)).status,400);
        const stored=await h.db.prepare("SELECT data FROM mailbox_records WHERE kind='message' AND id=? AND owner=?").bind(p.messageId,a.accountId).first();
        assert.equal(JSON.parse(stored.data).payload,null);
    } finally { await h.close(); await rm(persist,{recursive:true,force:true}); }
});
test('concurrent decision races commit one outcome and preserve the exact requester-scoped request', async () => {
    const h=await start();
    try {
        const a=await h.participant(),b=await h.participant(),c=await h.participant(),p=payload();
        for(const sender of [a,c]) await h.ok('/v1/contacts/requests',sender.control,'POST',{requestId:p.messageId,cardId:b.cardId,methodId:'relay',payload:p});
        const results=await Promise.all(['accepted','rejected'].map(status=>h.request('/v1/contacts/requests/'+p.messageId+'/decision',b.control,'POST',{requesterMailboxId:a.accountId,expectedGeneration:1,status,reply:null})));
        assert.deepEqual(results.map(r=>r.status).sort(),[200,409]);
        const other=await h.ok('/v1/contacts/requests/'+p.messageId+'?requesterMailboxId='+c.accountId,b.control);
        assert.equal(other.status,'pending');
        const missing=await h.request('/v1/contacts/requests/'+p.messageId,b.control); assert.equal(missing.status,400);
    } finally {await h.close();}
});
test('int64 generation does not round in JavaScript, D1, replay fingerprints or response JSON', async () => {
    const h=await start();
    try {
        const a=await h.participant(),b=await h.participant(),p=payload();
        await h.ok('/v1/contacts/requests',a.control,'POST',{requestId:p.messageId,cardId:b.cardId,methodId:'relay',payload:p});
        // Seed a reachable long-running channel boundary directly; exercise all operations via HTTP.
        const rows=await h.db.prepare("SELECT kind,owner,id,data FROM mailbox_records WHERE kind IN ('channel','request')").all();
        for(const row of rows.results) await h.db.prepare('UPDATE mailbox_records SET data=? WHERE kind=? AND owner=? AND id=?')
            .bind(row.data.replaceAll('"generation":1','"generation":9007199254740993'),row.kind,row.owner,row.id).run();
        const rejected=await h.request('/v1/contacts/blocks',b.control,'PUT','{"peerMailboxId":"'+a.accountId+'","expectedGeneration":9007199254740992}');
        assert.equal(rejected.status,409);
        const changed=await h.request('/v1/contacts/blocks',b.control,'PUT','{"peerMailboxId":"'+a.accountId+'","expectedGeneration":9007199254740993}');
        assert.equal(changed.status,200,await changed.clone().text()); assert.match(await changed.text(),/"generation":9007199254740994/);
    } finally {await h.close();}
});
