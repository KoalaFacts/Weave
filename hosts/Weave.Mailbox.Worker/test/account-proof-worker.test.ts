import assert from 'node:assert/strict';
import {randomBytes} from 'node:crypto';
import {test} from 'node:test';
import {start,secret} from './harness.ts';
import {verifyAccountProof} from '../src/account-proof.ts';

const key=randomBytes(32).toString('base64url');
const config={issuer:'https://login.weave.example',resource:'http://localhost/mcp',key};

test('D1 owners mint different consent challenges without disclosing controls or modifying account state',async()=>{
    const h=await start({bindings:{OAUTH_ISSUER:config.issuer,OAUTH_RESOURCE:config.resource,OAUTH_ACCOUNT_PROOF_KEY:key}});
    try{
        const page=await h.request('/account-proof');
        assert.equal(page.status,200,await page.clone().text());
        assert.equal(page.headers.get('cache-control'),'no-store');
        assert.match(page.headers.get('content-security-policy')??'',/frame-ancestors 'none'/);
        assert.match(await page.text(),/Verify your existing Weave account/);
        const script=await h.request('/account-proof.js');
        assert.equal(script.status,200,await script.clone().text());
        assert.match(await script.text(),/\/v1\/accounts\/proof/);
        const a=await h.register(),b=await h.register(),challenge=randomBytes(32).toString('base64url');
        const revision=await h.db.prepare('SELECT token FROM mailbox_revision WHERE singleton=1').first('token');
        const count=await h.db.prepare('SELECT count(*) n FROM accounts').first('n');
        for(const owner of [a,b]){
            const response=await h.request('/v1/accounts/proof',owner.control,'POST',{challenge});
            assert.equal(response.status,200,await response.clone().text());
            assert.equal(response.headers.get('cache-control'),'no-store');
            const value=await response.json();
            assert.deepEqual(Object.keys(value),['proof']);
            assert.equal(typeof value.proof,'string');
            assert.equal(value.proof.includes(owner.control),false);
            assert.equal(await verifyAccountProof(config,value.proof,challenge),owner.accountId);
            assert.notEqual(await verifyAccountProof(config,value.proof,challenge),owner===a?b.accountId:a.accountId);
        }
        for(const [owner,input,expected] of [[a,{challenge:'bad'},400],[a,{challenge,accountId:b.accountId},400],
            [{control:secret()},{challenge},401]] as const){
            const response=await h.request('/v1/accounts/proof',owner.control,'POST',input);
            assert.equal(response.status,expected);
            assert.equal((await response.text()).includes(owner.control),false);
        }
        assert.equal(await h.db.prepare('SELECT token FROM mailbox_revision WHERE singleton=1').first('token'),revision);
        assert.equal(await h.db.prepare('SELECT count(*) n FROM accounts').first('n'),count);
    }finally{await h.close();}
});

test('no consent proof is minted without an explicitly installed signing key or trusted origin',async()=>{
    const h=await start();
    try{const a=await h.register();assert.equal((await h.request('/v1/accounts/proof',a.control,'POST',
        {challenge:randomBytes(32).toString('base64url')})).status,503);
        assert.equal((await h.request('/account-proof')).status,503);
        assert.equal((await h.request('/account-proof.js')).status,503);
    }finally{await h.close();}
    const mismatch=await start({bindings:{OAUTH_ISSUER:config.issuer,OAUTH_RESOURCE:'https://wrong.example/mcp',OAUTH_ACCOUNT_PROOF_KEY:key}});
    try{const a=await mismatch.register();assert.equal((await mismatch.request('/v1/accounts/proof',a.control,'POST',
        {challenge:randomBytes(32).toString('base64url')})).status,503);}finally{await mismatch.close();}
});
