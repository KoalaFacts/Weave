import assert from 'node:assert/strict';
import { test } from 'node:test';
import { accessTokenHash, dpopWindowSeconds, htuOf, verifyDpop } from '../src/dpop.ts';
import { agentKey, dpopProof, jws } from './agent-key.ts';

const now=1_800_000_000;
const url='https://relay.example/v1/inbox';
const request=(proofs: string[], method='GET', target=url) => {
    const headers=new Headers();
    for(const proof of proofs) headers.append('DPoP',proof);
    return new Request(target,{method,headers});
};

test('verifyDpop_ValidProof_ReturnsThumbprint', async () => {
    const key=agentKey();
    const result=await verifyDpop(request([dpopProof(key,{method:'GET',url,iat:now})]),{now});
    assert.deepEqual(result,{ok:true,jkt:key.kid,jwk:key.publicJwk});
    assert.equal(dpopWindowSeconds,60);
});

test('verifyDpop_MissingOrMalformedProof_Fails', async () => {
    assert.deepEqual(await verifyDpop(request([]),{now}),{ok:false});
    assert.deepEqual(await verifyDpop(request(['not.a.jws']),{now}),{ok:false});
});

test('verifyDpop_IatAtWindowEdges', async () => {
    const key=agentKey();
    const verify=async (iat: unknown) => (await verifyDpop(request([dpopProof(key,{method:'GET',url,claims:{iat}})]),{now})).ok;
    assert.equal(await verify(now-60),true);
    assert.equal(await verify(now+60),true);
    assert.equal(await verify(now-61),false);
    assert.equal(await verify(now+61),false);
    for(const iat of [now+0.5,String(now),undefined,null]) assert.equal(await verify(iat),false,String(iat));
});

test('verifyDpop_HtuNormalisation', async () => {
    const key=agentKey();
    const target='https://Relay.Example:443/v1/inbox?limit=1';
    const verify=async (htu: string) => (await verifyDpop(request([dpopProof(key,{method:'GET',url:htu,iat:now})],'GET',target),{now})).ok;
    assert.equal(await verify('https://relay.example/v1/inbox'),true);
    assert.equal(await verify('https://relay.example/v1/inbox?x=1#f'),true);
    for(const htu of ['https://relay.example/v1/inbox/','http://relay.example/v1/inbox','https://other.example/v1/inbox','https://relay.example:8443/v1/inbox','https://user@relay.example/v1/inbox','not a url'])
        assert.equal(await verify(htu),false,htu);
    assert.equal(htuOf('https://Relay.Example:443/v1/inbox?limit=1#x'),'https://relay.example/v1/inbox');
    assert.equal(htuOf('http://relay.example:80/'),'http://relay.example/');
    assert.equal(htuOf('not a url'),null);
    assert.equal(htuOf('data:text/plain,x'),null);
});

test('verifyDpop_MethodMismatch_Fails', async () => {
    const key=agentKey();
    assert.equal((await verifyDpop(request([dpopProof(key,{method:'GET',url,iat:now})],'POST'),{now})).ok,false);
    assert.equal((await verifyDpop(request([dpopProof(key,{method:'post',url,iat:now})],'POST'),{now})).ok,false);
    assert.equal((await verifyDpop(request([dpopProof(key,{method:'POST',url,iat:now})],'POST'),{now})).ok,true);
});

test('verifyDpop_Ath_RequiredMatchedAndOnlyWithToken', async () => {
    const key=agentKey();
    const token='opaque-access-token';
    const verify=async (proof: string, accessToken?: string) => (await verifyDpop(request([proof]),{now,accessToken})).ok;
    assert.equal(await accessTokenHash(token),Buffer.from(await crypto.subtle.digest('SHA-256',new TextEncoder().encode(token))).toString('base64url'));
    assert.equal(await verify(dpopProof(key,{method:'GET',url,iat:now,accessToken:token}),token),true);
    assert.equal(await verify(dpopProof(key,{method:'GET',url,iat:now}),token),false);
    assert.equal(await verify(dpopProof(key,{method:'GET',url,iat:now,accessToken:'other-token'}),token),false);
    assert.equal(await verify(dpopProof(key,{method:'GET',url,iat:now,accessToken:token})),false);
    assert.equal(await verify(dpopProof(key,{method:'GET',url,iat:now,claims:{ath:null}})),false);
});

test('verifyDpop_JtiShape_Enforced', async () => {
    const key=agentKey();
    const verify=async (jti: unknown) => (await verifyDpop(request([dpopProof(key,{method:'GET',url,iat:now,claims:{jti}})]),{now})).ok;
    assert.equal(await verify('a'),true);
    assert.equal(await verify('x'.repeat(128)),true);
    for(const jti of ['','x'.repeat(129),'café','line\nbreak',7,undefined]) assert.equal(await verify(jti),false,String(jti));
});

test('verifyDpop_JwkWithD_Fails', async () => {
    const key=agentKey();
    assert.equal((await verifyDpop(request([dpopProof(key,{method:'GET',url,iat:now,header:{jwk:key.privateJwk}})]),{now})).ok,false);
    assert.equal((await verifyDpop(request([dpopProof(key,{method:'GET',url,iat:now,header:{jwk:undefined}})]),{now})).ok,false);
});

test('verifyDpop_TwoDpopHeaders_Fails', async () => {
    const key=agentKey();
    const proof=dpopProof(key,{method:'GET',url,iat:now});
    assert.deepEqual(await verifyDpop(request([proof,proof]),{now}),{ok:false});
});

test('verifyDpop_WrongTyp_Fails', async () => {
    const key=agentKey();
    for(const typ of ['jwt','DPoP+JWT','application/dpop+jwt',undefined])
        assert.equal((await verifyDpop(request([dpopProof(key,{method:'GET',url,iat:now,header:{typ}})]),{now})).ok,false,String(typ));
    assert.equal((await verifyDpop(request([jws(key,{typ:'dpop+jwt',jwk:key.publicJwk,alg:'ES256'},{jti:'a',htm:'GET',htu:url,iat:now})]),{now})).ok,false);
});

test('verifyDpop_SignedByOtherKey_Fails', async () => {
    const a=agentKey(),b=agentKey();
    assert.equal((await verifyDpop(request([dpopProof(b,{method:'GET',url,iat:now,header:{jwk:a.publicJwk}})]),{now})).ok,false);
});
