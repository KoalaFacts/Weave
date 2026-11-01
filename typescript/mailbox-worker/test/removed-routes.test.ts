import assert from 'node:assert/strict';
import { test } from 'node:test';
import { start } from './harness.ts';

const trialBindings={
    ACCESS_TRIAL_ENABLED:'true',ACCESS_TRIAL_ISSUER:'https://synthetic-team.cloudflareaccess.com',
    ACCESS_TRIAL_RESOURCE:'https://localhost/access-trial/mcp',ACCESS_TRIAL_AUDIENCE:'a'.repeat(64),
    OAUTH_IDENTITY_LINKS_ENABLED:'true'
};

test('accessTrial_Removed_LinkAndMcpNotServed',async()=>{
    const h=await start({bindings:trialBindings});
    try{
        for(const path of ['/access-trial/link','/access-trial/mcp']){
            const response=await h.mf.dispatchFetch('https://localhost'+path,{
                method:'POST',headers:{'Content-Type':'application/json',Accept:'application/json, text/event-stream',
                    'Cf-Access-Jwt-Assertion':'synthetic.access.assertion'},
                body:JSON.stringify({consent:true})});
            assert.equal(response.status,401,path);
            assert.equal((await response.json()).code,'unauthenticated',path);
        }
    }finally{await h.close();}
});

test('oauthLinks_Removed_Returns401',async()=>{
    const h=await start({bindings:{OAUTH_IDENTITY_LINKS_ENABLED:'true'}});
    try{
        const response=await h.mf.dispatchFetch('https://localhost/v1/accounts/oauth/links');
        assert.equal(response.status,401);
    }finally{await h.close();}
});

test('controlHeaders_NoLongerAuthenticate',async()=>{
    const h=await start();
    try{
        await h.register();
        const accounts=async()=>(await h.db.prepare('SELECT count(*) n FROM accounts').first<number>('n'));
        const before=await accounts();
        const control='a1'.repeat(32);
        for(const header of ['X-Weave-Account-Control','X-Weave-Mailbox-Control']){
            const response=await h.mf.dispatchFetch('http://localhost/v1/addresses',{headers:{[header]:control}});
            assert.equal(response.status,401,header);
            const problem=await response.json();
            assert.equal(problem.code,'unauthenticated',header);
            assert.equal(problem.title,'Agent authentication required',header);
        }
        const registration=await h.mf.dispatchFetch('http://localhost/v1/accounts',{method:'POST',
            headers:{'Content-Type':'application/json','X-Weave-Account-Control':control},body:'{}'});
        assert.equal(registration.status,401);
        assert.equal(await accounts(),before);
    }finally{await h.close();}
});

test('schema_AccountsHaveNoControlColumns',async()=>{
    const h=await start();
    try{
        const columns=(await h.db.prepare('PRAGMA table_info(accounts)').all<{name:string}>()).results.map(c=>c.name);
        assert.ok(columns.includes('account_id'));
        assert.deepEqual(columns.filter(name=>name.startsWith('control_')),[]);
    }finally{await h.close();}
});
