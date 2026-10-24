import assert from 'node:assert/strict';
import { test } from 'node:test';
import { build } from 'esbuild';
import { runInNewContext } from 'node:vm';

// Actual stream implementation, in-memory boundaries: no database or network.
async function fixture(options:{abortDuringAcquire?:boolean;abortBeforeAcquire?:boolean;abortOnFrame?:boolean;initialCursor?:string|null}={}) {
    const controller=new AbortController(),frames:string[]=[],reads:(string|null)[]=[];
    if(options.abortBeforeAcquire)controller.abort();
    let saved:any=options.initialCursor?{cursor:options.initialCursor,version:'initial'}:null;
    let released=0,acquired=0,completion:Promise<unknown>;
    const tx={db:{prepare:()=>({bind(){return this;},first:async()=>({total:0,owned:0})})},writes:[],
        get:async()=>saved,put(kind:string,_owner:string,_id:string,value:any){if(kind==='stream-progress')saved=value;else{acquired++;if(options.abortDuringAcquire)controller.abort();}},
        delete(){released++;}};
    const boundary={atomic:async(_db:any,action:any)=>action(tx),read:async(_tx:any,url:URL)=>{reads.push(url.searchParams.get('afterCursor'));return {items:[],nextCursor:null};}};
    const bundled=await build({entryPoints:['src/stream.ts'],bundle:true,write:false,format:'cjs',platform:'node',plugins:[{name:'memory-boundaries',setup(b){
        b.onResolve({filter:/^\.\/(database|reads)$/},args=>({path:args.path,namespace:'memory'}));
        b.onLoad({filter:/.*/,namespace:'memory'},args=>({contents:args.path==='./database'?'export const atomic=globalThis.boundary.atomic;':'export const read=globalThis.boundary.read;',loader:'js'}));
    }}]});
    const module={exports:{} as any};
    class Writer {async write(bytes:Uint8Array){frames.push(new TextDecoder().decode(bytes));if(options.abortOnFrame)controller.abort();}async close(){}async abort(){}}
    class TestStream {readable=new ReadableStream();writable={getWriter:()=>new Writer()};}
    // Accelerate only the empty-poll delay, preserving deadline/write bounds.
    const timer=(fn:()=>void,ms:number)=>setTimeout(fn,ms===1000?0:ms);
    runInNewContext(bundled.outputFiles[0].text,{module,exports:module.exports,boundary,crypto,TextEncoder,URL,Response,TransformStream:TestStream,setTimeout:timer,clearTimeout});
    await module.exports.stream({db:{}},'owner',new Request('http://localhost',{signal:controller.signal}),{waitUntil(p:Promise<unknown>){completion=p;}});
    await completion!;
    return {frames,reads,saved,released,acquired};
}
test('cancellation during lease acquisition releases without inbox reads or frames',async()=>{
    const result=await fixture({abortDuringAcquire:true,initialCursor:'inbox:b3duZXI=:8'});
    assert.equal(result.acquired,1);assert.equal(result.released,1);
    assert.equal(result.reads.length,0);assert.equal(result.frames.length,0);
    assert.equal(result.saved.cursor,'inbox:b3duZXI=:8');assert.equal(result.saved.version,'initial');
});
test('empty tail wraps traversal and reconnect starts from the beginning',async()=>{
    const first=await fixture({initialCursor:'inbox:b3duZXI=:8'});
    assert.equal(first.reads.length,8);assert.equal(first.reads[0],'inbox:b3duZXI=:8');assert.equal(first.reads[1],null);
    assert.equal(first.saved.cursor,null);assert.deepEqual(first.frames,[': pending\n\n']);assert.equal(first.released,1);
    const reconnect=await fixture({initialCursor:first.saved.cursor});
    assert.equal(reconnect.reads[0],null);assert.equal(reconnect.released,1);
});

test('already aborted request does not start a polling traversal',async()=>{
    const result=await fixture({abortBeforeAcquire:true});
    assert.equal(result.reads.length,0);assert.equal(result.frames.length,0);assert.equal(result.released,1);assert.equal(result.saved,null);
});
test('disconnect during initial frame preserves resume cursor for reconnect',async()=>{
    const cursor='inbox:b3duZXI=:8',disconnected=await fixture({initialCursor:cursor,abortOnFrame:true});
    assert.equal(disconnected.reads.length,0);assert.equal(disconnected.saved.cursor,cursor);assert.equal(disconnected.saved.version,'initial');assert.equal(disconnected.released,1);
    const reconnect=await fixture({initialCursor:disconnected.saved.cursor});
    assert.equal(reconnect.reads[0],cursor);assert.equal(reconnect.released,1);
});
