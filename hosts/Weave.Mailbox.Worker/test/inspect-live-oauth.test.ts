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
