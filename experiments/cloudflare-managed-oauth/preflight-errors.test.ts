import test from 'node:test';
import assert from 'node:assert/strict';
import {inspectAccess} from './preflight.ts';

const account='a'.repeat(32);
const token='synthetic-credential-never-log';
const env={CLOUDFLARE_ACCOUNT_ID:account,CLOUDFLARE_API_TOKEN:token};
const envelope=(result:unknown)=>Response.json({success:true,result});

function replies(app:()=>Response, organization:()=>Response) {
    const calls:{path:string;method:string}[]=[];
    const fetchImpl:typeof fetch=async(input,init={})=>{
        const uri=new URL(String(input));
        assert.equal(uri.origin,'https://api.cloudflare.com');
        assert.equal(new Headers(init.headers).get('Authorization'),'Bearer '+token);
        assert.equal(init.method,'GET');
        assert.equal(init.redirect,'manual');
        assert.equal(init.body,undefined);
        const path=uri.pathname.slice(('/client/v4/accounts/'+account).length)+uri.search;
        calls.push({path,method:init.method});
        if(path==='/workers/subdomain')return envelope({subdomain:'weave-389'});
        if(path==='/access/apps?per_page=100&page=1')return app();
        if(path==='/access/organizations')return organization();
        throw Error('Unexpected request '+path);
    };
    return {calls,fetchImpl};
}

test('Cloudflare 403: report only bounded unique numeric error codes from Apps and Organization',async()=>{
    const f=replies(
        ()=>Response.json({success:false,errors:[
            {code:10000,message:token},{code:9109,message:account},{code:10000,message:'duplicate'}
        ]},{status:403}),
        ()=>Response.json({success:false,errors:[
            {code:10003,message:'not authorized',extra:{secret:token}}
        ]},{status:403})
    );
    const result=await inspectAccess({env,fetchImpl:f.fetchImpl});
    assert.equal(result.outcome,'blocked');
    assert.equal(result.apps.status,403);
    assert.equal(result.organization.status,403);
    assert.deepEqual((result.apps as {errorCodes?:number[]}).errorCodes,[10000,9109]);
    assert.deepEqual((result.organization as {errorCodes?:number[]}).errorCodes,[10003]);
    assert.deepEqual(f.calls.map(c=>c.path),[
        '/workers/subdomain','/access/apps?per_page=100&page=1','/access/organizations'
    ]);
    const output=JSON.stringify(result);
    for(const sensitive of [token,account,'not authorized','duplicate'])assert.equal(output.includes(sensitive),false);
    assert.equal(result.readOnly,true);
    assert.equal(result.writePermissionVerified,false);
});

test('only finite positive safe numeric Cloudflare codes survive, no coercion or arbitrary fields',async()=>{
    const f=replies(
        ()=>Response.json({success:false,errors:[
            {code:'10000',message:token},{code:-1},{code:1.5},{code:9999999999},
            {code:10000},{code:20000},{code:30000},{code:40000},{code:50000},
            {message:token}
        ]},{status:403}),
        ()=>new Response(token,{status:403,headers:{'Content-Type':'text/plain'}})
    );
    const result=await inspectAccess({env,fetchImpl:f.fetchImpl});
    assert.deepEqual((result.apps as {errorCodes?:number[]}).errorCodes,[10000,20000,30000,40000]);
    assert.deepEqual((result.organization as {errorCodes?:number[]}).errorCodes,[]);
    assert.ok(!JSON.stringify(result).includes(token));
});

test('oversized, malformed or stream-failed Access error bodies fail closed without leaking or retries',async()=>{
    for(const errorReply of [
        ()=>new Response('x'.repeat(500000),{status:403,headers:{'Content-Type':'application/json'}}),
        ()=>new Response('{"errors":[{"code":10000}],"secret":"'+token+'"', {status:403}),
        ()=>new Response(new ReadableStream({start(c){c.error(Error(token));}}),{status:403}),
    ]) {
        const f=replies(errorReply,()=>Response.json({success:false,errors:[{code:10001}]},{status:403}));
        const result=await inspectAccess({env,fetchImpl:f.fetchImpl});
        assert.equal(result.apps.status,403);
        assert.deepEqual((result.apps as {errorCodes?:number[]}).errorCodes,[]);
        assert.deepEqual((result.organization as {errorCodes?:number[]}).errorCodes,[10001]);
        assert.equal(f.calls.length,3);
        assert.ok(!JSON.stringify(result).includes(token));
    }
});

test('redirect remains blocked without following or reading response body',async()=>{
    const f=replies(
        ()=>Response.json({success:false,errors:[{code:10000,message:token}]},
            {status:302,headers:{Location:'https://evil.invalid/'}}),
        ()=>envelope({auth_domain:'example.cloudflareaccess.com'})
    );
    const result=await inspectAccess({env,fetchImpl:f.fetchImpl});
    assert.equal(result.apps.status,302);
    assert.deepEqual((result.apps as {errorCodes?:number[]}).errorCodes,[]);
    assert.equal(result.organization.configured,true);
    assert.equal(f.calls.length,3);
});
