import assert from 'node:assert/strict';
import {test} from 'node:test';
import {authorizeOAuth,oauthConfiguration,protectedResourceMetadata,type OAuthConfiguration} from '../src/remote-oauth.ts';
const id='01234567-89ab-4def-8123-456789abcdef';
const config:OAuthConfiguration={issuer:'https://auth.example',resource:'https://weave.example/mcp',introspectionUrl:'https://auth.example/oauth/introspect',clientId:'weave-client',clientSecret:'a_secure_local_test_secret_value'};
const req=(token='token')=>new Request(config.resource,{method:'POST',headers:{Authorization:'Bearer '+token}});
const valid=(now:number)=>({active:true,iss:config.issuer,aud:config.resource,sub:id,scope:'weave:messages.read weave:messages.send',iat:now-15,exp:now+120});
function fetchFor(data:unknown, spy?:(input:any,init:any)=>void):typeof fetch {return (async(input:any,init:any)=>{spy?.(input,init);return new Response(JSON.stringify(data),{headers:{'Content-Type':'application/json'}})}) as typeof fetch;}
const exists=async(a:string)=>a===id;
test('only a complete resource-bound provider enables auth',()=>{
    assert.equal(oauthConfiguration({},config.resource),null);
    assert.throws(()=>oauthConfiguration({OAUTH_ISSUER:config.issuer},config.resource),/Incomplete/);
    assert.throws(()=>oauthConfiguration({OAUTH_ISSUER:config.issuer,OAUTH_RESOURCE:'https://evil.example/mcp',OAUTH_INTROSPECTION_URL:config.introspectionUrl,OAUTH_CLIENT_ID:config.clientId,OAUTH_CLIENT_SECRET:config.clientSecret},config.resource));
    assert.deepEqual((protectedResourceMetadata(config).headers.get('cache-control')),'no-store');
});
test('bearer-only credentials and resource discovery on 401',async()=>{
    let called=false;
    const r=await authorizeOAuth(config,new Request(config.resource), 'weave:messages.send',exists,fetchFor({},()=>{called=true}));
    assert.ok(r instanceof Response);assert.equal(r.status,401);assert.match(r.headers.get('WWW-Authenticate')??'',/resource_metadata=.*oauth-protected-resource\/mcp/);assert.equal(called,false);
});
test('authorization-server binding is checked via live introspection, never token passthrough',async()=>{
    const now=Math.floor(Date.now()/1000);let checked=false;
    const r=await authorizeOAuth(config,req(), 'weave:messages.send',exists,fetchFor(valid(now),(url,init)=>{
        checked=true;assert.equal(url,config.introspectionUrl);assert.equal(init.redirect,'manual');assert.ok(init.body instanceof URLSearchParams);assert.equal(init.body.get('token'),'token');
    }),now*1000);
    assert.equal(checked,true);assert.deepEqual(r,{accountId:id});
});
test('revocation, audience, account, issuer, expiry and scopes fail closed',async()=>{
    const now=Math.floor(Date.now()/1000),good=valid(now);
    const variants=[{...good,active:false},{...good,iss:'https://evil.example'},{...good,aud:'https://another.example/mcp'},
      {...good,sub:'another-account'},{...good,exp:now},{...good,exp:now+1800},{...good,iat:now+500},{...good,scope:'weave:messages.read'}];
    for(const data of variants){const res=await authorizeOAuth(config,req(),'weave:messages.send',exists,fetchFor(data),now*1000);
        assert.ok(res instanceof Response);assert.ok([401,403].includes(res.status));}
    const denied=await authorizeOAuth(config,req(),'weave:messages.send',async()=>false,fetchFor(good),now*1000);
    assert.ok(denied instanceof Response);assert.equal(denied.status,401);
});
test('invalid AS response never grants access, no cached revoked token',async()=>{
    const now=Math.floor(Date.now()/1000);let count=0;
    const f=fetchFor(valid(now),()=>{count++});
    assert.deepEqual(await authorizeOAuth(config,req(),'weave:messages.send',exists,f,now*1000),{accountId:id});
    const revoked=await authorizeOAuth(config,req(),'weave:messages.send',exists,fetchFor({...valid(now),active:false},()=>{count++}),now*1000);
    assert.ok(revoked instanceof Response);assert.equal(revoked.status,401);assert.equal(count,2);
    const error=await authorizeOAuth(config,req(),'weave:messages.send',exists,(async()=>{throw Error('AS secret failure')}) as typeof fetch,now*1000);
    assert.ok(error instanceof Response);assert.equal(error.status,503);assert.doesNotMatch(await error.text(),/secret/);
});
test('OAuth Basic form-encodes client ID and secret before transport',async()=>{
    const now=Math.floor(Date.now()/1000),special={...config,clientId:'weave+client',clientSecret:'secret+%value'};
    let observed='';
    const r=await authorizeOAuth(special,req(),'weave:messages.send',exists,fetchFor(valid(now),(_url,init)=>{
        observed=Buffer.from(String(init.headers.Authorization).slice(6),'base64').toString('utf8');
    }),now*1000);
    assert.deepEqual(r,{accountId:id});
    assert.equal(observed,'weave%2Bclient:secret%2B%25value');
});
test('bearer expiry during introspection fails closed',async()=>{
    const now=Math.floor(Date.now()/1000),payload={...valid(now),exp:now+1};let clock=now*1000;
    const fetcher=(async()=>{clock+=2000;return new Response(JSON.stringify(payload),{headers:{'Content-Type':'application/json'}});}) as typeof fetch;
    const r=await authorizeOAuth(config,req(),'weave:messages.send',exists,fetcher,()=>clock);
    assert.ok(r instanceof Response);assert.equal(r.status,401);
});
test('bearer expiry during account lookup fails closed',async()=>{
    const now=Math.floor(Date.now()/1000),payload={...valid(now),exp:now+1};let clock=now*1000;
    const delayed=async(a:string)=>{assert.equal(a,id);clock+=2000;return true;};
    const r=await authorizeOAuth(config,req(),'weave:messages.send',delayed,fetchFor(payload),()=>clock);
    assert.ok(r instanceof Response);assert.equal(r.status,401);
});


test('Cloudflare v1 Service Binding opt-in rejects partial and ambiguous OAuth modes',()=>{
    const binding={validateToken:async()=>null};
    const configured=oauthConfiguration({OAUTH_ISSUER:config.issuer,OAUTH_RESOURCE:config.resource,AUTH_SERVER:binding} as any,config.resource);
    assert.equal((configured as any)?.mode,'service');
    assert.equal((configured as any)?.resource,config.resource);
    assert.deepEqual((protectedResourceMetadata(configured!).headers.get('cache-control')),'no-store');
    for(const env of [
        {AUTH_SERVER:binding},
        {AUTH_SERVER:binding,OAUTH_ISSUER:config.issuer},
        {AUTH_SERVER:{},OAUTH_ISSUER:config.issuer,OAUTH_RESOURCE:config.resource},
        {AUTH_SERVER:binding,OAUTH_ISSUER:config.issuer,OAUTH_RESOURCE:config.resource,OAUTH_CLIENT_ID:config.clientId},
        {AUTH_SERVER:binding,OAUTH_ISSUER:config.issuer,OAUTH_RESOURCE:config.resource,OAUTH_INTROSPECTION_URL:config.introspectionUrl,OAUTH_CLIENT_ID:config.clientId,OAUTH_CLIENT_SECRET:config.clientSecret}
    ])assert.throws(()=>oauthConfiguration(env as any,config.resource),'partial, malformed, or ambiguous providers must fail closed');
    assert.throws(()=>oauthConfiguration({AUTH_SERVER:binding,OAUTH_ISSUER:config.issuer,OAUTH_RESOURCE:config.resource} as any,'https://evil.example/mcp'));
});
test('Service Binding verifies exact resource, owner identity and operation scope without HTTP introspection',async()=>{
    const now=Math.floor(Date.now()/1000);let seen:unknown=null,lookups=0;
    const binding={validateToken:async(resource:string,token:string)=>{
        seen={resource,token};return {audience:config.resource,userId:id,props:{accountId:id},
            expiresAt:now+120,scope:['weave:messages.send'],clientId:'verified-public-client'};
    }};
    const cfg=oauthConfiguration({AUTH_SERVER:binding,OAUTH_ISSUER:config.issuer,OAUTH_RESOURCE:config.resource} as any,config.resource)!;
    const result=await authorizeOAuth(cfg as any,req(id+':grantId:secretValue'),'weave:messages.send',
        async account=>{lookups++;return account===id;},
        (async()=>{throw Error('Never perform public token introspection in service mode');}) as typeof fetch,now*1000);
    assert.deepEqual(result,{accountId:id});
    assert.deepEqual(seen,{resource:config.resource,token:id+':grantId:secretValue'});
    assert.equal(lookups,1);
});
test('Service Binding rejects revoked, foreign resource, foreign account, mismatched props and missing scope',async()=>{
    const now=Math.floor(Date.now()/1000);
    const valid={audience:config.resource,userId:id,props:{accountId:id},
        expiresAt:now+120,scope:['weave:messages.send'],clientId:'verified-public-client'};
    const cases:[unknown,number][]=[
        [null,401],
        [{...valid,audience:'https://other.example/mcp'},401],
        [{...valid,userId:'another-owner'},401],
        [{...valid,props:{accountId:'ffffffff-ffff-ffff-ffff-ffffffffffff'}},401],
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
        const result=await authorizeOAuth(cfg as any,req(id+':grantId:secretValue'),'weave:messages.send',exists,undefined,now*1000);
        assert.ok(result instanceof Response);
        assert.equal(result.status,status);
    }
    const binding={validateToken:async()=>valid};
    const cfg=oauthConfiguration({AUTH_SERVER:binding,OAUTH_ISSUER:config.issuer,OAUTH_RESOURCE:config.resource} as any,config.resource)!;
    const denied=await authorizeOAuth(cfg as any,req(id+':grantId:secretValue'),'weave:messages.send',async()=>false,undefined,now*1000);
    assert.ok(denied instanceof Response);assert.equal(denied.status,401);
});
test('Service Binding failure is 503 and never leaks upstream error details or dispatches account read',async()=>{
    let checked=0;
    const binding={validateToken:async()=>{throw Error('private binding unavailable SECRET');}};
    const cfg=oauthConfiguration({AUTH_SERVER:binding,OAUTH_ISSUER:config.issuer,OAUTH_RESOURCE:config.resource} as any,config.resource)!;
    const result=await authorizeOAuth(cfg as any,req(id+':grantId:secretValue'),'weave:messages.send',async()=>{checked++;return true;});
    assert.ok(result instanceof Response);assert.equal(result.status,503);assert.equal(checked,0);
    assert.doesNotMatch(await result.text(),/SECRET|binding/);
});
test('Service Binding denies a token that expires while the account lookup executes',async()=>{
    const now=Math.floor(Date.now()/1000);let clock=now*1000;
    const binding={validateToken:async()=>({audience:config.resource,userId:id,props:{accountId:id},
        expiresAt:now+1,scope:['weave:messages.send'],clientId:'verified-public-client'})};
    const cfg=oauthConfiguration({AUTH_SERVER:binding,OAUTH_ISSUER:config.issuer,OAUTH_RESOURCE:config.resource} as any,config.resource)!;
    const result=await authorizeOAuth(cfg as any,req(id+':grantId:secretValue'),'weave:messages.send',async()=>{clock+=2000;return true;},undefined,()=>clock);
    assert.ok(result instanceof Response);assert.equal(result.status,401);
});

test('Service Binding rechecks revocation rather than caching an accepted token',async()=>{
    const now=Math.floor(Date.now()/1000);
    const validClaims={audience:config.resource,userId:id,props:{accountId:id},
        expiresAt:now+120,scope:['weave:messages.send'],clientId:'verified-public-client'};
    let reads=0,revoked=false;
    const binding={validateToken:async()=>{reads++;return revoked?null:validClaims;}};
    const cfg=oauthConfiguration({AUTH_SERVER:binding,OAUTH_ISSUER:config.issuer,OAUTH_RESOURCE:config.resource} as any,config.resource)!;
    const request=req(id+':grantId:secretValue');
    assert.deepEqual(await authorizeOAuth(cfg,request,'weave:messages.send',exists,undefined,now*1000),{accountId:id});
    revoked=true;
    const denied=await authorizeOAuth(cfg,request,'weave:messages.send',exists,undefined,now*1000);
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
    const binding={validateToken:async()=>{clock+=2000;return {audience:config.resource,userId:id,props:{accountId:id},
        expiresAt:now+1,scope:['weave:messages.send'],clientId:'verified-public-client'};}};
    const cfg=oauthConfiguration({AUTH_SERVER:binding,OAUTH_ISSUER:config.issuer,OAUTH_RESOURCE:config.resource} as any,config.resource)!;
    let lookedUp=false;
    const denied=await authorizeOAuth(cfg,req(id+':grantId:secretValue'),'weave:messages.send',async()=>{lookedUp=true;return true;},undefined,()=>clock);
    assert.ok(denied instanceof Response);assert.equal(denied.status,401);assert.equal(lookedUp,false);
});

test('provider-neutral RFC7662 subject links only through an explicitly verified issuer/subject mapping',async()=>{
    const now=Math.floor(Date.now()/1000),externalSubject='tenant-specific-opaque-subject';
    const introspected={...valid(now),sub:externalSubject};
    const principal={issuer:config.issuer,subject:externalSubject,resource:config.resource};
    let mapped=0,lookups=0;
    const resolve=async(value:typeof principal)=>{assert.deepEqual(value,principal);mapped++;return id;};
    const accountExists=async(value:string)=>{lookups++;return value===id;};
    const before=await authorizeOAuth(config,req(),'weave:messages.send',accountExists,fetchFor(introspected),now*1000);
    assert.ok(before instanceof Response);assert.equal(before.status,401);
    // Explicit mapping is an opt-in, never inferred from a matching email or UUID.
    const linked=await authorizeOAuth(config,req(),'weave:messages.send',accountExists,fetchFor(introspected),now*1000,resolve);
    assert.deepEqual(linked,{accountId:id});assert.equal(mapped,1);assert.equal(lookups,1);
});
test('provider-neutral RFC7662 link is fail-closed for wrong issuer, missing link, account and mapping outage',async()=>{
    const now=Math.floor(Date.now()/1000),opaque='external-identity',good={...valid(now),sub:opaque};
    let mapped=0;
    const resolver=async(v:{issuer:string;subject:string;resource:string})=>{mapped++;return v.issuer===config.issuer&&v.subject===opaque&&v.resource===config.resource?id:null;};
    const call=(data:unknown,lookup=resolver,account=exists)=>authorizeOAuth(
        config,req(),'weave:messages.send',account,fetchFor(data),now*1000,lookup);
    const foreign=await call({...good,iss:'https://foreign-issuer.example'});
    assert.ok(foreign instanceof Response);assert.equal(foreign.status,401);assert.equal(mapped,0);
    for(const [lookup,account,status] of [
        [async()=>null,exists,401],
        [resolver,async()=>false,401],
        [async()=>{throw Error('secret-link-store-down');},exists,503]
    ] as const) {
        const denied=await call(good,lookup,account);
        assert.ok(denied instanceof Response);assert.equal(denied.status,status);
        assert.doesNotMatch(await denied.text(),/secret-link-store-down/);
    }
    // When an explicit map exists, even a UUID-like external subject must not
    // implicitly become its identically named Weave account.
    const uuidSub=await call(valid(now),async()=>null);
    assert.ok(uuidSub instanceof Response);assert.equal(uuidSub.status,401);
});
test('provider-neutral RFC7662 mapping never resurrects revoked or expired grants',async()=>{
    const now=Math.floor(Date.now()/1000),data={...valid(now),sub:'external-owner',exp:now+1};
    let clock=now*1000;
    const resolver=async()=>{clock+=2000;return id;};
    const expired=await authorizeOAuth(config,req(),'weave:messages.send',exists,fetchFor(data),()=>clock,resolver);
    assert.ok(expired instanceof Response);assert.equal(expired.status,401);
    const revoked=await authorizeOAuth(config,req(),'weave:messages.send',exists,fetchFor({...data,active:false,exp:now+120}),now*1000,resolver);
    assert.ok(revoked instanceof Response);assert.equal(revoked.status,401);
});

test('two AS issuers using the same opaque subject cannot cross-link accounts',async()=>{
    const now=Math.floor(Date.now()/1000),sub='same-subject-at-two-issuers';
    const another={...config,issuer:'https://other-auth.example',
        introspectionUrl:'https://other-auth.example/oauth/introspect'};
    const second='ffffffff-ffff-4fff-8fff-ffffffffffff';
    const links=new Map([[config.issuer+'|'+sub,id],[another.issuer+'|'+sub,second]]);
    const resolver=async(p:{issuer:string;subject:string;resource:string})=>{
        assert.equal(p.resource,config.resource);
        return links.get(p.issuer+'|'+p.subject)??null;
    };
    const existsAccount=async(value:string)=>value===id||value===second;
    const claims={...valid(now),sub};
    assert.deepEqual(await authorizeOAuth(config,req(),'weave:messages.send',existsAccount,fetchFor(claims),now*1000,resolver),{accountId:id});
    assert.deepEqual(await authorizeOAuth(another,req(),'weave:messages.send',existsAccount,fetchFor({...claims,iss:another.issuer}),now*1000,resolver),{accountId:second});
    const swapped=await authorizeOAuth(another,req(),'weave:messages.send',existsAccount,fetchFor(claims),now*1000,resolver);
    assert.ok(swapped instanceof Response);assert.equal(swapped.status,401);
});

test('RFC7662 introspection rejects redirects without forwarding the bearer or Basic credentials',async()=>{
    const now=Math.floor(Date.now()/1000);
    let outgoing=0,accountReads=0;
    const redirectOnly=(async(url:unknown,init:RequestInit)=>{
        outgoing++;
        assert.equal(url,config.introspectionUrl);
        assert.equal(init.redirect,'manual','workerd must not follow or throw on the redirect policy');
        assert.equal(init.method,'POST');
        return new Response(null,{status:302,headers:{Location:'https://untrusted.example/steal'}});
    }) as typeof fetch;
    const answer=await authorizeOAuth(config,req('synthetic-opaque-token'),'weave:messages.send',
        async()=>{accountReads++;return true;},redirectOnly,now*1000);
    assert.ok(answer instanceof Response);
    assert.equal(answer.status,503,'an upstream 3xx must fail closed');
    assert.equal(outgoing,1,'never follow the redirect destination');
    assert.equal(accountReads,0,'redirects cannot establish an authorized account');
});
