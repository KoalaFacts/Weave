import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {provisionTrial} from './provision.ts';
import {inspectAccess} from './preflight.ts';

const account='a'.repeat(32);
const appId='11111111-2222-3333-4444-555555555555';
const target='demo.weave-389.workers.dev/access-trial/*';
const env={
    CLOUDFLARE_ACCOUNT_ID:account,CLOUDFLARE_API_TOKEN:'existing-synthetic-token',
    WEAVE_ACCESS_TRIAL_EMAILS:'agent-one@example.org,agent-two@example.org',
    WEAVE_ACCESS_TRIAL_COMMIT:'create-weave-access-trial',
    GITHUB_REPOSITORY:'KoalaFacts/Weave',GITHUB_REF:'refs/heads/main',
    GITHUB_EVENT_NAME:'workflow_dispatch'
};
const envelope=(result:unknown)=>Response.json({success:true,result});
function cloud(accessStatus=200){
    const calls:{path:string;method:string;authorization:string|null}[]=[];
    let created:Record<string,unknown>|undefined;
    const fetchImpl:typeof fetch=async(input,init={})=>{
        const url=new URL(String(input));
        assert.equal(url.origin,'https://api.cloudflare.com');
        assert.equal(init.redirect,'manual');
        const path=url.pathname.slice(('/client/v4/accounts/'+account).length)+url.search;
        const method=init.method??'GET';
        calls.push({path,method,authorization:new Headers(init.headers).get('Authorization')});
        if(path==='/workers/subdomain'&&method==='GET')return envelope({subdomain:'weave-389'});
        if(path==='/access/apps?per_page=100&page=1'&&method==='GET')
            return accessStatus===200?Response.json({success:true,result:[],result_info:{page:1,total_pages:1}}):new Response(null,{status:accessStatus});
        if(path==='/access/organizations'&&method==='GET')return envelope({auth_domain:'team.cloudflareaccess.com'});
        if(path==='/access/apps'&&method==='POST'){
            created=JSON.parse(String(init.body)) as Record<string,unknown>;
            return envelope({id:appId,name:created.name});
        }
        if(path==='/access/apps/'+appId&&method==='GET'&&created)return envelope({id:appId,...created});
        if(path==='/access/apps/'+appId+'/policies'&&method==='GET'&&created)return envelope(created.policies);
        throw Error('Unexpected Cloudflare request');
    };
    return {calls,fetchImpl};
}

test('existing token alone reaches real preflight, single create and policy readback',async()=>{
    const f=cloud();
    const result=await provisionTrial({env,fetchImpl:f.fetchImpl});
    assert.equal(result.outcome,'created-verified');
    assert.equal(result.path,target);
    assert.deepEqual(f.calls.map(call=>call.method+' '+call.path),[
        'GET /workers/subdomain','GET /access/apps?per_page=100&page=1','GET /access/organizations',
        'POST /access/apps','GET /access/apps/'+appId,'GET /access/apps/'+appId+'/policies'
    ]);
    assert.ok(f.calls.every(call=>call.authorization==='Bearer '+env.CLOUDFLARE_API_TOKEN));
});

test('a stale trial token cannot replace the selected existing credential on reads or writes',async()=>{
    const f=cloud();
    const result=await provisionTrial({env:{...env,CLOUDFLARE_ACCESS_TRIAL_TOKEN:'unused-synthetic-token'},fetchImpl:f.fetchImpl});
    assert.equal(result.outcome,'created-verified');
    assert.deepEqual(f.calls.map(call=>call.method+' '+call.path),[
        'GET /workers/subdomain','GET /access/apps?per_page=100&page=1','GET /access/organizations',
        'POST /access/apps','GET /access/apps/'+appId,'GET /access/apps/'+appId+'/policies'
    ]);
    assert.ok(f.calls.every(call=>call.authorization==='Bearer '+env.CLOUDFLARE_API_TOKEN));
});

test('missing existing token refuses preflight and creation even with a stale trial token',async()=>{
    for(const operation of [inspectAccess,provisionTrial]){
        const f=cloud();
        await assert.rejects(operation({env:{...env,CLOUDFLARE_API_TOKEN:undefined,CLOUDFLARE_ACCESS_TRIAL_TOKEN:'unused-synthetic-token'},fetchImpl:f.fetchImpl}));
        assert.equal(f.calls.length,0);
    }
});

test('existing token Access denial is tested and blocks every write without token fallback',async()=>{
    const f=cloud(403);
    await assert.rejects(provisionTrial({env,fetchImpl:f.fetchImpl}),/inventory is unreadable/);
    assert.equal(f.calls.length,3);
    assert.ok(f.calls.every(call=>call.method==='GET'&&call.authorization==='Bearer '+env.CLOUDFLARE_API_TOKEN));
});

test('live inspect and create use the same production secret context, never PR credentials',()=>{
    for(const [file,job] of [['inspect-weave-access.yml','inspect'],['provision-weave-access.yml','provision']]){
        const source=readFileSync(new URL('../../.github/workflows/'+file,import.meta.url),'utf8');
        const start=source.indexOf('\n  '+job+':\n');
        assert.ok(start>0);
        const live=source.slice(start);
        assert.match(live,/\n    environment: production\n/);
        assert.match(live,/github\.ref == 'refs\/heads\/main'/);
        assert.match(live,/CLOUDFLARE_API_TOKEN: \$\{\{ secrets\.CLOUDFLARE_API_TOKEN \}\}/);
        assert.doesNotMatch(source.slice(0,start),/secrets\./);
    }
});

test('Access workflows do not require or inject a second token secret',()=>{
    for(const file of ['inspect-weave-access.yml','provision-weave-access.yml']){
        const source=readFileSync(new URL('../../.github/workflows/'+file,import.meta.url),'utf8');
        assert.doesNotMatch(source,/CLOUDFLARE_ACCESS_TRIAL_TOKEN/);
    }
});
