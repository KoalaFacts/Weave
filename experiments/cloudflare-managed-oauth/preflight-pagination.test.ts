import test from 'node:test';
import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import {inspectAccess} from './preflight.ts';

const account='a'.repeat(32),secret='synthetic-preflight-secret';
const env={CLOUDFLARE_ACCOUNT_ID:account,CLOUDFLARE_API_TOKEN:secret};
const app={id:'11111111-2222-3333-4444-555555555555',
    name:'Existing protected app',domain:'demo.weave-389.workers.dev/private'};
function fixture(pages:(page:number)=>{result:unknown[];result_info:unknown}) {
    const requests:string[]=[];
    const fetchImpl:typeof fetch=async(input,init={})=>{
        const url=new URL(String(input));
        assert.equal(url.origin,'https://api.cloudflare.com');
        assert.equal(init.method,'GET');assert.equal(init.redirect,'manual');
        assert.equal(init.body,undefined);
        assert.equal(new Headers(init.headers).get('Authorization'),'Bearer '+secret);
        const path=url.pathname.slice(('/client/v4/accounts/'+account).length);
        requests.push(path+url.search);
        if(path==='/workers/subdomain')return Response.json({success:true,result:{subdomain:'weave-389'}});
        if(path==='/access/organizations')return Response.json({success:true,result:{auth_domain:'weave-test.cloudflareaccess.com'}});
        if(path==='/access/apps')return Response.json({success:true,...pages(Number(url.searchParams.get('page')))});
        throw Error('Unexpected request');
    };
    return {requests,fetchImpl};
}

for(const pagination of [
    {page:1,per_page:100,count:0,total_count:0,total_pages:0},
    {page:1,per_page:100,count:0,total_count:0},
    {page:1,per_page:100},
])test('empty documented Access page works without a positive total_pages: '+JSON.stringify(pagination),async()=>{
    const f=fixture(()=>({result:[],result_info:pagination}));
    const report=await inspectAccess({env,fetchImpl:f.fetchImpl});
    assert.equal(report.outcome,'readable');assert.equal(report.apps.status,200);
    assert.equal(report.apps.complete,true);assert.deepEqual(report.apps.trial,[]);
    assert.equal(report.organization.configured,true);assert.equal(report.writePermissionVerified,false);
    assert.equal(f.requests.length,3);
});

test('without total_pages scan to the empty page, keeping a later overlapping app',async()=>{
    const f=fixture(page=>({result:page===1?[{name:'Unrelated',domain:'elsewhere.example.org'}]:page===2?[app]:[],
        result_info:{page,per_page:100}}));
    const report=await inspectAccess({env,fetchImpl:f.fetchImpl});
    assert.equal(report.apps.complete,true);assert.equal(f.requests.length,5);
    assert.deepEqual(report.apps.trial,[{id:app.id,managedOAuth:false,matchesTrialName:false}]);
});

test('without total_pages never call ten nonempty pages a complete inventory',async()=>{
    const f=fixture(page=>({result:[{name:'Unrelated',domain:'elsewhere.example.org'}],result_info:{page}}));
    const report=await inspectAccess({env,fetchImpl:f.fetchImpl});
    assert.equal(report.outcome,'blocked');assert.equal(report.apps.complete,false);
    assert.equal(f.requests.filter(path=>path.startsWith('/access/apps')).length,10);
});

for(const pagination of [
    {page:2,total_pages:1}, {page:1,total_pages:-1}, {page:1,total_pages:11},
    {page:1,total_pages:2}, {page:1,total_pages:0,total_count:1},
    {page:1,count:1,total_count:0}, {page:1,total_count:3},
    {page:1,total_count:'0'},
])test('contradictory empty inventory fails closed: '+JSON.stringify(pagination),async()=>{
    const f=fixture(()=>({result:[],result_info:pagination}));
    await assert.rejects(inspectAccess({env,fetchImpl:f.fetchImpl}),/pagination/);
    assert.equal(f.requests.length,2);
});

test('changing total_count between pages fails closed',async()=>{
    const f=fixture(page=>({result:page===1?[app]:[],result_info:{page,total_count:page===1?1:0}}));
    await assert.rejects(inspectAccess({env,fetchImpl:f.fetchImpl}),/pagination/);
});

test('CLI identifies a fixed safe failure stage instead of hiding all causes or printing secrets',()=>{
    const source=new URL('./preflight.ts',import.meta.url);
    const script=`globalThis.fetch=async()=>Response.json({success:true,result:{subdomain:'wrong-account'}});`+
        `process.argv=[process.execPath,${JSON.stringify(fileURLToPath(source))}];await import(${JSON.stringify(source.href)});`;
    const result=spawnSync(process.execPath,['--experimental-strip-types','--input-type=module','--eval',script],{
        env:{...process.env,...env},encoding:'utf8',timeout:10000
    });
    assert.equal(result.status,1);
    assert.match(result.stderr,/"failure":"account-identity"/);
    assert.equal(result.stdout,'');
    for(const value of [account,secret,'wrong-account'])assert.equal(result.stderr.includes(value),false);
});

for(const invalid of [undefined,null,[],false,'unexpected'])test('missing or malformed result_info never proves a complete inventory: '+String(invalid),async()=>{
    const f=fixture(()=>({result:[],result_info:invalid}));
    await assert.rejects(inspectAccess({env,fetchImpl:f.fetchImpl}),/pagination/);
});
