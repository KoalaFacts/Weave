import assert from 'node:assert/strict';
import { test } from 'node:test';
import { base64url, fromBase64url, jwkThumbprint, parseJws, publicJwk, verifyJws } from '../src/jose.ts';
import { agentKey, jws } from './agent-key.ts';

const rfc8037X='11qYAYKxCrfVS_7TyWQHOg7hcvPapiMlrwIaaPcHURo';
const encodeJson=(value: unknown) => Buffer.from(JSON.stringify(value)).toString('base64url');
const signature=Buffer.alloc(64).toString('base64url');

test('publicJwk_CanonicalMembers_ReturnsKey', () => {
    assert.deepEqual(publicJwk({kty:'OKP',crv:'Ed25519',x:rfc8037X}),{kty:'OKP',crv:'Ed25519',x:rfc8037X});
});

test('publicJwk_PrivateMemberD_ReturnsNull', () => {
    const key=agentKey();
    assert.equal(publicJwk(key.privateJwk),null);
    for(const member of ['d','p','k']) assert.equal(publicJwk({...key.publicJwk,[member]:key.publicJwk.x}),null,member);
});

test('publicJwk_ExtraPublicMembers_ReturnsCanonical', () => {
    const result=publicJwk({kty:'OKP',crv:'Ed25519',x:rfc8037X,kid:'any',alg:'EdDSA',use:'sig'});
    assert.ok(result);
    assert.deepEqual(Object.keys(result).sort(),['crv','kty','x']);
    assert.ok(publicJwk({kty:'OKP',crv:'Ed25519',x:rfc8037X,alg:'Ed25519'}));
});

test('publicJwk_WrongShape_ReturnsNull', () => {
    const base={kty:'OKP',crv:'Ed25519',x:rfc8037X};
    const last=rfc8037X.at(-1)!;
    const nonCanonical=rfc8037X.slice(0,-1)+String.fromCharCode(last.charCodeAt(0)+1);
    for(const value of [
        null,'key',[base],{},
        {...base,crv:'X25519'},{...base,kty:'EC'},
        {...base,x:Buffer.alloc(31,1).toString('base64url')},{...base,x:Buffer.alloc(33,1).toString('base64url')},
        {...base,x:rfc8037X+'='},{...base,x:nonCanonical},{...base,x:rfc8037X.replace('_','/')},{...base,x:1},
        {...base,use:'enc'},{...base,alg:'ES256'},{...base,alg:'none'},{...base,kid:7},{...base,key_ops:['verify']},
    ]) assert.equal(publicJwk(value),null,JSON.stringify(value));
});

test('fromBase64url_NonCanonical_ReturnsNull', () => {
    assert.deepEqual(fromBase64url('AQID'),new Uint8Array([1,2,3]));
    assert.equal(base64url(new Uint8Array([1,2,3,4])),'AQIDBA');
    for(const text of ['AQIDBA==','AQIDBB','AQIDB','AQ ID','AQ+D','AQ/D']) assert.equal(fromBase64url(text),null,text);
});

test('jwkThumbprint_Rfc8037Vector', async () => {
    assert.equal(await jwkThumbprint({kty:'OKP',crv:'Ed25519',x:rfc8037X}),'kPrK_qmxVWaYVA9wwBF6Iuo3vVzz7TxHCTwXBygrS4k');
});

test('jwkThumbprint_MatchesTestKeyKid', async () => {
    const key=agentKey();
    assert.equal(await jwkThumbprint(key.publicJwk),key.kid);
    assert.equal(key.kid.length,43);
});

test('parseJws_AlgNoneOrHs256_ReturnsNull', () => {
    const payload=encodeJson({sub:'a'});
    for(const alg of ['none','HS256','ES256','RS256',undefined]) assert.equal(parseJws(`${encodeJson({alg})}.${payload}.${signature}`),null,String(alg));
    assert.equal(parseJws(`${encodeJson({alg:'none'})}.${payload}.`),null);
    assert.ok(parseJws(`${encodeJson({alg:'EdDSA'})}.${payload}.${signature}`));
    assert.ok(parseJws(`${encodeJson({alg:'Ed25519'})}.${payload}.${signature}`));
});

test('parseJws_MalformedParts_ReturnsNull', () => {
    const header=encodeJson({alg:'EdDSA'}),payload=encodeJson({sub:'a'});
    for(const token of [
        `${header}.${payload}`,`${header}.${payload}.${signature}.x`,
        `${header}.${payload}.${Buffer.alloc(63).toString('base64url')}`,`${header}.${payload}.${signature}=`,
        `${encodeJson([1])}.${payload}.${signature}`,`${header}.${encodeJson('text')}.${signature}`,
        `${header}.${Buffer.from('{').toString('base64url')}.${signature}`,`${header}.${encodeJson(null)}.${signature}`,
    ]) assert.equal(parseJws(token),null,token);
});

test('parseJws_Crit_ReturnsNull', () => {
    assert.equal(parseJws(`${encodeJson({alg:'EdDSA',crit:['exp']})}.${encodeJson({})}.${signature}`),null);
});

test('parseJws_OverMaxLength_ReturnsNull', () => {
    const token=`${encodeJson({alg:'EdDSA'})}.${encodeJson({pad:'x'.repeat(4096)})}.${signature}`;
    assert.equal(parseJws(token),null);
    assert.ok(parseJws(token,token.length));
    assert.equal(parseJws(token,token.length-1),null);
});

test('verifyJws_RoundTrip_True', async () => {
    const key=agentKey();
    const parsed=parseJws(jws(key,{typ:'test'},{sub:'a'}));
    assert.ok(parsed);
    assert.deepEqual(parsed.header,{alg:'EdDSA',typ:'test'});
    assert.deepEqual(parsed.payload,{sub:'a'});
    assert.equal(await verifyJws(parsed,key.publicJwk),true);
    assert.equal(await verifyJws(parsed,agentKey().publicJwk),false);
});

test('verifyJws_TamperedPayload_False', async () => {
    const key=agentKey();
    const [header,,sig]=jws(key,{},{sub:'a'}).split('.');
    const parsed=parseJws(`${header}.${encodeJson({sub:'b'})}.${sig}`);
    assert.ok(parsed);
    assert.equal(await verifyJws(parsed,key.publicJwk),false);
});
