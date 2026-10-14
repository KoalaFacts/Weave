import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFile } from 'node:fs/promises';
import { start, payload } from './harness.mjs';
import { seedMessage,seedRequest,seedChannel,messageRow,relationSnapshot,messageSnapshot } from './storage-fixtures.mjs';
import { deploymentConfig } from '../scripts/deploy.mjs';
const DAY=86400000;
async function scheduled(h){const result=await (await h.mf.getWorker()).scheduled({cron:'*/5 * * * *',scheduledTime:Date.now()});assert.equal(result.outcome,'ok');}
async function seed(h,sender,recipient,p,state='pending',terminalAt=null){
    const record={receipt:{messageId:p.messageId,senderMailboxId:sender,recipientMailboxId:recipient,state,expiresAt:p.expiresAt,terminalAt},generation:1,version:1,payload:p,size:Buffer.from(p.bytes,'base64').length,expiresTicks:(BigInt(Date.parse(p.expiresAt))*10000n).toString(),fingerprint:'synthetic-retention-fixture',purpose:0,request:null};
    await seedMessage(h.db,record);return record;
}
test('demo deployment bindings preserve expired business records while registry housekeeping continues',async()=>{
    const base=JSON.parse(await readFile('wrangler.jsonc','utf8'));
    const config=deploymentConfig(base,{CLOUDFLARE_ACCOUNT_ID:'a'.repeat(32),CLEANUP_MAX_MESSAGES:'128',CLEANUP_TIME_BUDGET_MS:'2000'});
    const h=await start({bindings:config.vars});
    try{
        const old=Date.now()-9*DAY,p=payload('retained demo body',old);
        await seed(h,'sender','recipient',p);
        for(const [id,status] of [['old-request','pending'],['terminal-request','rejected']])await seedRequest(h.db,{summary:{requestId:id,cardId:'synthetic-card',requesterMailboxId:'sender',recipientMailboxId:'recipient',methodId:'relay',generation:1,status,createdAt:p.createdAt,expiresAt:p.expiresAt,requestMessageId:p.messageId,replyMessageId:null},terminalAt:status==='rejected'?p.createdAt:null});
        await seedChannel(h.db,{low:'recipient',high:'sender',generation:1,blockedLow:false,blockedHigh:false,currentRequest:{requesterMailboxId:'sender',requestId:'terminal-request'},updatedAt:p.createdAt});
        await h.db.batch([h.db.prepare("UPDATE counters SET value=value+1 WHERE name='record-seq'"),h.db.prepare("INSERT INTO rate_limits(seq,policy,bucket_key,window_start,count) VALUES((SELECT value FROM counters WHERE name='record-seq'),'public-read','old-counter',?,1)").bind((BigInt(old)*10000n).toString())]);
        const snapshot=async()=>({relations:await relationSnapshot(h.db),messages:await messageSnapshot(h.db)});
        const before=await snapshot();await scheduled(h);assert.deepEqual(await snapshot(),before);
        assert.equal(await h.db.prepare("SELECT count(*) n FROM maintenance_state").first('n'),0);
        assert.deepEqual((await h.ok('/v1/directory')).items,[]);
        assert.equal(await h.db.prepare("SELECT count(*) n FROM rate_limits WHERE policy='public-read' AND bucket_key='old-counter'").first('n'),0);
        assert.equal(await h.db.prepare("SELECT count(*) n FROM rate_limits WHERE policy='public-read'").first('n'),2);
        assert.deepEqual(await snapshot(),before);
    }finally{await h.close();}
});
test('scheduled safety net clears expired and retained terminal bodies, preserves live pending and seven-day receipts',async()=>{
    const h=await start();
    try{
        const a=await h.participant(),b=await h.participant(),now=Date.now(),expired=payload('expired body',now-2*3600000),acked=payload('unexpected retained ACK body'),live=payload('live pending');
        await seed(h,a.agentId,b.agentId,expired);await seed(h,b.agentId,a.agentId,acked,'acknowledged',new Date(now).toISOString());await seed(h,a.agentId,b.agentId,live);
        await scheduled(h);
        for(const p of [expired,acked]){const row=await messageRow(h.db,p.messageId);assert.equal(row.payload,null);assert.equal(row.size,0);assert.equal(row.receipt.state,p===expired?'expired':'acknowledged');}
        const retained=await messageRow(h.db,live.messageId);assert.deepEqual(retained.payload,live);
        const first=await messageSnapshot(h.db);await scheduled(h);assert.deepEqual(await messageSnapshot(h.db),first);
        assert.deepEqual(JSON.parse(await readFile('wrangler.jsonc','utf8')).triggers.crons,['*/5 * * * *']);
    }finally{await h.close();}
});
test('bounded cleanup resumes oldest eligible rows and purged IDs cannot be refreshed or replayed',async()=>{
    const h=await start();
    try{
        const a=await h.participant(),b=await h.participant(),old=Date.now()-9*DAY,ids=[];let prior;
        for(let i=0;i<130;i++){const p=payload('old synthetic',old+i);ids.push(p.messageId);prior??=p;await seed(h,a.agentId,b.agentId,p,'acknowledged',new Date(old+3600000).toISOString());}
        await scheduled(h);assert.equal(await h.db.prepare("SELECT count(*) n FROM mailbox_records").first('n'),2);
        // Advance the maintenance throttle, not the business records or production clock.
        await h.db.prepare("DELETE FROM maintenance_state WHERE name='sweep'").run();await scheduled(h);assert.equal(await h.db.prepare("SELECT count(*) n FROM mailbox_records").first('n'),0);
        const envelope={version:1,recipientMailboxId:b.agentId,contactGeneration:1,payload:prior};assert.equal((await h.request('/v1/messages',a.control,'POST',envelope)).status,410);
        assert.equal((await h.request('/v1/messages',a.control,'POST',{...envelope,payload:{...prior,createdAt:new Date().toISOString(),expiresAt:new Date(Date.now()+3600000).toISOString()}})).status,400);
        assert.equal((await h.request('/v1/outbox/'+ids[0],a.control)).status,404);
    }finally{await h.close();}
});
test('scheduled cleanup races with actual ACK and send without resurrecting payload or dropping new mail',async()=>{
    const h=await start();
    try{
        const a=await h.participant(),b=await h.participant(),intro=payload('intro');await h.ok('/v1/contacts/requests',a.control,'POST',{requestId:intro.messageId,cardId:b.cardId,methodId:'relay',payload:intro});
        await h.ok('/v1/contacts/requests/'+intro.messageId+'/decision',b.control,'POST',{requesterMailboxId:a.agentId,expectedGeneration:1,status:'accepted',reply:null});
        const next=payload('fresh race');await h.db.prepare("DELETE FROM maintenance_state WHERE name='sweep'").run();
        const results=await Promise.all([scheduled(h),h.request('/v1/inbox/'+intro.messageId+'/ack',b.control,'POST',{senderMailboxId:a.agentId}),h.request('/v1/messages',a.control,'POST',{version:1,recipientMailboxId:b.agentId,contactGeneration:1,payload:next})]);
        assert.equal(results[1].status,200);assert.equal(results[2].status,200);
        const row=await messageRow(h.db,intro.messageId);assert.equal(row.payload,null);assert.equal(row.receipt.state,'acknowledged');
        assert.ok((await h.ok('/v1/inbox',b.control)).items.some(x=>x.envelope.payload.messageId===next.messageId));
    }finally{await h.close();}
});
for(const [maximum,budget,expected] of [['0','2000',0],['1','2000',1],['64','2000',64],['100000','2000',128],['100000','0',0]])test(`cleanup message limit ${maximum}, time budget ${budget} stops at actual bounded message count`,async()=>{
    const h=await start({bindings:{CLEANUP_MAX_MESSAGES:maximum,CLEANUP_TIME_BUDGET_MS:budget}});
    try{
        const old=Date.now()-9*DAY;
        for(let i=0;i<130;i++)await seed(h,'synthetic-owner-a','synthetic-owner-b',payload('expired',old+i),'acknowledged',new Date(old+3600000).toISOString());
        await scheduled(h);assert.equal(await h.db.prepare("SELECT count(*) n FROM mailbox_records").first('n'),130-expected);
    }finally{await h.close();}
});
test('cleanup rejects configuration above the hard message cap and preserves rows on a failed transaction',async()=>{
    const h=await start({bindings:{CLEANUP_MAX_MESSAGES:'100001'}});
    try{const result=await(await h.mf.getWorker()).scheduled({cron:'*/5 * * * *',scheduledTime:Date.now()});assert.equal(result.outcome,'exception');assert.equal(await h.db.prepare("SELECT count(*) n FROM maintenance_state").first('n'),0);}finally{await h.close();}
    const active=await start();
    try{
        const p=payload('expired rollback',Date.now()-9*DAY);await seed(active,'owner-a','owner-b',p);
        await active.db.prepare("CREATE TRIGGER reject_cleanup BEFORE DELETE ON mailbox_records BEGIN SELECT RAISE(ABORT,'synthetic cleanup interruption'); END").run();
        const outcome=await(await active.mf.getWorker()).scheduled({cron:'*/5 * * * *',scheduledTime:Date.now()});assert.equal(outcome.outcome,'exception');
        const retained=await messageRow(active.db,p.messageId);assert.equal(retained.receipt.state,'pending');assert.deepEqual(retained.payload,p);assert.equal(await active.db.prepare("SELECT count(*) n FROM maintenance_state").first('n'),0);
        await active.db.prepare('DROP TRIGGER reject_cleanup').run();await scheduled(active);assert.equal(await active.db.prepare("SELECT count(*) n FROM mailbox_records").first('n'),0);
    }finally{await active.close();}
});
test('elapsed time stops before another batch and an interrupted round resumes its remaining eligible messages',async()=>{
    const {build}=await import('esbuild'),{runInNewContext}=await import('node:vm'),h=await start();
    try{
        const old=Date.now()-9*DAY;for(let i=0;i<70;i++)await seed(h,'owner-a','owner-b',payload('time budget',old+i),'acknowledged',new Date(old+3600000).toISOString());
        const built=await build({entryPoints:['src/cleanup.ts'],bundle:true,write:false,format:'cjs',platform:'node'}),module={exports:{}};let elapsed=0;
        runInNewContext(built.outputFiles[0].text,{module,exports:module.exports,crypto:globalThis.crypto,performance:{now:()=>elapsed},TextEncoder});
        const db={prepare:sql=>h.db.prepare(sql),batch:async statements=>{const result=await h.db.batch(Array.from(statements));elapsed=2500;return result;}};
        const result=await module.exports.sweep({db,CLEANUP_MAX_MESSAGES:'100000',CLEANUP_TIME_BUDGET_MS:'2000'},true);assert.equal(result.messages,64);assert.equal(result.batches,1);assert.equal(await h.db.prepare("SELECT count(*) n FROM mailbox_records").first('n'),6);
        await h.db.prepare("DELETE FROM maintenance_state WHERE name='sweep'").run();await scheduled(h);assert.equal(await h.db.prepare("SELECT count(*) n FROM mailbox_records").first('n'),0);
    }finally{await h.close();}
});
