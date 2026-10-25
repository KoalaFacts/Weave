import assert from 'node:assert/strict';
import { test } from 'node:test';
import { remoteMcp } from '../src/remote-mcp.ts';
const origin = 'https://demo.example';
const headers = {'content-type':'application/json',accept:'application/json, text/event-stream'};
const rpc = (method:string, params?:unknown) => ({jsonrpc:'2.0',id:1,method,...(params===undefined?{}:{params})});
function request(value:unknown, extra:Record<string,string>={}) {
    return new Request(origin+'/mcp',{method:'POST',headers:{...headers,...extra},body:JSON.stringify(value)});
}
const unused = async (_url:URL):Promise<Response> => { throw Error('unexpected directory read'); };
async function call(method:string,params?:unknown,read=unused) {
    const response=await remoteMcp(request(rpc(method,params)),read);
    assert.equal(response.status,200);return response.json();
}

test('remote client initializes without credentials or session state',async()=>{
    const response=await remoteMcp(request(rpc('initialize',{protocolVersion:'2025-11-25',capabilities:{},clientInfo:{name:'fixture',version:'1'}})),unused);
    assert.equal(response.status,200);
    const body=await response.json();
    assert.equal(body.result.protocolVersion,'2025-11-25');
    assert.deepEqual(body.result.capabilities,{tools:{listChanged:false}});
    assert.equal(response.headers.get('mcp-session-id'),null);
    assert.equal(response.headers.get('cache-control'),'no-store');
    assert.equal(response.headers.get('x-content-type-options'),'nosniff');
});
test('negotiates a supported version for an unknown initialization version',async()=>{
    const body=await call('initialize',{protocolVersion:'2099-01-01',capabilities:{},clientInfo:{name:'fixture',version:'1'}});
    assert.equal(body.result.protocolVersion,'2025-11-25');
});
test('advertises only three unauthenticated read-only tools',async()=>{
    const body=await call('tools/list');
    assert.deepEqual(body.result.tools.map((x:any)=>x.name),['demo_guide','search_registry','lookup_address']);
    for(const tool of body.result.tools){
        assert.equal(tool.annotations.readOnlyHint,true);
        assert.equal(tool.annotations.destructiveHint,false);
        assert.deepEqual(tool.securitySchemes,[{type:'noauth'}]);
    }
});
test('guide states that private account access and messaging are unavailable',async()=>{
    const body=await call('tools/call',{name:'demo_guide',arguments:{}});
    const guide=JSON.parse(body.result.content[0].text);
    assert.equal(guide.mode,'public-read-only');
    assert.equal(guide.authenticated,false);
    assert.equal(guide.privateMessaging,false);
    assert.equal(guide.agentGuide,origin+'/agent-start.md');
});
test('search maps to the canonical directory with encoded pagination parameters',async()=>{
    let seen:URL|undefined;
    const raw='{"items":[],"nextCursor":"opaque","exact":9223372036854775807}';
    const body=await call('tools/call',{name:'search_registry',arguments:{q:'a&b',afterCursor:'x+/=',limit:10}},async url=>{seen=url;return new Response(raw);});
    assert.equal(seen?.origin,origin);assert.equal(seen?.pathname,'/v1/directory');
    assert.equal(seen?.searchParams.get('q'),'a&b');assert.equal(seen?.searchParams.get('afterCursor'),'x+/=');
    assert.equal(body.result.content[0].text,raw,'do not round exact numbers through JSON.parse/stringify');
});
test('lookup uses the public address lookup, never the private account endpoint',async()=>{
    const id='01234567-89ab-cdef-0123-456789abcdef';let seen:URL|undefined;
    await call('tools/call',{name:'lookup_address',arguments:{addressId:id}},async url=>{seen=url;return new Response('{}');});
    assert.equal(seen?.pathname,'/v1/directory/address');assert.equal(seen?.searchParams.get('addressId'),id);
});
for(const args of [{limit:11},{limit:0},{limit:1.5},{q:'x'.repeat(129)},{afterCursor:'x'.repeat(2049)},{url:'https://evil.invalid'},{control:'secret'}]){
    test('rejects invalid public arguments '+JSON.stringify(args).slice(0,60),async()=>{
        const body=await call('tools/call',{name:'search_registry',arguments:args});assert.equal(body.error.code,-32602);
    });
}
test('rejects private tools and malformed lookup without touching the database',async()=>{
    for(const name of ['register_account','send_message','read_inbox']){
        assert.equal((await call('tools/call',{name,arguments:{}})).error.code,-32602);
    }
    assert.equal((await call('tools/call',{name:'lookup_address',arguments:{addressId:'../accounts/me'}})).error.code,-32602);
    assert.equal((await call('tools/call',{name:'demo_guide',arguments:null})).error.code,-32602);
});
test('notifications return 202 without invoking tools',async()=>{
    for(const value of [{jsonrpc:'2.0',method:'notifications/initialized'},{jsonrpc:'2.0',method:'tools/call',params:{name:'search_registry'}}]){
        const response=await remoteMcp(request(value),unused);assert.equal(response.status,202);assert.equal(await response.text(),'');
    }
});
test('ping and unsupported methods have JSON-RPC responses',async()=>{
    assert.deepEqual((await call('ping')).result,{});
    assert.equal((await call('resources/list')).error.code,-32601);
});
test('rejects foreign and null origins before reading the directory',async()=>{
    for(const origin of ['https://evil.invalid','null'])assert.equal((await remoteMcp(request(rpc('tools/list'),{origin}),unused)).status,403);
    assert.equal((await remoteMcp(request(rpc('tools/list'),{origin}),unused)).status,200);
});
test('requires JSON content and both Streamable HTTP Accept types',async()=>{
    assert.equal((await remoteMcp(request(rpc('ping'),{'content-type':'text/plain'}),unused)).status,415);
    for(const accept of ['application/json','text/event-stream','*/*','application/json, text/event-stream;q=0']){
        assert.equal((await remoteMcp(request(rpc('ping'),{accept}),unused)).status,406);
    }
});
test('rejects unsupported protocol headers',async()=>{
    assert.equal((await remoteMcp(request(rpc('ping'),{'mcp-protocol-version':'2099-01-01'}),unused)).status,400);
    for(const version of ['2025-11-25','2025-06-18','2025-03-26'])assert.equal((await remoteMcp(request(rpc('ping'),{'mcp-protocol-version':version}),unused)).status,200);
});
test('GET and DELETE are explicitly unsupported, not an auth fallback',async()=>{
    for(const method of ['GET','DELETE']){
        const response=await remoteMcp(new Request(origin+'/mcp',{method}),unused);
        assert.equal(response.status,405);assert.equal(response.headers.get('allow'),'POST');
    }
});
test('rejects batches, null ids, malformed envelopes and parameters',async()=>{
    for(const value of [[rpc('ping')],null,{method:'ping',id:1},{jsonrpc:'2.0',id:null,method:'ping'},rpc('ping',[])]){
        const response=await remoteMcp(request(value),unused);assert.equal(response.status,400);assert.equal((await response.json()).error.code,-32600);
    }
});
test('bounds request bytes including chunked bodies and rejects invalid JSON/UTF-8',async()=>{
    assert.equal((await remoteMcp(request(rpc('ping',{padding:'x'.repeat(16384)})),unused)).status,413);
    for(const body of ['{',new Uint8Array([0xff])]){
        const response=await remoteMcp(new Request(origin+'/mcp',{method:'POST',headers,body}),unused);
        assert.equal(response.status,400);assert.equal((await response.json()).error.code,-32700);
    }
    const stream=new ReadableStream({start(controller){controller.enqueue(new Uint8Array(10000));controller.enqueue(new Uint8Array(10000));controller.close();}});
    const response=await remoteMcp(new Request(origin+'/mcp',{method:'POST',headers,body:stream,duplex:'half'} as any),unused);
    assert.equal(response.status,413);
});
test('directory errors are tool errors and never disclose backend exception text',async()=>{
    for(const read of [async()=>new Response('private secret',{status:429}),async()=>{throw Error('private secret');}]){
        const body=await call('tools/call',{name:'search_registry',arguments:{}},read);
        assert.equal(body.result.isError,true);assert.doesNotMatch(JSON.stringify(body),/private secret/);
    }
});
test('bounds directory response bytes',async()=>{
    const body=await call('tools/call',{name:'search_registry'},async()=>new Response('x'.repeat(1024*1024+1)));
    assert.equal(body.result.isError,true);
});
