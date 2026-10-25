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
        checked=true;assert.equal(url,config.introspectionUrl);assert.equal(init.redirect,'error');assert.ok(init.body instanceof URLSearchParams);assert.equal(init.body.get('token'),'token');
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
