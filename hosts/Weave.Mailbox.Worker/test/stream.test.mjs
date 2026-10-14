import assert from 'node:assert/strict';
import { test } from 'node:test';
import { start, payload } from './harness.mjs';
async function nextMessage(reader) {
    let text = '';
    for (let i=0;i<10;i++) {
        const part = await reader.read(); assert.equal(part.done,false); text += new TextDecoder().decode(part.value);
        const match = /data: (.+)\n\n/.exec(text); if (match) return JSON.parse(match[1]);
    }
    assert.fail('No SSE message');
}
test('SSE replays pending across reconnect, bounds per-account slots, ACK removes subsequent events', async () => {
    const h = await start(); let first, second;
    try {
        const a = await h.participant(), b = await h.participant(), p = payload();
        await h.ok('/v1/contacts/requests',a.control,'POST',{requestId:p.messageId,cardId:b.cardId,methodId:'relay',payload:p});
        const r1 = await h.request('/v1/inbox/events',b.control); assert.equal(r1.status,200,await (r1.status===200 ? Promise.resolve('') : r1.text()));
        assert.equal(r1.headers.get('content-type'),'text/event-stream'); first = r1.body.getReader();
        assert.equal((await nextMessage(first)).envelope.payload.messageId,p.messageId);
        const r2 = await h.mf.dispatchFetch('http://localhost/v1/inbox/events',{headers:{'X-Weave-Mailbox-Control':b.control,'Last-Event-ID':p.messageId}});
        assert.equal(r2.status,200); second = r2.body.getReader(); assert.equal((await nextMessage(second)).envelope.payload.messageId,p.messageId);
        assert.equal((await h.request('/v1/inbox/events',b.control)).status,429);
        await h.ok('/v1/inbox/'+p.messageId+'/ack',b.control,'POST',{senderMailboxId:a.accountId});
        assert.deepEqual(await h.ok('/v1/inbox',b.control),{items:[],nextCursor:null});
        let rest=''; for (;;) { const v=await first.read(); if(v.done) break; rest+=new TextDecoder().decode(v.value); }
        assert.equal(rest.includes('event: message'),false);
    } finally { await first?.cancel(); await second?.cancel(); await h.close(); }
});

test('SSE account isolation holds while another account has a pending message and both stream slots occupied',async()=>{
    const h=await start(); let first,second;
    try {
        const a=await h.participant(),b=await h.participant(),p=payload();
        await h.ok('/v1/contacts/requests',a.control,'POST',{requestId:p.messageId,cardId:b.cardId,methodId:'relay',payload:p});
        first=(await h.request('/v1/inbox/events',b.control)).body.getReader();
        second=(await h.request('/v1/inbox/events',b.control)).body.getReader();
        assert.equal((await h.request('/v1/inbox/events',b.control)).status,429);
        const own=await h.request('/v1/inbox/events',a.control);assert.equal(own.status,200);
        assert.equal((await own.text()).includes('event: message'),false);
        assert.equal((await h.ok('/v1/inbox',b.control)).items[0].envelope.payload.messageId,p.messageId);
    } finally {await first?.cancel();await second?.cancel();await h.close();}
});

test('absolute stream deadline releases its lease even when each write is slow but below the write timeout',async t=>{
    const {build}=await import('esbuild');
    const bundled=await build({entryPoints:['src/stream.ts'],bundle:true,write:false,format:'esm',platform:'node'});
    const {stream}=await import('data:text/javascript;base64,'+Buffer.from(bundled.outputFiles[0].text).toString('base64'));
    const Original=globalThis.TransformStream;
    let aborted=false,released=false,completion;
    const writer={write:()=>new Promise(resolve=>setTimeout(resolve,9000)),close:async()=>{},abort:async()=>{aborted=true;}};
    globalThis.TransformStream=class {readable=new ReadableStream();writable={getWriter:()=>writer};};
    const message={receipt:{senderMailboxId:'a',recipientMailboxId:'b'},generation:1,version:1,payload:{messageId:'id'}};
    const db={prepare(sql){return {sql,bind(){return this;},async first(column){return column==='token'?'revision':sql.includes('count(*)')?{total:0,owned:0}:null;},async all(){return {results:[{seq:'1',message_id:'id',sender_account_id:'a',recipient_account_id:'b',payload:[65],payload_size:1,payload_encoding:'text/plain',created_at:'0',expires_at:'36000000000',state:'pending',terminal_at:null,legacy_generation:'1',protocol_version:1,purpose:0,connection_token:null,request_id:null}]};}}},async batch(statements){if(statements.some(s=>s.sql.toLowerCase().startsWith('delete from stream_leases')))released=true;return [];}};
    t.mock.timers.enable({apis:['setTimeout','Date'],now:Date.now()});
    try {
        await stream({db:db},'b',new Request('http://localhost'),{waitUntil(p){completion=p;}});
        for(const elapsed of [9000,9000,1000,1000]) {t.mock.timers.tick(elapsed);await new Promise(resolve=>setImmediate(resolve));}
        await completion;
        assert.equal(aborted,true);assert.equal(released,true);
    } finally {t.mock.timers.reset();globalThis.TransformStream=Original;}
});
for(const count of [9,64])test('SSE traverses all '+count+' pending messages without ACK and replays them on reconnect',async()=>{
    const h=await start();
    try {
        const a=await h.participant(),b=await h.participant(),expiresAt=new Date(Date.now()+3600000).toISOString();
        const first={...payload('contact'),expiresAt},ids=[first.messageId];
        await h.ok('/v1/contacts/requests',a.control,'POST',{requestId:first.messageId,cardId:b.cardId,methodId:'relay',payload:first});
        await h.ok('/v1/contacts/requests/'+first.messageId+'/decision',b.control,'POST',{requesterMailboxId:a.accountId,expectedGeneration:1,status:'accepted',reply:null});
        for(let i=1;i<count;i++){
            const p={...payload('pending '+i),expiresAt};ids.push(p.messageId);
            await h.ok('/v1/messages',a.control,'POST',{version:1,recipientMailboxId:b.accountId,contactGeneration:1,payload:p});
        }
        for(let traversal=0;traversal<2;traversal++){
            const delivered=new Set();
            for(let connection=0;connection<Math.ceil(count/8)+2 && delivered.size<count;connection++){
                const response=await h.mf.dispatchFetch('http://localhost/v1/inbox/events',{headers:{'X-Weave-Mailbox-Control':b.control,'Last-Event-ID':ids.at(-1)}});
                assert.equal(response.status,200);
                for(const match of (await response.text()).matchAll(/^id: (.+)$/gm))delivered.add(match[1]);
                // Response closure precedes waitUntil's lease/progress commit.
                for(let wait=0;wait<100;wait++){
                    if(await h.db.prepare("SELECT count(*) n FROM stream_leases WHERE account_id=?").bind(b.accountId).first('n')===0)break;
                    await new Promise(resolve=>setTimeout(resolve,10));
                }
            }
            assert.deepEqual([...delivered].sort(),[...ids].sort());
            assert.equal((await h.ok('/v1/outbox/'+ids.at(-1),a.control)).state,'pending');
        }
    } finally {await h.close();}
});
async function pendingPair(h,count){
    const a=await h.participant(),b=await h.participant(),p=payload('contact'),ids=[p.messageId];
    await h.ok('/v1/contacts/requests',a.control,'POST',{requestId:p.messageId,cardId:b.cardId,methodId:'relay',payload:p});
    await h.ok('/v1/contacts/requests/'+p.messageId+'/decision',b.control,'POST',{requesterMailboxId:a.accountId,expectedGeneration:1,status:'accepted',reply:null});
    for(let i=1;i<count;i++){const next=payload('message '+i);ids.push(next.messageId);await h.ok('/v1/messages',a.control,'POST',{version:1,recipientMailboxId:b.accountId,contactGeneration:1,payload:next});}
    return {a,b,ids};
}
async function controlledStream(h,owner,write){
    const {build}=await import('esbuild'),{runInNewContext}=await import('node:vm');
    const bundled=await build({stdin:{contents:"export {stream} from './src/stream.ts'; export {budgetDatabase} from './src/budget.ts';",resolveDir:process.cwd()},bundle:true,write:false,format:'cjs',platform:'node'});
    const module={exports:{}};let completion;
    const TestStream=class {readable=new ReadableStream();writable={getWriter:()=>({write:bytes=>write(new TextDecoder().decode(bytes)),close:async()=>{},abort:async()=>{}})};};
    runInNewContext(bundled.outputFiles[0].text,{module,exports:module.exports,TextEncoder,URL,crypto,Response,Request,btoa,atob,TransformStream:TestStream,setTimeout,clearTimeout});
    const {stream,budgetDatabase}=module.exports;
    await stream({db:budgetDatabase({prepare:sql=>h.db.prepare(sql),batch:statements=>h.db.batch(Array.from(statements))})},owner,new Request('http://localhost'),{waitUntil(p){completion=p;}});
    return {completion};
}
for(const transition of ['ACK','block'])test('SSE takes a fresh D1 snapshot after a blocked write while the next message is '+transition+'ed',async()=>{
    const h=await start();
    try {
        const {a,b,ids}=await pendingPair(h,2),frames=[];let changed=false;
        const {completion}=await controlledStream(h,b.accountId,async frame=>{
            frames.push(frame);
            if(!changed&&frame.includes('id: '+ids[0])){
                changed=true;
                if(transition==='ACK')await h.ok('/v1/inbox/'+ids[1]+'/ack',b.control,'POST',{senderMailboxId:a.accountId});
                else await h.ok('/v1/contacts/blocks',b.control,'PUT',{peerMailboxId:a.accountId,expectedGeneration:1});
            }
        });
        await completion;assert.equal(changed,true);
        assert.equal(frames.some(frame=>frame.includes('id: '+ids[1])),false);
    } finally {await h.close();}
});
test('late stream release cannot overwrite newer traversal progress',async()=>{
    const h=await start();let unblock;
    try {
        const {b,ids}=await pendingPair(h,9);let entered;
        const waiting=new Promise(resolve=>{entered=resolve;}),blocked=new Promise(resolve=>{unblock=resolve;});
        let writes=0;
        const old=await controlledStream(h,b.accountId,async frame=>{if(!frame.includes('event: message'))return;if(writes++===0){entered();await blocked;}else throw Error('disconnected');});
        await waiting;
        const newer=await controlledStream(h,b.accountId,async()=>{});await newer.completion;
        const before=await h.db.prepare("SELECT json_object('cursor',cursor,'version',version) data FROM stream_cursors WHERE account_id=?").bind(b.accountId).first('data');
        assert.ok(JSON.parse(before).version);assert.ok(JSON.parse(before).cursor);
        unblock();await old.completion;
        assert.equal(await h.db.prepare("SELECT json_object('cursor',cursor,'version',version) data FROM stream_cursors WHERE account_id=?").bind(b.accountId).first('data'),before);
        assert.equal((await h.ok('/v1/inbox',b.control)).items.length,ids.length);
    } finally {unblock?.();await h.close();}
});
