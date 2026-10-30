import assert from 'node:assert/strict';
import {test} from 'node:test';
import {verifyPublicMcp} from '../scripts/verify-public-mcp.ts';

const goodHeaders={'Content-Type':'application/json','Cache-Control':'no-store','X-Content-Type-Options':'nosniff'};
const tools=['demo_guide','search_registry','lookup_address'];
function successfulResponse(method:string,id:number):Response{
    let result:unknown;
    if(method==='initialize')result={protocolVersion:'2025-11-25',serverInfo:{name:'weave-public'}};
    else if(method==='tools/list')result={tools:tools.map(name=>({name,annotations:{readOnlyHint:true},securitySchemes:[{type:'noauth'}]}))};
    else result={content:[{type:'text',text:JSON.stringify({items:[],nextCursor:null})}]};
    return new Response(JSON.stringify({jsonrpc:'2.0',id,result}),{headers:goodHeaders});
}
test('release initialization tolerates bounded transient 503 without retrying directory reads',async()=>{
    const calls:string[]=[],delays:number[]=[];
    const fetcher=(async(_input:RequestInfo|URL,init?:RequestInit)=>{
        const req=JSON.parse(String(init?.body)),method=String(req.method);calls.push(method);
        if(method==='initialize'&&calls.length<=2)return new Response('do-not-log-credentials',{status:503,headers:goodHeaders});
        return successfulResponse(method,req.id);
    }) as typeof fetch;
    const result=await verifyPublicMcp({fetchImpl:fetcher,delayImpl:async(ms:number)=>{delays.push(ms);}});
    assert.equal(result.directoryRead,true);
    assert.deepEqual(calls,['initialize','initialize','initialize','tools/list','tools/call']);
    assert.deepEqual(delays,[500,1000]);
});
test('release initialization never retries forbidden response and reports only status and fixed header flags',async()=>{
    let calls=0;const fetcher=(async()=>{calls++;return new Response('private token text',{status:403,headers:{'Content-Type':'text/html'}});}) as typeof fetch;
    await assert.rejects(verifyPublicMcp({fetchImpl:fetcher,delayImpl:async()=>{throw Error('should not delay');}}),error=>{
        assert.match(String(error),/status=403/);
        assert.match(String(error),/contentTypeOk=false/);
        assert.doesNotMatch(String(error),/private token text/);
        return true;
    });
    assert.equal(calls,1);
});
test('release initialization fails after bounded transient retries and exposes no response content',async()=>{
    let calls=0;const delays:number[]=[];
    const fetcher=(async()=>{calls++;return new Response('possibly-sensitive-cloudflare-body',{status:503,headers:goodHeaders});}) as typeof fetch;
    await assert.rejects(verifyPublicMcp({fetchImpl:fetcher,delayImpl:async ms=>{delays.push(ms);}}),error=>{
        assert.match(String(error),/status=503/);
        assert.doesNotMatch(String(error),/possibly-sensitive-cloudflare-body/);
        return true;
    });
    assert.equal(calls,3);assert.deepEqual(delays,[500,1000]);
});
test('release still fails immediately for 200 with incorrect no-store, without retry',async()=>{
    let calls=0;const fetcher=(async()=>{calls++;return new Response('{}',{status:200,headers:{...goodHeaders,'Cache-Control':'public'}});}) as typeof fetch;
    await assert.rejects(verifyPublicMcp({fetchImpl:fetcher,delayImpl:async()=>{throw Error('should not delay');}}),/noStoreOk=false/);
    assert.equal(calls,1);
});
