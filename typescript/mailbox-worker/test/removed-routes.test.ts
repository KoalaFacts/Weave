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
