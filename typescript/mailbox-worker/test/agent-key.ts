// Test-only Ed25519 agent key and JWS builders; production code verifies with WebCrypto in src/jose.ts.
import { createHash, createPrivateKey, generateKeyPairSync, randomUUID, sign } from 'node:crypto';
import type { Ed25519Jwk } from '../src/jose.ts';
import { epochSeconds } from '../src/time.ts';

export type TestKey = {kid: string; publicJwk: Ed25519Jwk; privateJwk: JsonWebKey; sign(data: Uint8Array): Uint8Array};

const encode=(value: unknown) => Buffer.from(JSON.stringify(value)).toString('base64url');

/** A new Ed25519 key, or the key in a saved private JWK (for example a demo key-file backup). */
export function agentKey(savedJwk?: JsonWebKey): TestKey {
    const privateKey=savedJwk===undefined ? generateKeyPairSync('ed25519').privateKey : createPrivateKey({key:{...savedJwk},format:'jwk'});
    const privateJwk=privateKey.export({format:'jwk'}) as JsonWebKey;
    const publicJwk: Ed25519Jwk={kty:'OKP',crv:'Ed25519',x:privateJwk.x!};
    const kid=createHash('sha256').update(JSON.stringify({crv:publicJwk.crv,kty:publicJwk.kty,x:publicJwk.x})).digest('base64url');
    return {kid,publicJwk,privateJwk,sign:data => new Uint8Array(sign(null,data,privateKey))};
}

export const nowSeconds=(): number => epochSeconds();

export function jws(key: TestKey, header: object, payload: object): string {
    const signingInput=`${encode({alg:'EdDSA',...header})}.${encode(payload)}`;
    return `${signingInput}.${Buffer.from(key.sign(Buffer.from(signingInput))).toString('base64url')}`;
}

export function dpopProof(key: TestKey, options: {method: string; url: string; accessToken?: string; iat?: number; jti?: string; header?: object; claims?: object}): string {
    const ath=options.accessToken===undefined ? undefined : createHash('sha256').update(options.accessToken).digest('base64url');
    return jws(key,{typ:'dpop+jwt',jwk:key.publicJwk,...options.header},
        {jti:options.jti??randomUUID(),htm:options.method,htu:options.url,iat:options.iat??nowSeconds(),ath,...options.claims});
}

export function clientAssertion(key: TestKey, options: {accountId: string; audience: string; iat?: number; exp?: number; jti?: string; header?: object; claims?: object}): string {
    const now=nowSeconds();
    return jws(key,{kid:key.kid,...options.header},
        {iss:options.accountId,sub:options.accountId,aud:options.audience,iat:options.iat??now,exp:options.exp??now+120,jti:options.jti??randomUUID(),...options.claims});
}

export function keyProof(newKey: TestKey, options: {accountId: string; audience: string; iat?: number; header?: object}): string {
    return jws(newKey,{typ:'weave-key-proof+jwt',jwk:newKey.publicJwk,...options.header},
        {aud:options.audience,sub:options.accountId,iat:options.iat??nowSeconds()});
}

export function keyEndorsement(existing: TestKey, options: {accountId: string; audience: string; newKid: string; iat?: number}): string {
    return jws(existing,{typ:'weave-key-endorsement+jwt',kid:existing.kid},
        {aud:options.audience,sub:options.accountId,iat:options.iat??nowSeconds(),newKid:options.newKid});
}

export function bindingRequest(key: TestKey, options: {accountId: string; audience: string; human: string; iat?: number; header?: object; claims?: object}): string {
    return jws(key,{typ:'weave-binding-request+jwt',kid:key.kid,...options.header},
        {aud:options.audience,sub:options.accountId,iat:options.iat??nowSeconds(),human:options.human,...options.claims});
}
