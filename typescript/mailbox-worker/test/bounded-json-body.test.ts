import assert from 'node:assert/strict';
import {test} from 'node:test';
import {body,Problem} from '../src/wire.ts';

const request=(stream:ReadableStream<Uint8Array>)=>new Request('https://weave.test/v1/accounts/oauth/links',{
    method:'POST',headers:{'Content-Type':'application/json'},body:stream,duplex:'half'
} as RequestInit&{duplex:'half'});

async function observed<T>(operation:Promise<T>,milliseconds:number):Promise<T|'timed-out'> {
    let timer:ReturnType<typeof setTimeout>|undefined;
    try {
        return await Promise.race([operation,new Promise<'timed-out'>(resolve=>{
            timer=setTimeout(()=>resolve('timed-out'),milliseconds);
        })]);
    }finally{if(timer!==undefined)clearTimeout(timer);}
}

test('JSON request body that stalls after a chunk cannot hold an HTTP handler indefinitely',{timeout:7000},async()=>{
    let cancelled=0;
    const stream=new ReadableStream<Uint8Array>({
        start(controller){controller.enqueue(new TextEncoder().encode('{"consent":'));},
        cancel(){cancelled++;return new Promise<void>(()=>{});}
    });
    const outcome=await observed(body(request(stream)).then(()=>null).catch((e:unknown)=>e),4800);
    assert.notEqual(outcome,'timed-out','a stalled client body must have one whole-stream deadline');
    assert.ok(outcome instanceof Problem,'the deadline must reject the request with a typed error');
    assert.equal(outcome.status,408,'slow client body is a request timeout, not a storage error');
    assert.equal(cancelled,1,'deadline must initiate best-effort stream cleanup');
});

test('oversized JSON body refuses immediately even if upstream cancellation never settles',{timeout:3000},async()=>{
    let cancelled=0;
    const stream=new ReadableStream<Uint8Array>({
        start(controller){controller.enqueue(new Uint8Array(131073));},
        cancel(){cancelled++;return new Promise<void>(()=>{});}
    });
    const outcome=await observed(body(request(stream)).then(()=>null).catch((e:unknown)=>e),1200);
    assert.notEqual(outcome,'timed-out','a too-large request must not wait for a hung cancel()');
    assert.ok(outcome instanceof Problem);
    assert.equal(outcome.status,413);
    assert.equal(cancelled,1);
});

test('bounded JSON request parsing still accepts ordinary valid bodies',async()=>{
    const request=new Request('https://weave.test/v1/accounts/oauth/links',{
        method:'POST',headers:{'Content-Type':'application/json'},body:'{"consent":true}'
    });
    assert.deepEqual(await body(request),{consent:true});
});
