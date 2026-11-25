import assert from 'node:assert/strict';
import {test} from 'node:test';
import {authorizeOAuth,oauthConfiguration,protectedResourceMetadata} from '../src/remote-oauth.ts';
const id='01234567-89ab-4def-8123-456789abcdef';
const config={issuer:'https://auth.example',resource:'https://weave.example/mcp'};
const req=(token='token')=>new Request(config.resource,{method:'POST',headers:{Authorization:'Bearer '+token}});
const humanId='fedcba98-7654-4321-8765-43210fedcba9',grantId='0f1e2d3c-4b5a-4968-8776-a5b4c3d2e1f0';
// The mirrored consent grant behind every Service Binding token below.
const exists=async(g:string)=>g===grantId?{accountId:id,humanId,clientId:'verified-public-client'}:null;
const none=async()=>null;
test('no OAuth settings leave private tools off; the retired RFC 7662 settings are ignored',()=>{
    assert.equal(oauthConfiguration({},config.resource),null);
    // The external introspection mode is deleted: its old variables neither configure nor enable anything.
    assert.equal(oauthConfiguration({OAUTH_INTROSPECTION_URL:'https://auth.example/oauth/introspect',OAUTH_CLIENT_ID:'weave-client',
        OAUTH_CLIENT_SECRET:'a_secure_local_test_secret_value',OAUTH_INTROSPECTION_TRUSTED_ORIGIN:'https://auth.example'} as any,config.resource),null);
});
test('bearer-only credentials and resource discovery on 401',async()=>{
    let called=false;
    const cfg=oauthConfiguration({AUTH_SERVER:{validateToken:async()=>{called=true;return null;}},OAUTH_ISSUER:config.issuer,OAUTH_RESOURCE:config.resource},config.resource)!;
    const r=await authorizeOAuth(cfg,new Request(config.resource),'weave:messages.send',exists);
    assert.ok(r instanceof Response);assert.equal(r.status,401);assert.match(r.headers.get('WWW-Authenticate')??'',/resource_metadata=.*oauth-protected-resource\/mcp/);assert.equal(called,false);
});
test('only a complete, resource-bound Service Binding enables auth; anything partial fails closed',()=>{
    const binding={validateToken:async()=>null};
    const configured=oauthConfiguration({OAUTH_ISSUER:config.issuer,OAUTH_RESOURCE:config.resource,AUTH_SERVER:binding} as any,config.resource);
    assert.equal(configured?.resource,config.resource);
    assert.equal(configured?.issuer,config.issuer);
    assert.deepEqual((protectedResourceMetadata(configured!).headers.get('cache-control')),'no-store');
    for(const env of [
        {AUTH_SERVER:binding},
        {AUTH_SERVER:binding,OAUTH_ISSUER:config.issuer},
        {AUTH_SERVER:{},OAUTH_ISSUER:config.issuer,OAUTH_RESOURCE:config.resource},
        {OAUTH_ISSUER:config.issuer},
        {OAUTH_RESOURCE:config.resource},
        {OAUTH_ISSUER:config.issuer,OAUTH_RESOURCE:config.resource},
        {AUTH_SERVER:binding,OAUTH_ISSUER:'http://auth.example',OAUTH_RESOURCE:config.resource},
        {AUTH_SERVER:binding,OAUTH_ISSUER:config.issuer,OAUTH_RESOURCE:'https://weave.example/other'}
    ])assert.throws(()=>oauthConfiguration(env as any,config.resource),'partial, malformed, or ambiguous providers must fail closed');
    assert.throws(()=>oauthConfiguration({AUTH_SERVER:binding,OAUTH_ISSUER:config.issuer,OAUTH_RESOURCE:config.resource} as any,'https://evil.example/mcp'));
});
test('Service Binding verifies exact resource, owner identity and operation scope',async()=>{
    const now=Math.floor(Date.now()/1000);let seen:unknown=null,lookups=0;
    const binding={validateToken:async(resource:string,token:string)=>{
        seen={resource,token};return {audience:config.resource,userId:id,props:{accountId:id,humanId,grantId},
            expiresAt:now+120,scope:['weave:messages.send'],clientId:'verified-public-client'};
    }};
    const cfg=oauthConfiguration({AUTH_SERVER:binding,OAUTH_ISSUER:config.issuer,OAUTH_RESOURCE:config.resource} as any,config.resource)!;
    const result=await authorizeOAuth(cfg,req(id+':grantId:secretValue'),'weave:messages.send',
        async grant=>{lookups++;return exists(grant);},now*1000);
    assert.deepEqual(result,{accountId:id,grantId});
    assert.deepEqual(seen,{resource:config.resource,token:id+':grantId:secretValue'});
    assert.equal(lookups,1);
});
test('Service Binding rejects revoked, foreign resource, foreign account, mismatched props and missing scope',async()=>{
    const now=Math.floor(Date.now()/1000);
    const valid={audience:config.resource,userId:id,props:{accountId:id,humanId,grantId},
        expiresAt:now+120,scope:['weave:messages.send'],clientId:'verified-public-client'};
    const cases:[unknown,number][]=[
        [null,401],
        [{...valid,audience:'https://other.example/mcp'},401],
        [{...valid,userId:'another-owner'},401],
        [{...valid,props:{accountId:'ffffffff-ffff-ffff-ffff-ffffffffffff',humanId,grantId}},401],
        [{...valid,props:{accountId:id}},401],
        [{...valid,props:{accountId:id,humanId}},401],
        [{...valid,props:{accountId:id,grantId}},401],
        [{...valid,clientId:'another-client'},401],
        [{...valid,props:null},401],
        [{...valid,expiresAt:now},401],
        [{...valid,expiresAt:now+3600},401],
        [{...valid,expiresAt:Date.now()+120000},401],
        [{...valid,scope:['weave:messages.read']},403],
        [{...valid,scope:'weave:messages.send'},401]
    ];
    for(const [claims,status] of cases){
        const binding={validateToken:async()=>claims};
        const cfg=oauthConfiguration({AUTH_SERVER:binding,OAUTH_ISSUER:config.issuer,OAUTH_RESOURCE:config.resource} as any,config.resource)!;
        const result=await authorizeOAuth(cfg,req(id+':grantId:secretValue'),'weave:messages.send',exists,now*1000);
        assert.ok(result instanceof Response);
        assert.equal(result.status,status);
    }
    const binding={validateToken:async()=>valid};
    const cfg=oauthConfiguration({AUTH_SERVER:binding,OAUTH_ISSUER:config.issuer,OAUTH_RESOURCE:config.resource} as any,config.resource)!;
    const denied=await authorizeOAuth(cfg,req(id+':grantId:secretValue'),'weave:messages.send',none,now*1000);
    assert.ok(denied instanceof Response);assert.equal(denied.status,401);
});
test('Service Binding failure is 503 and never leaks upstream error details or dispatches grant read',async()=>{
    let checked=0;
    const binding={validateToken:async()=>{throw Error('private binding unavailable SECRET');}};
    const cfg=oauthConfiguration({AUTH_SERVER:binding,OAUTH_ISSUER:config.issuer,OAUTH_RESOURCE:config.resource} as any,config.resource)!;
    const result=await authorizeOAuth(cfg,req(id+':grantId:secretValue'),'weave:messages.send',async()=>{checked++;return exists(grantId);});
    assert.ok(result instanceof Response);assert.equal(result.status,503);assert.equal(checked,0);
    assert.doesNotMatch(await result.text(),/SECRET|binding/);
});
test('Service Binding denies a token that expires while the grant lookup executes',async()=>{
    const now=Math.floor(Date.now()/1000);let clock=now*1000;
    const binding={validateToken:async()=>({audience:config.resource,userId:id,props:{accountId:id,humanId,grantId},
        expiresAt:now+1,scope:['weave:messages.send'],clientId:'verified-public-client'})};
    const cfg=oauthConfiguration({AUTH_SERVER:binding,OAUTH_ISSUER:config.issuer,OAUTH_RESOURCE:config.resource} as any,config.resource)!;
    const result=await authorizeOAuth(cfg,req(id+':grantId:secretValue'),'weave:messages.send',async g=>{clock+=2000;return exists(g);},()=>clock);
    assert.ok(result instanceof Response);assert.equal(result.status,401);
});

test('Service Binding rechecks revocation rather than caching an accepted token',async()=>{
    const now=Math.floor(Date.now()/1000);
    const validClaims={audience:config.resource,userId:id,props:{accountId:id,humanId,grantId},
        expiresAt:now+120,scope:['weave:messages.send'],clientId:'verified-public-client'};
    let reads=0,revoked=false;
    const binding={validateToken:async()=>{reads++;return revoked?null:validClaims;}};
    const cfg=oauthConfiguration({AUTH_SERVER:binding,OAUTH_ISSUER:config.issuer,OAUTH_RESOURCE:config.resource} as any,config.resource)!;
    const request=req(id+':grantId:secretValue');
    assert.deepEqual(await authorizeOAuth(cfg,request,'weave:messages.send',exists,now*1000),{accountId:id,grantId});
    revoked=true;
    const denied=await authorizeOAuth(cfg,request,'weave:messages.send',exists,now*1000);
    assert.ok(denied instanceof Response);assert.equal(denied.status,401);assert.equal(reads,2);
});
test('Service Binding rejects a non-Cloudflare token before private RPC',async()=>{
    let touched=0;
    const binding={validateToken:async()=>{touched++;return null;}};
    const cfg=oauthConfiguration({AUTH_SERVER:binding,OAUTH_ISSUER:config.issuer,OAUTH_RESOURCE:config.resource} as any,config.resource)!;
    const denied=await authorizeOAuth(cfg,req('plain_legacy_token'),'weave:messages.send',exists);
    assert.ok(denied instanceof Response);assert.equal(denied.status,401);assert.equal(touched,0);
});
test('Service Binding rechecks expiry after the private RPC latency',async()=>{
    const now=Math.floor(Date.now()/1000);let clock=now*1000;
    const binding={validateToken:async()=>{clock+=2000;return {audience:config.resource,userId:id,props:{accountId:id,humanId,grantId},
        expiresAt:now+1,scope:['weave:messages.send'],clientId:'verified-public-client'};}};
    const cfg=oauthConfiguration({AUTH_SERVER:binding,OAUTH_ISSUER:config.issuer,OAUTH_RESOURCE:config.resource} as any,config.resource)!;
    let lookedUp=false;
    const denied=await authorizeOAuth(cfg,req(id+':grantId:secretValue'),'weave:messages.send',async g=>{lookedUp=true;return exists(g);},()=>clock);
    assert.ok(denied instanceof Response);assert.equal(denied.status,401);assert.equal(lookedUp,false);
});
