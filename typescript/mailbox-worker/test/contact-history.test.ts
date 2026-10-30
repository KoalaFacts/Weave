import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtemp, rm } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { start, payload } from './harness.ts';
import { seedRequest,seedMessage,seedChannel,relationSnapshot } from './storage-fixtures.ts';

const DAY = 86400000;
async function history(h, sender, recipient, count) {
    const summaries = [];
    for (let i = 0; i < count; i++) {
        const p = payload('synthetic expired introduction', Date.now() - 9 * DAY + i);
        const status = ['pending', 'needsAction', 'accepted', 'rejected'][i % 4];
        const summary = { requestId:p.messageId, cardId:'synthetic-card', requesterMailboxId:sender.agentId,
            recipientMailboxId:recipient.agentId, methodId:'relay', generation:1, status,
            createdAt:p.createdAt, expiresAt:p.expiresAt, requestMessageId:p.messageId, replyMessageId:null };
        await seedRequest(h.db,{summary,fingerprint:'synthetic-history',updatedAt:p.createdAt,terminalAt:status==='rejected'?p.createdAt:null});
        Object.assign(summary,{requesterAccountId:sender.agentId,recipientAccountId:recipient.agentId,requestVersion:1,requesterAddressId:null,recipientAddressId:null});
        summaries.push(summary);
    }
    return summaries;
}

test('expired, accepted and rejected history remains participant-visible across pages and restart without cleanup', async () => {
    const persist = await mkdtemp(join(tmpdir(), 'weave-contact-history-'));
    const bindings = { CLEANUP_MAX_MESSAGES:'0', CLEANUP_TIME_BUDGET_MS:'0' };
    let h = await start({ persist, bindings });
    try {
        const a = await h.register(), b = await h.register(), outsider = await h.register();
        const summaries = await history(h, a, b, 12);
        const expected = summaries.map(r => ['pending','needsAction'].includes(r.status) ? {...r,status:'expired'} : r);
        await h.close(); h = await start({ persist, migrate:false, bindings });
        const revision = await h.db.prepare('SELECT token FROM mailbox_revision WHERE singleton=1').first('token');
        for (const participant of [a,b]) {
            const first = await h.ok('/v1/contacts/requests', participant.control);
            assert.deepEqual(first.items, expected.slice(0,10));
            assert.ok(first.nextCursor);
            const second = await h.ok('/v1/contacts/requests?afterCursor=' + encodeURIComponent(first.nextCursor), participant.control);
            assert.deepEqual(second, { items:expected.slice(10), nextCursor:null });
            assert.deepEqual(await h.ok('/v1/contacts/requests/' + summaries[0].requestId + '?requesterMailboxId=' + a.agentId, participant.control), expected[0]);
        }
        assert.deepEqual(await h.ok('/v1/contacts/requests', outsider.control), { items:[], nextCursor:null });
        assert.equal((await h.request('/v1/contacts/requests/' + summaries[0].requestId + '?requesterMailboxId=' + a.agentId, outsider.control)).status, 404);
        assert.equal(await h.db.prepare('SELECT token FROM mailbox_revision WHERE singleton=1').first('token'), revision);
        assert.equal(await h.db.prepare("SELECT count(*) n FROM contact_requests").first('n'), 12);
    } finally { await h.close(); await rm(persist, { recursive:true, force:true }); }
});

test('enabled scheduled cleanup preserves every contact request and current locator while purging old message bodies', async () => {
    const h = await start();
    try {
        const a = await h.register(), b = await h.register();
        const summaries = await history(h, a, b, 4), r = summaries[3];
        const [low,high] = [a.agentId,b.agentId].sort();
        const channel = { low, high, generation:1, blockedLow:false, blockedHigh:false,
            currentRequest:{ requesterMailboxId:a.agentId, requestId:r.requestId }, updatedAt:r.createdAt };
        await seedChannel(h.db,channel);
        const p=payload('old body must not be retained',Date.now()-9*DAY);
        await seedMessage(h.db,{receipt:{messageId:p.messageId,senderMailboxId:a.agentId,recipientMailboxId:b.agentId,state:'pending',expiresAt:p.expiresAt,terminalAt:null},payload:p});
        const before=await relationSnapshot(h.db);
        const result = await (await h.mf.getWorker()).scheduled({ cron:'*/5 * * * *', scheduledTime:Date.now() });
        assert.equal(result.outcome,'ok');
        assert.deepEqual(await relationSnapshot(h.db),before);
        assert.equal(await h.db.prepare("SELECT count(*) n FROM mailbox_records").first('n'),0);
        assert.deepEqual((await h.ok('/v1/contacts/requests',b.control)).items.map(r=>r.status),['expired','expired','accepted','rejected']);
    } finally { await h.close(); }
});
