import assert from 'node:assert/strict';
import test from 'node:test';
import {inspectLiveOAuth} from '../scripts/inspect-live-oauth.ts';

const account = 'a'.repeat(32), token = 'SECRET-NEVER-PRINTED';
const versionId = 'c6624e9d-2dfb-4801-9ed8-ad45ece12b88';
const deployId = 'c6624e9d-2dfb-4801-9ed8-ad45ece12b99';
const api = 'https://api.cloudflare.com/client/v4/accounts/' + account;
const demo = 'https://demo.weave-389.workers.dev';

function fixture({bindings = [
    {name: 'db', type: 'd1', id: '11111111-2222-3333-4444-555555555555'},
    {name: 'AUTH_SERVER', type: 'service', service: 'weave-oauth'},
    {name: 'OAUTH_ISSUER', type: 'plain_text', text: 'https://issuer.example/private'},
    {name: 'OAUTH_RESOURCE', type: 'plain_text', text: demo + '/mcp'},
    {name: 'OAUTH_ACCOUNT_PROOF_KEY', type: 'secret_text', text: token}
], statuses = [200,401,401,405], override}: {
    bindings?: any[];statuses?: number[];
    override?: (url: string, index: number) => Response | undefined
} = {}) {
    const calls: {url: string;opts: RequestInit}[] = [];
    const fetchImpl = async (url: string, opts: RequestInit) => {
        const idx = calls.length;
        calls.push({url, opts});
        const overridden = override?.(url,idx);
        if (overridden) return overridden;
        const result = idx === 0 ? {subdomain: 'weave-389'} : idx === 1 ? {bindings}
            : idx === 2 ? {deployments: [{id: deployId, created_on: '2026-10-26T00:00:00Z',
                versions: [{version_id: versionId, percentage: 100}]}]} : undefined;
        return idx < 3 ? Response.json({success: true, result})
            : new Response('NOT-TO-PRINT ' + token, {status: statuses[idx - 3]});
    };
    return {calls, input: {env: {CLOUDFLARE_ACCOUNT_ID: account, CLOUDFLARE_API_TOKEN: token}, fetchImpl: fetchImpl as typeof fetch}};
}
test('inspects active deployed versions and OAuth 401 without forwarding or printing credentials', async () => {
    const f = fixture(), result = await inspectLiveOAuth(f.input);
    assert.deepEqual(f.calls.map(c => c.url), [
        api + '/workers/subdomain', api + '/workers/scripts/demo/settings',
        api + '/workers/scripts/demo/deployments?per_page=1', demo + '/',
        demo + '/.well-known/oauth-protected-resource/mcp',
        demo + '/.well-known/oauth-protected-resource', demo + '/mcp'
    ]);
    assert.equal(result.deployment.versions[0].versionId, versionId);
    assert.equal(result.configuration.mode, 'service-binding');
    assert.equal(result.configuration.proofKeyPresent, true);
    assert.equal(result.metadataBlockedWithoutBearerChallenge, true);
    assert.deepEqual(Object.values(result.anonymous).map(x => x.status), [200,401,401,405]);
    assert.equal(result.readOnly, true);
    assert.ok(!JSON.stringify(result).includes(token));
    assert.ok(!JSON.stringify(result).includes(account));
    for (const {opts} of f.calls.slice(0, 3)) {
        assert.equal(opts.method, 'GET');assert.equal(opts.redirect,'error');
        assert.equal((opts.headers as Record<string,string>).Authorization, 'Bearer ' + token);
    }
    for (const {opts} of f.calls.slice(3)) {
        assert.equal(opts.method, 'GET');assert.equal(opts.redirect, 'manual');
        assert.equal((opts.headers as Record<string,string>).Authorization, undefined);
        assert.equal(opts.cache, 'no-store');
    }
});
test('unprovisioned OAuth and publicly missing metadata are reported, not mistaken for live private access',async()=>{
    const f = fixture({bindings:[{name:'db',type:'d1'}],statuses:[200,404,404,405]});
    const report = await inspectLiveOAuth(f.input);
    assert.equal(report.configuration.mode,'not-configured');
    assert.equal(report.metadataBlockedWithoutBearerChallenge,false);
});
test('binding ambiguity is reported without leaking any returned secret text',async()=>{
    const f = fixture({bindings:[{name:'AUTH_SERVER',type:'service',service:'as'},
        {name:'AUTH_SERVER',type:'service',service:'duplicate'},{name:'OAUTH_INTROSPECTION_URL',type:'plain_text',text:token}]});
    const result=await inspectLiveOAuth(f.input);
    assert.equal(result.configuration.mode,'invalid');
    assert.ok(result.configuration.problems.some(x=>x.includes('Duplicate AUTH_SERVER')));
    assert.ok(result.configuration.problems.some(x=>x.includes('Mixed Service')));
    assert.ok(!JSON.stringify(result).includes(token));
});
test('wrong Cloudflare account subdomain stops before Worker settings or public calls',async()=>{
    const f=fixture({override:(url,i)=>i===0?Response.json({success:true,result:{subdomain:'unrelated'}}):undefined});
    await assert.rejects(inspectLiveOAuth(f.input),/authorized demo subdomain/);
    assert.equal(f.calls.length,1);
});
test('Cloudflare API redirects and refusals do not follow the target or reflect error bodies',async()=>{
    for(const response of [new Response(null,{status:302,headers:{location:'https://evil.example/'}}),
        new Response(token,{status:403})]){
        const f=fixture({override:(_url,i)=>i===2?response:undefined});
        await assert.rejects(inspectLiveOAuth(f.input),/Active Worker deployment returned HTTP/);
        assert.equal(f.calls.length,3);
        assert.equal(f.calls[2].opts.redirect,'error');
    }
});
test('invalid deployed version metadata fails closed before any public probe',async()=>{
    const f=fixture({override:(_url,i)=>i===2?Response.json({success:true,result:{deployments:[{id:token,versions:[]}]}}):undefined});
    await assert.rejects(inspectLiveOAuth(f.input),/Active Worker deployment metadata is invalid/);
    assert.equal(f.calls.length,3);
});
test('public redirects remain observable without following attacker locations',async()=>{
    const f=fixture({override:(url,i)=>i===4?new Response(null,{status:302,headers:{location:'https://evil.example/'}}):undefined});
    const result=await inspectLiveOAuth(f.input);
    assert.equal(result.anonymous['/.well-known/oauth-protected-resource/mcp'].redirect,true);
    assert.equal(f.calls.length,7);
});
test('missing credentials stop before all network calls',async()=>{
    const f=fixture();
    await assert.rejects(inspectLiveOAuth({env:{},fetchImpl:f.input.fetchImpl}),/requires the existing Cloudflare/);
    assert.equal(f.calls.length,0);
});

test('service binding cannot hide any external-only OAuth configuration, even without URL',async()=>{
    for(const name of ['OAUTH_CLIENT_ID','OAUTH_CLIENT_SECRET','OAUTH_INTROSPECTION_TRUSTED_ORIGIN']){
        const f=fixture({bindings:[{name:'AUTH_SERVER',type:'service',service:'approved-as'},
            {name,type:name==='OAUTH_CLIENT_SECRET'?'secret_text':'plain_text',text:token}]});
        const result=await inspectLiveOAuth(f.input);
        assert.equal(result.configuration.mode,'invalid',name);
        assert.ok(result.configuration.problems.some(x=>x.includes('Mixed Service')),name);
        assert.ok(!JSON.stringify(result).includes(token));
    }
});
test('an incomplete or partial traffic rollout cannot be presented as the active deployment',async()=>{
    const f=fixture({override:(_url,i)=>i===2?Response.json({success:true,result:{
        deployments:[{id:deployId,versions:[{version_id:versionId,percentage:50}]}]}}):undefined});
    await assert.rejects(inspectLiveOAuth(f.input),/traffic allocation is incomplete/);
    assert.equal(f.calls.length,3,'must refuse inconsistent metadata before public calls');
});
test('complete two-version weighted deployments retain each percentage without mutation',async()=>{
    const other='ac7f7ad9-97d7-4c0a-9f2b-276991341333';
    const f=fixture({override:(_url,i)=>i===2?Response.json({success:true,result:{
        deployments:[{id:deployId,versions:[{version_id:versionId,percentage:25},{version_id:other,percentage:75}]}]}}):undefined});
    const result=await inspectLiveOAuth(f.input);
    assert.deepEqual(result.deployment.versions,[{versionId:versionId,percentage:25},{versionId:other,percentage:75}]);
});
test('valid fallback metadata is not falsely classified as entirely inaccessible',async()=>{
    const f=fixture({statuses:[200,401,200,405]});
    const result=await inspectLiveOAuth(f.input);
    assert.equal(result.metadataBlockedWithoutBearerChallenge,false);
});

test('stream errors in Cloudflare metadata cannot echo secrets from a failed body read',async()=>{
    const secret = 'PRIVATE-STREAM-ERROR-DO-NOT-LEAK';
    for(const stage of [0,1,2]){
        const f=fixture({override:(_url,i)=>i===stage?new Response(new ReadableStream({
            pull(controller){controller.error(Error(secret));}
        }),{status:200}):undefined});
        await assert.rejects(inspectLiveOAuth(f.input),error=>{
            assert.match((error as Error).message,/metadata read failed; no retry or mutation attempted/);
            assert.ok(!(error as Error).message.includes(secret));
            return true;
        });
        assert.equal(f.calls.length,stage+1);
    }
});
test('malformed Cloudflare JSON cannot echo a raw secret or start further requests',async()=>{
    const f=fixture({override:(_url,i)=>i===1?new Response('NOT JSON: '+token):undefined});
    await assert.rejects(inspectLiveOAuth(f.input),error=>{
        assert.match((error as Error).message,/metadata read failed/);
        assert.ok(!(error as Error).message.includes(token));
        return true;
    });
    assert.equal(f.calls.length,2);
});

test('incomplete RFC 7662 settings are never reported as configured', async () => {
    const required = [
        {name: 'OAUTH_ISSUER', type: 'plain_text', text: 'https://issuer.example'},
        {name: 'OAUTH_RESOURCE', type: 'plain_text', text: demo + '/mcp'},
        {name: 'OAUTH_INTROSPECTION_URL', type: 'plain_text', text: 'https://issuer.example/introspect'},
        {name: 'OAUTH_CLIENT_ID', type: 'plain_text', text: 'client-id'},
        {name: 'OAUTH_CLIENT_SECRET', type: 'secret_text', text: token}
    ];
    for (const missing of required.map(binding => binding.name)) {
        const f = fixture({bindings: required.filter(binding => binding.name !== missing)});
        const report = await inspectLiveOAuth(f.input);
        assert.equal(report.configuration.mode, 'invalid', missing);
        assert.ok(report.configuration.problems.includes('Incomplete external introspection settings'), missing);
        assert.ok(!JSON.stringify(report).includes(token), missing);
    }
    for (const bindings of [
        required.filter(binding => ['OAUTH_ISSUER', 'OAUTH_RESOURCE'].includes(binding.name)),
        [{name: 'OAUTH_INTROSPECTION_TRUSTED_ORIGIN', type: 'plain_text', text: 'https://issuer.example'}]
    ]) {
        const f = fixture({bindings});
        const report = await inspectLiveOAuth(f.input);
        assert.equal(report.configuration.mode, 'invalid');
        assert.ok(report.configuration.problems.includes('Incomplete external introspection settings'));
    }
});
