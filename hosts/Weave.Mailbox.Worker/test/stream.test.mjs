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
    const db={prepare(sql){return {sql,bind(){return this;},async first(column){return column==='token'?'revision':{total:0,owned:0};},async all(){return {results:[{seq:'1',data:JSON.stringify(message)}]};}}},async batch(statements){if(statements.some(s=>s.sql.startsWith('DELETE FROM mailbox_records WHERE kind=?')))released=true;return [];}};
    t.mock.timers.enable({apis:['setTimeout','Date'],now:Date.now()});
    try {
        await stream({DB:db},'b',new Request('http://localhost'),{waitUntil(p){completion=p;}});
        for(const elapsed of [9000,9000,1000,1000]) {t.mock.timers.tick(elapsed);await new Promise(resolve=>setImmediate(resolve));}
        await completion;
        assert.equal(aborted,true);assert.equal(released,true);
    } finally {t.mock.timers.reset();globalThis.TransformStream=Original;}
});
