import test from 'node:test';
import assert from 'node:assert/strict';
import { inspectAccess } from './preflight.ts';

const env = { CLOUDFLARE_ACCOUNT_ID: 'a'.repeat(32), CLOUDFLARE_API_TOKEN: 'synthetic-worker-secret' };
const envelope = (result: unknown, result_info?: unknown) => Response.json({success:true,result,result_info});
const pages = {page:1,total_pages:1};
function fixture(overrides: Record<string, () => Response> = {}) {
    const calls: {path:string;init:RequestInit}[] = [];
    const fetchImpl: typeof fetch = async (input, init = {}) => {
        const url = new URL(String(input));
        assert.equal(url.origin, 'https://api.cloudflare.com');
        const path = url.pathname.slice(('/client/v4/accounts/' + env.CLOUDFLARE_ACCOUNT_ID).length) + url.search;
        calls.push({path,init});
        assert.equal(init.method, 'GET');
        assert.equal(init.redirect, 'manual');
        assert.equal(init.body, undefined);
        const token = env.CLOUDFLARE_API_TOKEN;
        assert.equal(new Headers(init.headers).get('Authorization'), 'Bearer ' + token);
        if(overrides[path]) return overrides[path]();
        if(path === '/workers/subdomain') return envelope({subdomain:'weave-389'});
        if(path === '/access/apps?per_page=100&page=1') return envelope([], pages);
        if(path === '/access/organizations') return envelope({auth_domain:'example.cloudflareaccess.com'});
        throw Error('Unexpected request');
    };
    return {calls,fetchImpl};
}

test('reads Access permission and organization without writes or disclosing credentials', async () => {
    const f = fixture();
    const report = await inspectAccess({env,fetchImpl:f.fetchImpl});
    assert.equal(report.outcome, 'readable');
    assert.equal(report.apps.status, 200);
    assert.equal(report.apps.complete, true);
    assert.equal(report.organization.configured, true);
    assert.equal(report.organization.authDomain, 'example.cloudflareaccess.com');
    assert.equal(report.writePermissionVerified, false);
    assert.equal(report.readOnly, true);
    assert.equal(f.calls.length,3);
    for(const secret of Object.values(env)) assert.ok(!JSON.stringify(report).includes(secret));
});

test('missing credentials and wrong account stop before Access access', async () => {
    let calls = 0;
    await assert.rejects(inspectAccess({env:{},fetchImpl:async () => { calls++;throw Error('must not fetch'); }}));
    assert.equal(calls,0);
    const f = fixture({'/workers/subdomain':() => envelope({subdomain:'not-weave'})});
    await assert.rejects(inspectAccess({env,fetchImpl:f.fetchImpl}), /authorized Weave account/);
    assert.equal(f.calls.length,1);
});

for (const status of [401,403,429,500]) test(`Access HTTP ${status} is reported without response bodies, retries or writes`, async () => {
    const f = fixture({'/access/apps?per_page=100&page=1':() => new Response(env.CLOUDFLARE_API_TOKEN,{status})});
    const report = await inspectAccess({env,fetchImpl:f.fetchImpl});
    assert.equal(report.outcome,'blocked');
    assert.equal(report.apps.status,status);
    assert.equal(report.apps.complete,false);
    assert.equal(report.organization.configured,true);
    assert.equal(f.calls.filter(x => x.path.startsWith('/access/apps')).length,1);
    assert.ok(!JSON.stringify(report).includes(env.CLOUDFLARE_API_TOKEN));
});

test('follows bounded API pages and projects only the trial and matching-host application IDs', async () => {
    const f = fixture({
        '/access/apps?per_page=100&page=1':() => envelope([{id:'11111111-1111-1111-1111-111111111111',name:'private unrelated app',domain:'private.example.com',secret:'never-output'}],{page:1,total_pages:2}),
        '/access/apps?per_page=100&page=2':() => envelope([{id:'22222222-2222-2222-2222-222222222222',name:'Weave Managed OAuth Trial',domain:'demo.weave-389.workers.dev/managed-oauth-trial',oauth_configuration:{enabled:true}}],{page:2,total_pages:2})
    });
    const report = await inspectAccess({env,fetchImpl:f.fetchImpl});
    assert.equal(report.apps.complete,true);
    assert.deepEqual(report.apps, {status:200,complete:true,trial:[{
        id:'22222222-2222-2222-2222-222222222222',managedOAuth:true,matchesTrialName:true
    }]});
    assert.equal(f.calls.length,4);
});

test('redirects are not followed and malformed application results fail closed', async () => {
    const f = fixture({'/access/apps?per_page=100&page=1':() => new Response(null,{status:302,headers:{Location:'https://evil.invalid/'}})});
    assert.equal((await inspectAccess({env,fetchImpl:f.fetchImpl})).apps.status,302);
    const malformed = fixture({'/access/apps?per_page=100&page=1':() => envelope({applications:[]},pages)});
    await assert.rejects(inspectAccess({env,fetchImpl:malformed.fetchImpl}),/Access application metadata/);
});

test('body stream errors are sanitized and pages above the cap never look complete', async () => {
    const f = fixture({'/access/apps?per_page=100&page=1':() => new Response(new ReadableStream({start(controller){controller.error(Error(env.CLOUDFLARE_API_TOKEN));}}))});
    await assert.rejects(inspectAccess({env,fetchImpl:f.fetchImpl}),error => error instanceof Error && !error.message.includes(env.CLOUDFLARE_API_TOKEN));
    const many = fixture({'/access/apps?per_page=100&page=1':() => envelope([],{page:1,total_pages:11})});
    await assert.rejects(inspectAccess({env,fetchImpl:many.fetchImpl}),/pagination/);
});

test('unexpected organization names and reflected credentials cannot be logged', async () => {
    const f = fixture({'/access/organizations':() => envelope({auth_domain:'https://evil.invalid/'})});
    await assert.rejects(inspectAccess({env,fetchImpl:f.fetchImpl}),/organization metadata/);
});

for (const additional of [
    {self_hosted_domains:['private.example.com','demo.weave-389.workers.dev/managed-oauth-trial']},
    {destinations:[{type:'public',uri:'private.example.com'},{type:'public',uri:'*.weave-389.workers.dev/managed-oauth-trial'}]}
]) test('finds Weave coverage on additional public domains without exposing unrelated hosts', async () => {
    const f=fixture({'/access/apps?per_page=100&page=1':()=>envelope([{
        id:'33333333-3333-3333-3333-333333333333',name:'existing multi-host app',domain:'private.example.com',...additional
    }],pages)});
    const report=await inspectAccess({env,fetchImpl:f.fetchImpl});
    assert.deepEqual(report.apps.trial,[{id:'33333333-3333-3333-3333-333333333333',managedOAuth:false,matchesTrialName:false}]);
});

test('current destinations override legacy aliases and unrelated suffixes do not match', async () => {
    const f=fixture({'/access/apps?per_page=100&page=1':()=>envelope([{
        id:'44444444-4444-4444-4444-444444444444',name:'other',domain:'private.example.com',
        self_hosted_domains:['demo.weave-389.workers.dev'],
        destinations:[{type:'public',uri:'demo.weave-389.workers.dev.attacker.invalid'}]
    }],pages)});
    assert.deepEqual((await inspectAccess({env,fetchImpl:f.fetchImpl})).apps.trial,[]);
});


for(const [description, destinations] of [
    ['account-wide Worker rule', [{type:'all_workers'}]],
    ['Worker rule with unresolved target', [{type:'worker',worker_id:'different-worker-id'}]],
    ['all preview Workers', [{type:'all_preview_workers'}]],
    ['wildcard parent domain', [{type:'public',uri:'*.workers.dev/special/*'}]],
] as const) test('existing '+description+' is a potential Access precedence conflict; refuse implicit policy replacement',async()=>{
    const f=fixture({'/access/apps?per_page=100&page=1':()=>envelope([{
        id:'55555555-5555-5555-5555-555555555555',
        name:'Existing independent Access policy',
        domain:'private.example.org',destinations
    }],pages)});
    const report=await inspectAccess({env,fetchImpl:f.fetchImpl});
    assert.deepEqual(report.apps.trial,[{
        id:'55555555-5555-5555-5555-555555555555',managedOAuth:false,matchesTrialName:false
    }]);
});

for(const uri of [
    'foo*.weave-389.workers.dev/protected/*',
    'dem*.weave-389.workers.dev.attacker.invalid',
    '*mo.different.workers.dev',
]) test('disjoint Access partial wildcard '+uri+' does not block an unrelated path',async()=>{
    const f=fixture({'/access/apps?per_page=100&page=1':()=>envelope([{
        id:'66666666-6666-6666-6666-666666666666',
        name:'Unrelated Access policy',domain:'unrelated.example.org',
        destinations:[{type:'public',uri}]
    }],pages)});
    assert.deepEqual((await inspectAccess({env,fetchImpl:f.fetchImpl})).apps.trial,[]);
});


test('classifies an exact Cloudflare Access-not-enabled marker with 9999 without exposing provider text', async () => {
    const errorBody=JSON.stringify({success:false,errors:[{
        code:9999,message:'access.api.error.not_enabled: Access is not enabled. Private diagnostic ' + env.CLOUDFLARE_API_TOKEN
    }],account_id:env.CLOUDFLARE_ACCOUNT_ID});
    const f=fixture({
        '/access/apps?per_page=100&page=1':()=>new Response(errorBody,{status:403}),
        '/access/organizations':()=>new Response(errorBody,{status:403})
    });
    const report=await inspectAccess({env,fetchImpl:f.fetchImpl});
    assert.equal(report.outcome,'blocked');
    assert.equal(report.apps.status,403);
    assert.equal(report.organization.status,403);
    assert.deepEqual(report.apps.errorCodes,[9999]);
    assert.deepEqual(report.organization.errorCodes,[9999]);
    assert.equal(report.apps.accessNotEnabled,true);
    assert.equal(report.organization.accessNotEnabled,true);
    assert.equal(f.calls.length,3);
    const output=JSON.stringify(report);
    assert.ok(!output.includes(env.CLOUDFLARE_API_TOKEN));
    assert.ok(!output.includes(env.CLOUDFLARE_ACCOUNT_ID));
    assert.ok(!output.includes('access.api.error.not_enabled'));
});

test('9999 alone, unrelated error messages and forged markers outside errors are not proof of disabled Access', async () => {
    for(const payload of [
        {errors:[{code:9999,message:'Account has restricted access.'}]},
        {errors:[{code:10000,message:'access.api.error.not_enabled'}]},
        {errors:[{code:9999,message:'unrelated error'}],metadata:'access.api.error.not_enabled'},
        {errors:[{code:9999,message:{value:'access.api.error.not_enabled'}}]}
    ]) {
        const f=fixture({'/access/apps?per_page=100&page=1':()=>new Response(JSON.stringify(payload),{status:403})});
        const report=await inspectAccess({env,fetchImpl:f.fetchImpl});
        assert.equal(report.outcome,'blocked');
        assert.notEqual(report.apps.accessNotEnabled,true);
        assert.equal(f.calls.filter(c=>c.path.startsWith('/access/apps')).length,1);
    }
});

test('an HTTP redirect or oversized 403 response cannot be mistaken for an Access-not-enabled confirmation', async () => {
    const body=JSON.stringify({errors:[{code:9999,message:'access.api.error.not_enabled'}]});
    for(const response of [
        ()=>new Response(body,{status:302,headers:{Location:'https://evil.invalid/'}}),
        ()=>new Response(body.repeat(1000),{status:403})
    ]) {
        const f=fixture({'/access/apps?per_page=100&page=1':response});
        const report=await inspectAccess({env,fetchImpl:f.fetchImpl});
        assert.notEqual(report.apps.accessNotEnabled,true);
        assert.deepEqual(report.apps.errorCodes,[]);
    }
});
