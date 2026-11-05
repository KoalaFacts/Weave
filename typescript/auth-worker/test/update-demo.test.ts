import assert from 'node:assert/strict';
import {test} from 'node:test';
import {preflightUpdate} from '../scripts/update-demo.ts';

const env:NodeJS.ProcessEnv={CLOUDFLARE_ACCOUNT_ID:'a'.repeat(32),CLOUDFLARE_API_TOKEN:'a-token-never-to-log'};
const kvId='c'.repeat(32),clientId='Iv23liRunningClientId';
const running=[
    {name:'OAUTH_ISSUER',type:'plain_text',text:'https://weave-auth-demo.weave-389.workers.dev'},
    {name:'OAUTH_RESOURCE',type:'plain_text',text:'https://demo.weave-389.workers.dev/mcp'},
    {name:'GITHUB_CLIENT_ID',type:'plain_text',text:clientId},
    {name:'OAUTH_KV',type:'kv_namespace',namespace_id:kvId},
    {name:'CONSENT_NONCES',type:'durable_object_namespace',class_name:'ConsentNonce'},
    {name:'MAILBOX',type:'service',service:'demo',entrypoint:'HumanDirectory'},
    {name:'GITHUB_CLIENT_SECRET',type:'secret_text'},
    {name:'HUMAN_SESSION_KEY',type:'secret_text'},
    {name:'WEAVE_TRIAL_GITHUB_SUBJECTS',type:'secret_text'}
];
/** A Cloudflare API that answers only the running Worker's settings, by GET. */
const cloudflare=(answer:()=>Response,calls:string[]=[])=>async(input:RequestInfo|URL,init?:RequestInit)=>{
    calls.push((init?.method??'unknown')+' '+String(input));
    assert.equal(String(input),'https://api.cloudflare.com/client/v4/accounts/'+env.CLOUDFLARE_ACCOUNT_ID+'/workers/scripts/weave-auth-demo/settings');
    assert.equal(init?.redirect,'error');
    return answer();
};
const settings=(bindings:unknown[])=>()=>Response.json({success:true,result:{bindings}});

test('preflightUpdate_RunningWorker_RebuildsItsConfigurationFromItsOwnBindingsByGetOnly',async()=>{
    const calls:string[]=[];
    const {manifest}=await preflightUpdate(env,cloudflare(settings(running),calls));
    assert.deepEqual(calls.map(call=>call.split(' ')[0]),['GET']);
    assert.equal(manifest.name,'weave-auth-demo');
    assert.deepEqual((manifest.kv_namespaces as unknown[]),[{binding:'OAUTH_KV',id:kvId}]);
    assert.equal((manifest.vars as Record<string,string>).GITHUB_CLIENT_ID,clientId);
    assert.deepEqual(manifest.services,[{binding:'MAILBOX',service:'demo',entrypoint:'HumanDirectory'}]);
    assert.ok(!JSON.stringify(manifest).includes(env.CLOUDFLARE_API_TOKEN!),'the token never reaches the configuration');
});

test('preflightUpdate_NotPublishedMissingBindingOrSecret_RefusesWithoutChange',async()=>{
    await assert.rejects(()=>preflightUpdate(env,cloudflare(()=>new Response('{}',{status:404}))),/not published yet/);
    await assert.rejects(()=>preflightUpdate(env,cloudflare(()=>new Response('{}',{status:403}))),/denied \(HTTP 403\)/);
    for(const name of ['WEAVE_TRIAL_GITHUB_SUBJECTS','HUMAN_SESSION_KEY','OAUTH_KV','CONSENT_NONCES','MAILBOX','GITHUB_CLIENT_ID'])
        await assert.rejects(()=>preflightUpdate(env,cloudflare(settings(running.filter(b=>b.name!==name)))),/lacks an expected binding or secret/,name);
    const otherService=running.map(b=>b.name==='MAILBOX'?{...b,service:'elsewhere'}:b);
    await assert.rejects(()=>preflightUpdate(env,cloudflare(settings(otherService))),/lacks an expected binding/);
    const otherIssuer=running.map(b=>b.name==='OAUTH_ISSUER'?{...b,text:'https://evil.example'}:b);
    await assert.rejects(()=>preflightUpdate(env,cloudflare(settings(otherIssuer))),/lacks an expected binding/);
});

test('preflightUpdate_NoAccountOrToken_RefusesBeforeAnyRequest',async()=>{
    for(const update of [{CLOUDFLARE_API_TOKEN:undefined},{CLOUDFLARE_ACCOUNT_ID:'not-an-id'}]){
        let called=false;
        await assert.rejects(()=>preflightUpdate({...env,...update},async()=>{called=true;throw Error('unexpected');}),/required/);
        assert.equal(called,false);
    }
});
