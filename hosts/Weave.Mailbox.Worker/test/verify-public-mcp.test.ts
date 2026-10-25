import assert from 'node:assert/strict';
import {test} from 'node:test';
import {verifyPublicMcp} from '../scripts/verify-public-mcp.ts';

const origin='https://demo.weave-389.workers.dev';
const noStore={'Content-Type':'application/json','Cache-Control':'no-store','X-Content-Type-Options':'nosniff'};
const json=(id:number,result:unknown)=>new Response(JSON.stringify({jsonrpc:'2.0',id,result}),{headers:noStore});
const publicTools=['demo_guide','search_registry','lookup_address'];
function fixture(options:{missing?:boolean;malformed?:boolean;error?:boolean}={}) {
    const seen:string[]=[];
    const fetcher=(async(input:RequestInfo|URL,init?:RequestInit)=>{
        assert.equal(String(input),origin+'/mcp');
        assert.equal(init?.method,'POST');
        assert.equal(init?.redirect,'error');
        assert.equal(init?.cache,'no-store');
        assert.ok(init?.signal);
        const headers=new Headers(init?.headers);
        assert.equal(headers.get('content-type'),'application/json');
        assert.equal(headers.get('accept'),'application/json, text/event-stream');
        assert.equal(headers.get('mcp-protocol-version'),'2025-11-25');
        assert.equal(headers.has('authorization'),false);
        assert.equal(headers.has('cookie'),false);
        const request=JSON.parse(String(init?.body));
        assert.equal(request.jsonrpc,'2.0');
        assert.equal(Object.hasOwn(request,'id'),true);
        seen.push(request.method);
        if(options.malformed&&seen.length===1)return new Response('synthetic-sensitive-response',{headers:noStore});
        if(options.error&&seen.length===3)return new Response(JSON.stringify({jsonrpc:'2.0',id:3,result:{isError:true,content:[{type:'text',text:'private data'}]}}),{headers:noStore});
        if(request.method==='initialize')return json(1,{protocolVersion:'2025-11-25',capabilities:{tools:{listChanged:false}},serverInfo:{name:'weave-public',version:'0.1.0'}});
        if(request.method==='tools/list')return json(2,{tools:publicTools.filter(x=>!options.missing||x!=='search_registry').map(name=>({name,annotations:{readOnlyHint:true},securitySchemes:[{type:'noauth'}]}))});
        if(request.method==='tools/call'){
            assert.deepEqual(request.params,{name:'search_registry',arguments:{limit:1}});
            return json(3,{content:[{type:'text',text:JSON.stringify({items:[],nextCursor:null})}]});
        }
        throw Error('Unexpected method');
    }) as typeof fetch;
    return {fetcher,seen};
}
test('release smoke calls exactly one public read without credentials or writes',async()=>{
    const f=fixture();const result=await verifyPublicMcp({fetchImpl:f.fetcher});
    assert.deepEqual(f.seen,['initialize','tools/list','tools/call']);
    assert.deepEqual(result,{endpoint:origin+'/mcp',protocolVersion:'2025-11-25',publicTools:3,directoryRead:true,readOnly:true});
});
test('release smoke refuses missing public tool',async()=>{
    await assert.rejects(verifyPublicMcp({fetchImpl:fixture({missing:true}).fetcher}),/public tool/);
});
test('release smoke rejects malformed response without echoing its content',async()=>{
    await assert.rejects(verifyPublicMcp({fetchImpl:fixture({malformed:true}).fetcher}),err=>{
        assert.doesNotMatch(String(err),/synthetic-sensitive-response/);return true;
    });
});
test('release smoke reports directory tool errors without exposing payload',async()=>{
    await assert.rejects(verifyPublicMcp({fetchImpl:fixture({error:true}).fetcher}),err=>{
        assert.doesNotMatch(String(err),/private data/);return true;
    });
});
test('release smoke enforces response bytes even for otherwise valid JSON-RPC',async()=>{
    const oversizedInitialize=(bytes:number)=>json(1,{
        protocolVersion:'2025-11-25',capabilities:{tools:{listChanged:false}},
        serverInfo:{name:'weave-public',version:'0.1.0'},padding:'x'.repeat(bytes)
    });
    for(const [length,shouldReject] of [[240000,false],[300000,true]] as const){
        const normal=fixture();let count=0;
        const fetcher=(async(input:RequestInfo|URL,init?:RequestInit)=>{
            count++;
            if(count===1)return oversizedInitialize(length);
            return normal.fetcher(input,init);
        }) as typeof fetch;
        if(shouldReject){
            await assert.rejects(verifyPublicMcp({fetchImpl:fetcher}),/Published MCP response exceeds size limit\./);
            assert.equal(count,1,'response limit must stop before discovery or a directory read');
        }else{
            const result=await verifyPublicMcp({fetchImpl:fetcher});
            assert.equal(result.directoryRead,true,'a valid response below the size limit must complete discovery');
            assert.equal(count,3);
            assert.deepEqual(normal.seen,['tools/list','tools/call']);
        }
    }
});
