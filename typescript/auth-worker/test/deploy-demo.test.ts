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

test('a protected issuer deployment refuses missing credentials and trial identities before Cloudflare GET',async()=>{
    for(const update of [
        {CLOUDFLARE_API_TOKEN:undefined},{GITHUB_CLIENT_SECRET:undefined},
        {HUMAN_SESSION_KEY:'wrong'},{WEAVE_TRIAL_GITHUB_SUBJECTS:'Everyone'},
        {WEAVE_APPROVED_ISSUER_DEPLOY:undefined},
        {WEAVE_AUTH_KV_NAMESPACE_ID:undefined}
    ]){
        let called=false;
        await assert.rejects(()=>preflightIssuerDeployment({...env,...update},async()=>{
            called=true;throw Error('unexpected network');
        }),/required|selected|approved|identifier|namespace|session/i);
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

test('post-deploy verification requires exact issuer, consent DO, mailbox owner RPC and all three Worker secrets',()=>{
    assert.doesNotThrow(()=>validateIssuerReadback(binding,env));
    for(const name of binding.map(x=>x.name)){
        assert.throws(()=>validateIssuerReadback(binding.filter(x=>x.name!==name),env),/mismatch/);
    }
    assert.throws(()=>validateIssuerReadback(binding.map(x=>x.name==='MAILBOX'?{...x,service:'unrelated'}:x),env),/mismatch/);
});


test('already-installed approved AUTH_SERVER may coexist with issuer preflight without a demo write',async()=>{
    const database={name:'db',type:'d1',database_id:'01234567-89ab-cdef-0123-456789abcdef'};
    const approved=[
        database,
        {name:'AUTH_SERVER',type:'service',service:'weave-auth-demo',environment:'production'},
        {name:'OAUTH_ISSUER',type:'plain_text',text:env.OAUTH_ISSUER},
        {name:'OAUTH_RESOURCE',type:'plain_text',text:env.OAUTH_RESOURCE}
    ];
    const scenarios: {name:string;bindings:typeof approved;accepted:boolean}[]=[
        {name:'exact already-bound issuer',bindings:approved,accepted:true},
        {name:'foreign service',bindings:approved.map(b=>b.name==='AUTH_SERVER'?{...b,service:'foreign-auth'}:b),accepted:false},
        {name:'unapproved named environment',bindings:approved.map(b=>b.name==='AUTH_SERVER'?{...b,environment:'staging'}:b),accepted:false},
        {name:'unexpected RPC entrypoint',bindings:approved.map(b=>b.name==='AUTH_SERVER'?{...b,entrypoint:'Admin'}:b),accepted:false},
        {name:'wrong issuer',bindings:approved.map(b=>b.name==='OAUTH_ISSUER'?{...b,text:'https://foreign.example'}:b),accepted:false},
        {name:'wrong resource',bindings:approved.map(b=>b.name==='OAUTH_RESOURCE'?{...b,text:'https://foreign.example/mcp'}:b),accepted:false},
        {name:'missing issuer',bindings:approved.filter(b=>b.name!=='OAUTH_ISSUER'),accepted:false},
        {name:'duplicate binding',bindings:[...approved,approved[1]],accepted:false},
        {name:'mixed legacy introspection',bindings:[...approved,{name:'OAUTH_INTROSPECTION_URL',type:'plain_text',text:'https://foreign.example/introspect'}],accepted:false}
    ];
    for(const scenario of scenarios){
        const targets:string[]=[];
        const request=async(input:RequestInfo|URL,init?:RequestInit)=>{
            const url=String(input);
            targets.push(url);
            assert.equal(init?.method,'GET',scenario.name);
            assert.equal(init?.redirect,'error',scenario.name);
            assert.equal((init?.headers as Record<string,string>).Authorization,'Bearer '+env.CLOUDFLARE_API_TOKEN);
            if(url.endsWith('/workers/subdomain'))
                return Response.json({success:true,result:{subdomain:'weave-389'}});
            if(url.endsWith('/workers/scripts/demo/settings'))
                return Response.json({success:true,result:{bindings:scenario.bindings}});
            if(url.endsWith('/storage/kv/namespaces/'+env.WEAVE_AUTH_KV_NAMESPACE_ID))
                return Response.json({success:true,result:{id:env.WEAVE_AUTH_KV_NAMESPACE_ID}});
            if(url.endsWith('/workers/scripts/weave-auth-demo/settings'))
                return new Response('{}',{status:404});
            throw Error('unexpected GET '+url);
        };
        if(scenario.accepted){
            const manifest=await preflightIssuerDeployment(env,request as typeof fetch);
            assert.equal(manifest.name,'weave-auth-demo');
            assert.equal(targets.length,4);
        }else{
            await assert.rejects(()=>preflightIssuerDeployment(env,request as typeof fetch),undefined,scenario.name);
        }
        assert.ok(targets.length>=2,scenario.name);
    }
});
