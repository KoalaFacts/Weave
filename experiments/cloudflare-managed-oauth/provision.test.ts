import test from 'node:test';
import assert from 'node:assert/strict';
import {provisionTrial} from './provision.ts';

const account='a'.repeat(32);
const appId='11111111-2222-3333-4444-555555555555';
const target='demo.weave-389.workers.dev/access-trial/*';
const emailA='agent-one@example.org', emailB='agent-two@example.org';
const env={
  CLOUDFLARE_ACCOUNT_ID:account,CLOUDFLARE_API_TOKEN:'test-worker-token',
  CLOUDFLARE_ACCESS_TRIAL_TOKEN:'test-access-token',
  WEAVE_ACCESS_TRIAL_EMAILS:emailA+','+emailB,
  WEAVE_ACCESS_TRIAL_COMMIT:'create-weave-access-trial',
  GITHUB_REPOSITORY:'KoalaFacts/Weave',GITHUB_REF:'refs/heads/main',
  GITHUB_EVENT_NAME:'workflow_dispatch'
};
const readOnly=()=>Promise.resolve({outcome:'readable',apps:{complete:true,trial:[]},organization:{configured:true,authDomain:'team.cloudflareaccess.com'}});
const withExisting=()=>Promise.resolve({outcome:'readable',apps:{complete:true,trial:[{id:appId,managedOAuth:false,matchesTrialName:true}]},organization:{configured:true,authDomain:'team.cloudflareaccess.com'}});
const envelope=(result:unknown)=>Response.json({success:true,result});
function fakeCloud(){
 const calls:{path:string;method:string;body:unknown;authorization:string|null;redirect:RequestRedirect|undefined}[]=[];
 const fetchImpl:typeof fetch=async (input,init={})=>{
   const url=new URL(String(input));
   assert.equal(url.origin,'https://api.cloudflare.com');
   assert.equal(url.pathname.startsWith('/client/v4/accounts/'+account+'/access/apps'),true);
   const method=init.method??'GET',path=url.pathname.slice(('/client/v4/accounts/'+account).length);
   const body=init.body?JSON.parse(String(init.body)):undefined;
   const authorization=new Headers(init.headers).get('Authorization');
   calls.push({path,method,body,authorization,redirect:init.redirect});
   if(method==='POST'&&path==='/access/apps')return envelope({id:appId,name:'Weave Managed OAuth Trial'});
   if(method==='GET'&&path==='/access/apps/'+appId)return envelope({
      id:appId,name:'Weave Managed OAuth Trial',type:'self_hosted',domain:target,
      destinations:[{type:'public',uri:target}],app_launcher_visible:false,oauth_configuration:{enabled:true,
      dynamic_client_registration:{enabled:true,allow_any_on_localhost:true,allow_any_on_loopback:true,allowed_uris:[]},
      grant:{access_token_lifetime:'15m',session_duration:'24h'}}
   });
   if(method==='GET'&&path==='/access/apps/'+appId+'/policies')return envelope([{name:'Weave trial exact identities',decision:'allow',include:[
      {email:{email:emailA}},{email:{email:emailB}}
   ]}]);
   throw Error('unexpected path: '+path);
 };
 return {calls,fetchImpl};
}

test('missing token, identities, dispatch authorization or ambiguous identities fail before any cloud call',async()=>{
 for(const bad of [
    {CLOUDFLARE_ACCESS_TRIAL_TOKEN:undefined},{WEAVE_ACCESS_TRIAL_EMAILS:undefined},
    {WEAVE_ACCESS_TRIAL_EMAILS:emailA},{WEAVE_ACCESS_TRIAL_EMAILS:emailA+','+emailA.toUpperCase()},
    {WEAVE_ACCESS_TRIAL_EMAILS:'everyone@example.org,*@example.org'},
    {WEAVE_ACCESS_TRIAL_COMMIT:'anything'},{GITHUB_REF:'refs/heads/feature'},
    {GITHUB_REPOSITORY:'other/repo'}, {GITHUB_EVENT_NAME:'push'}]){
     let read=0;const cloud=fakeCloud();
     await assert.rejects(provisionTrial({env:{...env,...bad},inspect:async()=>{read++;return readOnly();},fetchImpl:cloud.fetchImpl}));
     assert.equal(read,0);assert.equal(cloud.calls.length,0);
 }
});

test('unreadable Access or an existing candidate refuses to write',async()=>{
 for(const inspect of [withExisting,()=>Promise.resolve({outcome:'blocked',apps:{complete:false,trial:[]},organization:{configured:false,authDomain:null}})]){
    const cloud=fakeCloud();await assert.rejects(provisionTrial({env,inspect,fetchImpl:cloud.fetchImpl}));
    assert.equal(cloud.calls.length,0);
 }
});

test('create once with narrowly scoped path, exact users and short-lived OAuth; verify before success',async()=>{
 const cloud=fakeCloud();const result=await provisionTrial({env,inspect:readOnly,fetchImpl:cloud.fetchImpl});
 assert.deepEqual(result,{outcome:'created-verified',applicationId:appId,path:target,allowIdentityCount:2,oauthEnabled:true});
 assert.deepEqual(cloud.calls.map(c=>c.method+' '+c.path),['POST /access/apps','GET /access/apps/'+appId,'GET /access/apps/'+appId+'/policies']);
 const body=cloud.calls[0].body as any;
 assert.equal(body.type,'self_hosted');assert.equal(body.domain,target);
 assert.deepEqual(body.destinations,[{type:'public',uri:target}]);
 assert.equal(body.oauth_configuration.enabled,true);
 assert.equal(body.oauth_configuration.grant.access_token_lifetime,'15m');
 assert.equal(body.oauth_configuration.dynamic_client_registration.allow_any_on_loopback,true);
 assert.equal(body.oauth_configuration.dynamic_client_registration.allowed_uris.length,0);
 assert.deepEqual(body.policies,[{name:'Weave trial exact identities',decision:'allow',include:[{email:{email:emailA}},{email:{email:emailB}}]}]);
 assert.equal(body.app_launcher_visible,false);
 const output=JSON.stringify(result);
 for(const secret of [env.CLOUDFLARE_API_TOKEN,env.CLOUDFLARE_ACCESS_TRIAL_TOKEN,emailA,emailB])assert.equal(output.includes(secret),false);
 assert.ok(cloud.calls.every(c=>c.authorization==='Bearer '+env.CLOUDFLARE_ACCESS_TRIAL_TOKEN && c.redirect==='manual'));
});

test('invalid create result or ambiguous response refuses to claim success; no retry',async()=>{
 const cloud=fakeCloud();let n=0;
 const fetchImpl:typeof fetch=async(u,i)=>{n++;if(n===1)return envelope({name:'wrong',id:appId});return cloud.fetchImpl(u,i)};
 await assert.rejects(provisionTrial({env,inspect:readOnly,fetchImpl}));
 assert.equal(n,1);
});

test('HTTP errors, redirects and secret-containing body failures never cause retry, leak or success',async()=>{
 for(const status of [302,401,403,429,500]){
    let n=0;
    const fetchImpl:typeof fetch=async()=>{n++;return new Response(env.CLOUDFLARE_ACCESS_TRIAL_TOKEN,{status})};
    let err:unknown;try{await provisionTrial({env,inspect:readOnly,fetchImpl})}catch(e){err=e}
    assert.ok(err instanceof Error);assert.ok(!err.message.includes(env.CLOUDFLARE_ACCESS_TRIAL_TOKEN));
    assert.equal(n,1);
 }
});

test('POST success with missing policy verification is not represented as a usable Access app',async()=>{
 const cloud=fakeCloud();const real=cloud.fetchImpl;
 const injected:typeof fetch=async(u,i)=>String(u).endsWith('/policies')?envelope([{name:'Everyone',decision:'bypass',include:[{everyone:{}}]}]):real(u,i);
 await assert.rejects(provisionTrial({env,inspect:readOnly,fetchImpl:injected}),/verification/);
 assert.equal(cloud.calls.filter(x=>x.method==='POST').length,1);
});

test('POST success with wider domain or OAuth disabled cannot be reported verified',async()=>{
 const base=fakeCloud();
 const bad:typeof fetch=async(u,i)=>String(u).endsWith('/'+appId)?envelope({id:appId,name:'Weave Managed OAuth Trial',type:'self_hosted',domain:'demo.weave-389.workers.dev',destinations:[{type:'public',uri:'demo.weave-389.workers.dev'}],oauth_configuration:{enabled:false}}):base.fetchImpl(u,i);
 await assert.rejects(provisionTrial({env,inspect:readOnly,fetchImpl:bad}),/verification/);
});
