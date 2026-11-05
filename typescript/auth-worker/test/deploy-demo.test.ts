import assert from 'node:assert/strict';
import {test} from 'node:test';
import {preflightIssuerDeployment,validateIssuerDeployment,validateIssuerReadback} from '../scripts/deploy-demo.ts';

const env:NodeJS.ProcessEnv={
    CLOUDFLARE_ACCOUNT_ID:'a'.repeat(32),CLOUDFLARE_API_TOKEN:'a-token-never-to-log',
    WEAVE_AUTH_KV_NAMESPACE_ID:'b'.repeat(32),
    OAUTH_ISSUER:'https://weave-auth-demo.weave-389.workers.dev',
    OAUTH_RESOURCE:'https://demo.weave-389.workers.dev/mcp',
    GITHUB_CLIENT_ID:'Iv23liPublicClientId',
    GITHUB_CLIENT_SECRET:'secret-value-not-to-log',
    HUMAN_SESSION_KEY:Buffer.alloc(32,42).toString('base64url'),
    WEAVE_TRIAL_GITHUB_SUBJECTS:'8101,8102',
    WEAVE_APPROVED_ISSUER_DEPLOY:'true',
};
const binding=[
    {name:'OAUTH_ISSUER',type:'plain_text',text:env.OAUTH_ISSUER},
    {name:'OAUTH_RESOURCE',type:'plain_text',text:env.OAUTH_RESOURCE},
    {name:'GITHUB_CLIENT_ID',type:'plain_text',text:env.GITHUB_CLIENT_ID},
    {name:'OAUTH_KV',type:'kv_namespace',namespace_id:env.WEAVE_AUTH_KV_NAMESPACE_ID},
    {name:'CONSENT_NONCES',type:'durable_object_namespace',class_name:'ConsentNonce'},
    {name:'MAILBOX',type:'service',service:'demo',entrypoint:'HumanDirectory'},
    {name:'GITHUB_CLIENT_SECRET',type:'secret_text'},
    {name:'HUMAN_SESSION_KEY',type:'secret_text'},
    {name:'WEAVE_TRIAL_GITHUB_SUBJECTS',type:'secret_text'}
];

test('a protected issuer deployment refuses missing credentials or a malformed allowlist before Cloudflare GET',async()=>{
    for(const update of [
        {CLOUDFLARE_API_TOKEN:undefined},{GITHUB_CLIENT_SECRET:undefined},
        {HUMAN_SESSION_KEY:'wrong'},{WEAVE_TRIAL_GITHUB_SUBJECTS:'Everyone'},
        {WEAVE_APPROVED_ISSUER_DEPLOY:undefined},
        {WEAVE_AUTH_KV_NAMESPACE_ID:undefined}
    ]){
        let called=false;
        await assert.rejects(()=>preflightIssuerDeployment({...env,...update},async()=>{
            called=true;throw Error('unexpected network');
        }),/required|selected|approved|identifier|namespace|session|allowlist/i);
        assert.equal(called,false);
    }
    assert.equal(validateIssuerDeployment(env).name,'weave-auth-demo');
});

test('Cloudflare canonical database_id preflight reads only existing Weave account with GET',async()=>{
    const targets:string[]=[];
    const req=async(input:RequestInfo|URL,init?:RequestInit)=>{
        const url=String(input);
        targets.push((init?.method??'unknown')+' '+url);
        assert.equal(init?.method,'GET');
        assert.equal(init?.redirect,'error');
        assert.equal(init?.headers && (init.headers as Record<string,string>).Authorization,'Bearer '+env.CLOUDFLARE_API_TOKEN);
        const suffix=url.split('/accounts/'+env.CLOUDFLARE_ACCOUNT_ID)[1];
        let result:unknown;
        if(suffix==='/workers/subdomain')result={subdomain:'weave-389'};
        else if(suffix==='/workers/scripts/demo/settings')result={bindings:[
            {name:'db',type:'d1',database_id:'01234567-89ab-cdef-0123-456789abcdef'}]};
        else if(suffix==='/storage/kv/namespaces/'+env.WEAVE_AUTH_KV_NAMESPACE_ID)result={id:env.WEAVE_AUTH_KV_NAMESPACE_ID};
        else if(suffix==='/workers/scripts/weave-auth-demo/settings')
            return new Response('{}',{status:404});
        else throw Error('unexpected request '+url);
        return Response.json({success:true,result});
    };
    const manifest=await preflightIssuerDeployment(env,req as typeof fetch);
    assert.equal(manifest.workers_dev,true);
    assert.equal(targets.length,4);
    assert.deepEqual(targets.map(x=>x.split(' ')[0]),['GET','GET','GET','GET']);
});

test('existing script, incorrect account binding and KV permission denials abort with no writes',async()=>{
    for(const fault of ['wrong-subdomain','bad-demo','existing-auth','kv-403']){
        const req=async(input:RequestInfo|URL,init?:RequestInit)=>{
            assert.equal(init?.method,'GET');
            const url=String(input);
            if(url.endsWith('/workers/subdomain'))
                return Response.json({success:true,result:{subdomain:fault==='wrong-subdomain'?'other':'weave-389'}});
            if(url.endsWith('/workers/scripts/demo/settings'))
                return Response.json({success:true,result:{bindings:fault==='bad-demo'?[]:[
                    {name:'db',type:'d1',database_id:'01234567-89ab-cdef-0123-456789abcdef'}]}});
            if(url.includes('/storage/kv/namespaces/'))
                return fault==='kv-403'?new Response('{}',{status:403}):
                    Response.json({success:true,result:{id:env.WEAVE_AUTH_KV_NAMESPACE_ID}});
            if(url.endsWith('/workers/scripts/weave-auth-demo/settings'))
                return fault==='existing-auth'?Response.json({success:true,result:{bindings:binding}}):
                    new Response('{}',{status:404});
            throw Error('unknown lookup');
        };
        await assert.rejects(()=>preflightIssuerDeployment(env,req as typeof fetch));
    }
});

test('post-deploy verification requires exact issuer, consent DO, mailbox owner RPC and both authentication secrets',()=>{
    assert.doesNotThrow(()=>validateIssuerReadback(binding,env));
    assert.doesNotThrow(()=>validateIssuerReadback(binding.filter(x=>x.name!=='WEAVE_TRIAL_GITHUB_SUBJECTS'),env),'the sign-in allowlist is optional');
    for(const name of binding.map(x=>x.name).filter(name=>name!=='WEAVE_TRIAL_GITHUB_SUBJECTS')){
        assert.throws(()=>validateIssuerReadback(binding.filter(x=>x.name!==name),env),/mismatch/);
    }
    assert.throws(()=>validateIssuerReadback(binding.map(x=>x.name==='MAILBOX'?{...x,service:'unrelated'}:x),env),/mismatch/);
});
