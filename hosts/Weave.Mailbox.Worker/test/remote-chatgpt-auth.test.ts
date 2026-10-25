import assert from 'node:assert/strict';
import {test} from 'node:test';
import {build} from 'esbuild';
import {remoteMcp, type PrivateAccess} from '../src/remote-mcp.ts';
import {authorizeOAuth, type OAuthConfiguration} from '../src/remote-oauth.ts';
import {start} from './harness.ts';
import type {Env} from '../src/database.ts';

const compiled=await build({entryPoints:['src/remote-private.ts'],bundle:true,write:false,platform:'node',format:'esm',logLevel:'silent'});
const {privateMcpTools,privateScope,executePrivateMcp}=await import(
    'data:text/javascript;base64,'+Buffer.from(compiled.outputFiles[0].contents).toString('base64')
) as typeof import('../src/remote-private.ts');

const origin='https://demo.test';
const config:OAuthConfiguration={
    issuer:'https://auth.test',resource:origin+'/mcp',
    introspectionUrl:'https://auth.test/oauth/introspect',
    clientId:'synthetic-client',clientSecret:'synthetic_only_not_a_real_secret'
};
const accountId='01234567-89ab-4def-8123-456789abcdef';
const headers={'Content-Type':'application/json',Accept:'application/json, text/event-stream'};
const rpc=(name:string)=>({jsonrpc:'2.0',id:17,method:'tools/call',params:{name,arguments:{}}});
const request=(value:unknown,bearer?:string)=>new Request(config.resource,{method:'POST',
    headers:{...headers,...(bearer?{Authorization:'Bearer '+bearer}:{})},body:JSON.stringify(value)});
const unused=async(_url:URL):Promise<Response>=>{throw Error('Public directory must not be touched');};
const env=(db:Env['db']):Env=>({db,REGISTRATION_ENABLED:'true',REGISTRATION_MAX_ACCOUNTS:'100',
    REGISTRATION_PER_IP_PER_HOUR:'10',REGISTRATION_GLOBAL_PER_HOUR:'100',
    CLEANUP_MAX_MESSAGES:'0',CLEANUP_TIME_BUDGET_MS:'0'});
const introspection=(scopes:string)=>async()=>new Response(JSON.stringify({
    active:true,iss:config.issuer,aud:config.resource,sub:accountId,
    scope:scopes,iat:Math.floor(Date.now()/1000)-10,exp:Math.floor(Date.now()/1000)+120
}),{headers:{'Content-Type':'application/json'}});

test('missing bearer preserves HTTP 401 and supplies ChatGPT tool-level OAuth challenge without dispatch',async()=>{
    const incoming=request(rpc('send_message'));let called=0;
    const access:PrivateAccess={tools:privateMcpTools,scope:privateScope,
        authorize:scope=>authorizeOAuth(config,incoming,scope,async()=>true,
            (async()=>{throw Error('introspection must not run without token');}) as typeof fetch),
        invoke:async()=>{called++;return new Response('{}');}};
    const response=await remoteMcp(incoming,unused,access);
    assert.equal(response.status,401);
    const challenge=response.headers.get('WWW-Authenticate')??'';
    assert.match(challenge,/resource_metadata="https:\/\/demo\.test\/\.well-known\/oauth-protected-resource\/mcp"/);
    assert.match(challenge,/error="invalid_token"/);
    assert.match(challenge,/error_description="[^"]+"/);
    assert.match(challenge,/scope="weave:messages.send"/);
    const body=await response.json();
    assert.equal(body.jsonrpc,'2.0');assert.equal(body.id,17);
    assert.equal(body.result?.isError,true,'linking needs an MCP error result on the same denied request');
    assert.deepEqual(body.result?._meta?.['mcp/www_authenticate'],[challenge]);
    assert.equal(called,0,'no private write may execute');
    assert.doesNotMatch(JSON.stringify(body),/synthetic_only_not_a_real_secret/);
    assert.equal(response.headers.get('cache-control'),'no-store');
    assert.equal(response.headers.get('pragma'),'no-cache');
    assert.equal(response.headers.get('x-content-type-options'),'nosniff');
});

test('insufficient scope preserves HTTP 403 and gives bounded step-up challenge, never invoking tool',async()=>{
    const incoming=request(rpc('send_message'),'synthetic_access_token');let calls=0;
    const access:PrivateAccess={tools:privateMcpTools,scope:privateScope,
        authorize:scope=>authorizeOAuth(config,incoming,scope,async()=>true,
            introspection('weave:messages.read') as typeof fetch),
        invoke:async()=>{calls++;return new Response('{}');}};
    const response=await remoteMcp(incoming,unused,access);
    assert.equal(response.status,403);
    const challenge=response.headers.get('WWW-Authenticate')??'';
    assert.match(challenge,/error="insufficient_scope"/);
    assert.match(challenge,/error_description="[^"]+"/);
    assert.match(challenge,/scope="weave:messages.send"/);
    const body=await response.json();
    assert.equal(body.result?.isError,true);
    assert.deepEqual(body.result?._meta?.['mcp/www_authenticate'],[challenge]);
    assert.equal(calls,0);
    assert.doesNotMatch(JSON.stringify(body),/synthetic_access_token/);
});

test('introspection outage remains HTTP 503 with no misleading OAuth linking UI',async()=>{
    const incoming=request(rpc('send_message'),'synthetic_access_token');let writes=0;
    const access:PrivateAccess={tools:privateMcpTools,scope:privateScope,
        authorize:scope=>authorizeOAuth(config,incoming,scope,async()=>true,
            (async()=>{throw Error('provider unavailable with internal secret');}) as typeof fetch),
        invoke:async()=>{writes++;return new Response('{}');}};
    const response=await remoteMcp(incoming,unused,access);
    assert.equal(response.status,503);
    assert.equal(response.headers.get('WWW-Authenticate'),null);
    assert.equal(writes,0);
    assert.doesNotMatch(await response.text(),/internal secret|mcp\/www_authenticate/);
});

test('configured discovery and guide describe OAuth-capable private messaging without claiming login',async()=>{
    const access:PrivateAccess={tools:privateMcpTools,scope:privateScope,
        authorize:async()=>{throw Error('no auth required for public tools/list or guide');},
        invoke:async()=>{throw Error('no private invocation for public tools');}};
    const list=await remoteMcp(request({jsonrpc:'2.0',id:1,method:'tools/list'}),unused,access);
    assert.equal(list.status,200);
    const tools=(await list.json()).result.tools as Array<{name:string,description:string}>;
    assert.ok(tools.some(t=>t.name==='send_message'));
    assert.match(tools.find(t=>t.name==='demo_guide')?.description??'',/OAuth/i);
    const initialized=await remoteMcp(request({jsonrpc:'2.0',id:2,method:'initialize',
        params:{protocolVersion:'2025-11-25',capabilities:{},clientInfo:{name:'test',version:'1'}}}),unused,access);
    const init=(await initialized.json()).result;
    assert.match(init.instructions,/OAuth/);
    const guide=await remoteMcp(request(rpc('demo_guide')),unused,access);
    const data=JSON.parse((await guide.json()).result.content[0].text);
    assert.equal(data.authenticated,false);
    assert.equal(data.privateMessaging,true,'enabled OAuth makes per-user private tools available after linking');
    assert.match(data.nextRequirement,/OAuth/);
});

test('OAuth-only get_my_profile is declared for stable multi-account recognition',async()=>{
    const tool=privateMcpTools.find(t=>t.name==='get_my_profile');
    assert.ok(tool,'authenticated profile metadata must be discoverable');
    assert.deepEqual(tool.securitySchemes,[{type:'oauth2',scopes:['weave:addresses.read']}]);
    assert.equal(tool._meta['openai/profile'],true);
    assert.deepEqual(tool.inputSchema.required,[]);
    assert.deepEqual(tool.outputSchema.required,['id']);
    assert.equal(tool.outputSchema.properties.id.pattern,'\\S','nonblank identity pattern must survive JavaScript string escaping');
    assert.equal(tool.outputSchema.additionalProperties,false);
    assert.equal(privateScope('get_my_profile'),'weave:addresses.read');
});

test('authenticated profile returns only its own stable account ID through real Worker D1 and MCP',async()=>{
    const h=await start();
    try{
        const e=env(h.db),a=await h.register(),b=await h.register();
        const own=await executePrivateMcp(e,origin,a.accountId,'get_my_profile',{});
        assert.equal(own.status,200,await own.clone().text());
        assert.deepEqual(await own.json(),{id:a.accountId});
        const other=await executePrivateMcp(e,origin,b.accountId,'get_my_profile',{});
        assert.equal(other.status,200);
        assert.deepEqual(await other.json(),{id:b.accountId});
        const access:PrivateAccess={tools:privateMcpTools,scope:privateScope,
            authorize:async scope=>{assert.equal(scope,'weave:addresses.read');return a.accountId;},
            invoke:(owner,name,args)=>executePrivateMcp(e,origin,owner,name,args)};
        const response=await remoteMcp(request(rpc('get_my_profile')),unused,access);
        assert.equal(response.status,200);
        const result=(await response.json()).result;
        assert.deepEqual(result.structuredContent,{id:a.accountId});
        assert.deepEqual(JSON.parse(result.content[0].text),{id:a.accountId});
        assert.equal(result.isError,false);
        const again=await remoteMcp(request(rpc('get_my_profile')),unused,access);
        assert.deepEqual((await again.json()).result.structuredContent,{id:a.accountId});
    }finally{await h.close();}
});

test('unauthorized MCP challenge forwards only approved auth headers, never arbitrary provider details',async()=>{
    const challenge='Bearer resource_metadata="https://demo.test/.well-known/oauth-protected-resource/mcp", error="invalid_token", error_description="Authentication is required", scope="weave:messages.send"';
    const access:PrivateAccess={tools:privateMcpTools,scope:privateScope,
        authorize:async()=>new Response('internal-secret-response',{status:401,headers:{
            'WWW-Authenticate':challenge,'Cache-Control':'no-store','X-Internal-Trace':'private-secret-marker'
        }}),invoke:async()=>{throw Error('must not be invoked')}};
    const response=await remoteMcp(request(rpc('send_message')),unused,access);
    assert.equal(response.status,401);
    assert.equal(response.headers.get('WWW-Authenticate'),challenge);
    assert.equal(response.headers.get('X-Internal-Trace'),null);
    assert.equal(response.headers.get('Cache-Control'),'no-store','duplicate cache directives are invalid');
    assert.equal(response.headers.get('Pragma'),'no-cache');
    assert.doesNotMatch(await response.text(),/internal-secret-response|private-secret-marker/);
});
