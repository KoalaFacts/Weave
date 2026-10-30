import assert from 'node:assert/strict';
import {randomBytes} from 'node:crypto';
import {test} from 'node:test';
import {issueAccountProof,verifyAccountProof} from '../src/account-proof.ts';

const key=randomBytes(32).toString('base64url');
const challenge=randomBytes(32).toString('base64url');
const account='01234567-89ab-4cde-8fff-0123456789ab';
const config={issuer:'https://login.weave.example',resource:'https://demo.weave-389.workers.dev/mcp',key};

test('account control stays at Weave and the AS verifies only signed, exact-account challenge proof',async()=>{
    const proof=await issueAccountProof(config,account,challenge,1000);
    assert.equal(await verifyAccountProof(config,proof,challenge,1000),account);
    assert.equal(proof.includes(key),false);
    assert.equal(proof.includes(account),false,'account ID is payload-encoded, not a URL parameter; it is not secret');
    await assert.rejects(verifyAccountProof(config,proof,randomBytes(32).toString('base64url'),1000));
    await assert.rejects(verifyAccountProof({...config,resource:'https://other.example/mcp'},proof,challenge,1000));
    await assert.rejects(verifyAccountProof({...config,issuer:'https://evil.example'},proof,challenge,1000));
    await assert.rejects(verifyAccountProof({...config,key:randomBytes(32).toString('base64url')},proof,challenge,1000));
});

test('proof signing rejects manipulation, expiry, future issue and non-canonical inputs',async()=>{
    const proof=await issueAccountProof(config,account,challenge,2000);
    await assert.rejects(verifyAccountProof(config,proof.slice(0,-1)+(proof.at(-1)==='a'?'b':'a'),challenge,2000));
    await assert.rejects(verifyAccountProof(config,proof,challenge,2060),'expires at exactly 60s');
    await assert.rejects(verifyAccountProof(config,proof,challenge,1994),'future issue >5s');
    await assert.rejects(issueAccountProof(config,'other',challenge,2000));
    await assert.rejects(issueAccountProof(config,account,'unsafe',2000));
    await assert.rejects(issueAccountProof({...config,key:'short'},account,challenge,2000));
    await assert.rejects(issueAccountProof({...config,issuer:'http://evil.example'},account,challenge,2000));
    await assert.rejects(issueAccountProof({...config,resource:'https://other.example/anything'},account,challenge,2000));
});
