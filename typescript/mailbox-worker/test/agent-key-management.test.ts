import assert from 'node:assert/strict';
import { test } from 'node:test';
import { start } from './harness.ts';
import { agentKey, clientAssertion, dpopProof, jws, keyEndorsement, keyProof, type TestKey } from './agent-key.ts';
import { createOwnedAgent, signInHuman } from '../src/humans.ts';
import { addAgentKey, revokeAgentKey } from '../src/agent-keys.ts';
import { Problem } from '../src/wire.ts';
import type { Env } from '../src/database.ts';

type Harness = Awaited<ReturnType<typeof start>>;
type Credential = {accountId: string; key: TestKey; token: string};
type Summary = {kid: string; label: string | null; createdAt: string; lastUsedAt: string | null; coolingUntil: string | null};
type AddOverrides = {proof?: string; endorsement?: string; label?: string; body?: unknown; endorser?: TestKey; proofSub?: string; newKid?: string};

const origin = 'http://localhost';
const keysUrl = origin + '/v1/keys';
const tokenUrl = origin + '/v1/token';
const assertionType = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer';

async function registerAgent(h: Harness): Promise<{accountId: string; key: TestKey}> {
    const key = agentKey(), url = origin + '/v1/accounts';
    const response = await h.mf.dispatchFetch(url, {method: 'POST', headers: {'Content-Type': 'application/json', DPoP: dpopProof(key, {method: 'POST', url})}, body: '{}'});
    assert.equal(response.status, 200, await response.clone().text());
    return {accountId: (await response.json() as {accountId: string}).accountId, key};
}
async function issue(h: Harness, accountId: string, key: TestKey): Promise<Credential> {
    const assertion = clientAssertion(key, {accountId, audience: tokenUrl});
    const response = await h.mf.dispatchFetch(tokenUrl, {method: 'POST', headers: {'Content-Type': 'application/x-www-form-urlencoded', DPoP: dpopProof(key, {method: 'POST', url: tokenUrl})},
        body: new URLSearchParams([['grant_type', 'client_credentials'], ['client_assertion_type', assertionType], ['client_assertion', assertion]]).toString()});
    assert.equal(response.status, 200, await response.clone().text());
    return {accountId, key, token: (await response.json() as {access_token: string}).access_token};
}
async function agent(h: Harness): Promise<Credential> {
    const {accountId, key} = await registerAgent(h);
    return issue(h, accountId, key);
}
function call(h: Harness, cred: Credential, path: string, method = 'GET', body?: unknown): Promise<Response> {
    const url = origin + path;
    return h.mf.dispatchFetch(url, {method, headers: {Authorization: `DPoP ${cred.token}`, DPoP: dpopProof(cred.key, {method, url, accessToken: cred.token}),
        ...(body === undefined ? {} : {'Content-Type': 'application/json'})}, ...(body === undefined ? {} : {body: JSON.stringify(body)})}) as unknown as Promise<Response>;
}
function addKey(h: Harness, cred: Credential, newKey: TestKey, overrides: AddOverrides = {}): Promise<Response> {
    const proof = overrides.proof ?? keyProof(newKey, {accountId: overrides.proofSub ?? cred.accountId, audience: keysUrl});
    const endorser = overrides.endorser ?? cred.key;
    const endorsement = overrides.endorsement ?? keyEndorsement(endorser, {accountId: cred.accountId, audience: keysUrl, newKid: overrides.newKid ?? newKey.kid});
    return call(h, cred, '/v1/keys', 'POST', overrides.body ?? {proof, endorsement, ...(overrides.label === undefined ? {} : {label: overrides.label})});
}
async function added(h: Harness, cred: Credential, newKey: TestKey, overrides: AddOverrides = {}): Promise<Summary> {
    const response = await addKey(h, cred, newKey, overrides);
    assert.equal(response.status, 200, await response.clone().text());
    return response.json() as Promise<Summary>;
}
const revoke = (h: Harness, cred: Credential, kid: string) => call(h, cred, '/v1/keys/' + kid, 'DELETE');
async function problem(response: Response, status: number, expected: Record<string, unknown>): Promise<Record<string, unknown>> {
    const text = await response.text();
    assert.equal(response.status, status, text);
    const value = JSON.parse(text) as Record<string, unknown>;
    for (const [name, wanted] of Object.entries(expected)) assert.equal(value[name], wanted, text);
    return value;
}
const liveKeys = async (h: Harness, accountId: string) => Number(await h.db.prepare('SELECT count(*) n FROM agent_keys WHERE account_id=? AND revoked_at IS NULL').bind(accountId).first('n'));
const status = (outcome: PromiseSettledResult<unknown>) => outcome.status === 'fulfilled' ? 200 : outcome.reason instanceof Problem ? outcome.reason.status : 0;
const settle = (h: Harness, kid: string) => h.db.prepare('UPDATE agent_keys SET cooling_until=NULL WHERE kid=?').bind(kid).run();

test('addKey_ProofAndEndorsement_CoolsFor24hAndAuthenticates', async () => {
    const h = await start();
    try {
        const cred = await agent(h), newKey = agentKey();
        const summary = await added(h, cred, newKey, {label: 'phone'});
        assert.deepEqual(Object.keys(summary).sort(), ['coolingUntil', 'createdAt', 'kid', 'label', 'lastUsedAt']);
        assert.equal(summary.kid, newKey.kid);
        assert.equal(summary.label, 'phone');
        assert.equal(summary.lastUsedAt, null);
        assert.equal(Date.parse(summary.coolingUntil!) - Date.parse(summary.createdAt), 86400000);
        const row = await h.db.prepare('SELECT account_id,public_jwk FROM agent_keys WHERE kid=?').bind(newKey.kid).first<any>();
        assert.equal(row.account_id, cred.accountId);
        assert.deepEqual(JSON.parse(row.public_jwk), {crv: 'Ed25519', kty: 'OKP', x: newKey.publicJwk.x});
        const second = await issue(h, cred.accountId, newKey);
        assert.equal((await call(h, second, '/v1/addresses')).status, 200);
    } finally { await h.close(); }
});

test('addKey_ExtraPublicJwkMembers_AcceptedAndStoredCanonical', async () => {
    const h = await start();
    try {
        const cred = await agent(h), newKey = agentKey();
        const proof = keyProof(newKey, {accountId: cred.accountId, audience: keysUrl, header: {jwk: {...newKey.publicJwk, kid: 'mine', alg: 'EdDSA', use: 'sig'}}});
        await added(h, cred, newKey, {proof});
        assert.deepEqual(JSON.parse(await h.db.prepare('SELECT public_jwk FROM agent_keys WHERE kid=?').bind(newKey.kid).first<string>('public_jwk') ?? ''),
            {crv: 'Ed25519', kty: 'OKP', x: newKey.publicJwk.x});
    } finally { await h.close(); }
});

test('addKey_Retry_ReturnsSameKey', async () => {
    const h = await start();
    try {
        const cred = await agent(h), newKey = agentKey();
        const first = await added(h, cred, newKey, {label: 'first'});
        const second = await added(h, cred, newKey, {label: 'second'});
        assert.deepEqual(second, first);
        assert.equal(await liveKeys(h, cred.accountId), 2);
    } finally { await h.close(); }
});

test('addKey_ProofForOtherAgent_400', async () => {
    const h = await start();
    try {
        const cred = await agent(h), other = await registerAgent(h), newKey = agentKey();
        await problem(await addKey(h, cred, newKey, {proofSub: other.accountId}), 400, {code: 'invalid', field: 'proof'});
        await problem(await addKey(h, cred, newKey, {proof: keyProof(newKey, {accountId: cred.accountId, audience: origin + '/v1/token'})}), 400, {code: 'invalid', field: 'proof'});
        await problem(await addKey(h, cred, newKey, {proof: keyProof(newKey, {accountId: cred.accountId, audience: keysUrl, header: {typ: 'dpop+jwt'}})}), 400, {code: 'invalid', field: 'proof'});
        await problem(await addKey(h, cred, newKey, {proof: keyProof(newKey, {accountId: cred.accountId, audience: keysUrl, iat: Math.floor(Date.now() / 1000) - 120})}), 400, {code: 'invalid', field: 'proof'});
        // Signed by a key other than the one in its header.
        await problem(await addKey(h, cred, newKey, {proof: keyProof(agentKey(), {accountId: cred.accountId, audience: keysUrl, header: {jwk: newKey.publicJwk}})}), 400, {code: 'invalid', field: 'proof'});
        assert.equal(await liveKeys(h, cred.accountId), 1);
    } finally { await h.close(); }
});

test('addKey_EndorsementForOtherNewKid_400', async () => {
    const h = await start();
    try {
        const cred = await agent(h), newKey = agentKey();
        await problem(await addKey(h, cred, newKey, {newKid: agentKey().kid}), 400, {code: 'invalid', field: 'endorsement'});
        const other = await registerAgent(h);
        await problem(await addKey(h, cred, newKey, {endorsement: keyEndorsement(cred.key, {accountId: other.accountId, audience: keysUrl, newKid: newKey.kid})}), 400, {code: 'invalid', field: 'endorsement'});
        await problem(await addKey(h, cred, newKey, {endorsement: keyEndorsement(cred.key, {accountId: cred.accountId, audience: keysUrl + '/', newKid: newKey.kid})}), 400, {code: 'invalid', field: 'endorsement'});
        await problem(await addKey(h, cred, newKey, {endorsement: keyEndorsement(cred.key, {accountId: cred.accountId, audience: keysUrl, newKid: newKey.kid, iat: Math.floor(Date.now() / 1000) + 120})}), 400, {code: 'invalid', field: 'endorsement'});
        await problem(await addKey(h, cred, newKey, {endorsement: 'not-a-jws'}), 400, {code: 'invalid', field: 'endorsement'});
        assert.equal(await liveKeys(h, cred.accountId), 1);
    } finally { await h.close(); }
});

test('addKey_EndorsementWrongTyp_400', async () => {
    const h = await start();
    try {
        const cred = await agent(h), newKey = agentKey();
        const payload = {aud: keysUrl, sub: cred.accountId, iat: Math.floor(Date.now() / 1000), newKid: newKey.kid};
        for (const typ of ['weave-key-proof+jwt', 'dpop+jwt', undefined])
            await problem(await addKey(h, cred, newKey, {endorsement: jws(cred.key, {typ, kid: cred.key.kid}, payload)}), 400, {code: 'invalid', field: 'endorsement'});
        // Positive control: the same claims with the endorsement type are accepted.
        await added(h, cred, newKey, {endorsement: jws(cred.key, {typ: 'weave-key-endorsement+jwt', kid: cred.key.kid}, payload)});
    } finally { await h.close(); }
});

test('addKey_EndorsementKidNotCaller_400', async () => {
    const h = await start();
    try {
        const cred = await agent(h), second = agentKey(), newKey = agentKey();
        await added(h, cred, second);
        await settle(h, second.kid);
        // A live, settled key of the same agent still cannot endorse for a request authenticated by another key.
        await problem(await addKey(h, cred, newKey, {endorser: second}), 400, {code: 'invalid', field: 'endorsement'});
        // A self-endorsement by the new key is not an endorsement by the caller.
        await problem(await addKey(h, cred, newKey, {endorser: newKey}), 400, {code: 'invalid', field: 'endorsement'});
        assert.equal(await liveKeys(h, cred.accountId), 2);
    } finally { await h.close(); }
});

test('addKey_NewJwkWithD_400AndNotStored', async () => {
    const h = await start();
    try {
        const cred = await agent(h), newKey = agentKey();
        const proof = keyProof(newKey, {accountId: cred.accountId, audience: keysUrl, header: {jwk: newKey.privateJwk}});
        const response = await addKey(h, cred, newKey, {proof});
        const text = await response.clone().text();
        await problem(response, 400, {code: 'invalid', field: 'proof'});
        assert.equal(text.includes(newKey.privateJwk.d!), false);
        assert.equal(await h.db.prepare('SELECT count(*) n FROM agent_keys WHERE kid=?').bind(newKey.kid).first('n'), 0);
        assert.equal(await h.db.prepare("SELECT count(*) n FROM agent_keys WHERE public_jwk LIKE '%\"d\"%'").first('n'), 0);
    } finally { await h.close(); }
});

test('addKey_BodyNotExactlyProofEndorsementLabel_400', async () => {
    const h = await start();
    try {
        const cred = await agent(h), newKey = agentKey();
        const proof = keyProof(newKey, {accountId: cred.accountId, audience: keysUrl});
        const endorsement = keyEndorsement(cred.key, {accountId: cred.accountId, audience: keysUrl, newKid: newKey.kid});
        await problem(await addKey(h, cred, newKey, {body: {proof, endorsement, extra: 1}}), 400, {code: 'invalid'});
        await problem(await addKey(h, cred, newKey, {body: {proof}}), 400, {code: 'invalid'});
        await problem(await addKey(h, cred, newKey, {label: ''}), 400, {code: 'invalid', field: 'label'});
        assert.equal(await liveKeys(h, cred.accountId), 1);
    } finally { await h.close(); }
});

test('addKey_CoolingKeyEndorses_409Cooling', async () => {
    const h = await start();
    try {
        const cred = await agent(h), cooling = agentKey();
        await added(h, cred, cooling);
        const coolingCred = await issue(h, cred.accountId, cooling);
        await problem(await addKey(h, coolingCred, agentKey()), 409, {code: 'conflict', field: 'endorsement', reason: 'cooling'});
        assert.equal(await liveKeys(h, cred.accountId), 2);
    } finally { await h.close(); }
});

test('addKey_KeyOfAnotherAgent_409KeyInUse', async () => {
    const h = await start();
    try {
        const cred = await agent(h), other = await registerAgent(h);
        await problem(await addKey(h, cred, other.key), 409, {code: 'conflict', field: 'proof', reason: 'key-in-use'});
        // A revoked key of this agent is not reusable either.
        const old = agentKey();
        await added(h, cred, old);
        assert.equal((await revoke(h, cred, old.kid)).status, 200);
        await problem(await addKey(h, cred, old), 409, {code: 'conflict', field: 'proof', reason: 'key-in-use'});
        assert.equal(await h.db.prepare('SELECT account_id FROM agent_keys WHERE kid=?').bind(other.key.kid).first('account_id'), other.accountId);
    } finally { await h.close(); }
});

async function fillTo(h: Harness, accountId: string, live: number): Promise<string[]> {
    const kids: string[] = [];
    // Created 25 h ago, so the 24-hour key-addition limit does not mask the live-key limit under test.
    const now = (BigInt(Date.now()) * 10000n - 900000000000n).toString();
    for (let n = await liveKeys(h, accountId); n < live; n++) {
        const key = agentKey();
        kids.push(key.kid);
        await h.db.prepare('INSERT INTO agent_keys(kid,account_id,public_jwk,created_at,updated_at) VALUES(?,?,?,CAST(? AS INTEGER),CAST(? AS INTEGER))')
            .bind(key.kid, accountId, JSON.stringify(key.publicJwk), now, now).run();
    }
    return kids;
}

test('addKey_HundredLiveKeys_429ThenAfterRevokeSucceeds', async () => {
    const h = await start();
    try {
        const cred = await agent(h);
        const extra = await fillTo(h, cred.accountId, 100);
        assert.equal(extra.length, 99);
        const newKey = agentKey();
        await problem(await addKey(h, cred, newKey), 429, {code: 'capacity'});
        assert.equal(await liveKeys(h, cred.accountId), 100);
        assert.equal((await revoke(h, cred, extra[0])).status, 200);
        await added(h, cred, newKey);
        assert.equal(await liveKeys(h, cred.accountId), 100);
    } finally { await h.close(); }
});

test('addKey_ConcurrentAtNinetyNine_ExactlyOneSucceeds', async () => {
    const h = await start();
    try {
        const cred = await agent(h);
        await fillTo(h, cred.accountId, 99);
        // Same authenticated caller, prebuilt requests: the race is decided by the count inside the insert transaction.
        const env = {db: h.db} as unknown as Env, auth = {accountId: cred.accountId, kid: cred.key.kid};
        const attempt = () => {
            const newKey = agentKey();
            return addAgentKey(env, new Request(keysUrl, {method: 'POST'}), auth, {
                proof: keyProof(newKey, {accountId: cred.accountId, audience: keysUrl}),
                endorsement: keyEndorsement(cred.key, {accountId: cred.accountId, audience: keysUrl, newKid: newKey.kid})});
        };
        const outcomes = await Promise.allSettled([attempt(), attempt()]);
        assert.deepEqual(outcomes.map(status).sort(), [200, 429]);
        assert.equal(await liveKeys(h, cred.accountId), 100);
    } finally { await h.close(); }
});

test('addKey_HumanOwnedAgent_202AwaitingOwner', async () => {
    const h = await start();
    try {
        const env = {db: h.db, REGISTRATION_ENABLED: 'true', REGISTRATION_MAX_ACCOUNTS: '100', REGISTRATION_PER_IP_PER_HOUR: '100', REGISTRATION_GLOBAL_PER_HOUR: '100'} as unknown as Env;
        const human = await signInHuman(env, {provider: 'github', subject: '123', providerCreatedAt: '2020-01-01T00:00:00Z', displayLogin: 'alice'});
        assert.equal(human.ok, true);
        const humanId = (human as {humanId: string}).humanId;
        const owned = await createOwnedAgent(env, {humanId, displayName: 'owned', sourceIp: '203.0.113.7'});
        assert.equal(owned.ok, true);
        const accountId = (owned as {accountId: string}).accountId, key = agentKey();
        const now = (BigInt(Date.now()) * 10000n).toString();
        await h.db.prepare('INSERT INTO agent_keys(kid,account_id,public_jwk,created_at,updated_at) VALUES(?,?,?,CAST(? AS INTEGER),CAST(? AS INTEGER))')
            .bind(key.kid, accountId, JSON.stringify(key.publicJwk), now, now).run();
        const cred = await issue(h, accountId, key), newKey = agentKey();
        const response = await addKey(h, cred, newKey);
        assert.equal(response.status, 202, await response.clone().text());
        assert.equal((await response.json() as {status: string}).status, 'pending');
        assert.equal(await liveKeys(h, accountId), 1);
    } finally { await h.close(); }
});

test('revokeKey_OlderKeyRevokesCoolingKey_ItsTokenFailsNextRequest', async () => {
    const h = await start();
    try {
        const cred = await agent(h), cooling = agentKey();
        await added(h, cred, cooling);
        const coolingCred = await issue(h, cred.accountId, cooling);
        assert.equal((await call(h, coolingCred, '/v1/addresses')).status, 200);
        const response = await revoke(h, cred, cooling.kid);
        assert.equal(response.status, 200, await response.clone().text());
        const result = await response.json() as {kid: string; revokedAt: string};
        assert.deepEqual(Object.keys(result).sort(), ['kid', 'revokedAt']);
        assert.equal(result.kid, cooling.kid);
        assert.match(result.revokedAt, /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$/);
        await problem(await call(h, coolingCred, '/v1/addresses'), 401, {code: 'unauthenticated'});
        assert.equal((await call(h, cred, '/v1/addresses')).status, 200);
    } finally { await h.close(); }
});

test('revokeKey_CoolingKeyRevokesOlder_409Cooling', async () => {
    const h = await start();
    try {
        const cred = await agent(h), cooling = agentKey();
        await added(h, cred, cooling);
        const coolingCred = await issue(h, cred.accountId, cooling);
        await problem(await revoke(h, coolingCred, cred.key.kid), 409, {code: 'conflict', field: 'kid', reason: 'cooling'});
        assert.equal(await liveKeys(h, cred.accountId), 2);
        assert.equal((await call(h, cred, '/v1/addresses')).status, 200);
    } finally { await h.close(); }
});

test('revokeKey_CoolingKeyRevokesNewerKey_409Cooling', async () => {
    const h = await start();
    try {
        const cred = await agent(h), cooling = agentKey(), newer = agentKey();
        await added(h, cred, cooling);
        await added(h, cred, newer);
        const coolingCred = await issue(h, cred.accountId, cooling);
        await problem(await revoke(h, coolingCred, newer.kid), 409, {code: 'conflict', field: 'kid', reason: 'cooling'});
        // A cooling key may revoke only itself.
        assert.equal((await revoke(h, coolingCred, cooling.kid)).status, 200);
        assert.equal(await liveKeys(h, cred.accountId), 2);
    } finally { await h.close(); }
});

test('revokeKey_OldKeySelfRevokesDuringCooling_409Cooling', async () => {
    const h = await start();
    try {
        const cred = await agent(h), cooling = agentKey();
        await added(h, cred, cooling);
        const coolingCred = await issue(h, cred.accountId, cooling);
        // The settled key must survive the window so it can still revoke the cooling key.
        await problem(await revoke(h, cred, cred.key.kid), 409, {code: 'conflict', field: 'kid', reason: 'cooling'});
        assert.equal((await call(h, cred, '/v1/addresses')).status, 200);
        assert.equal((await call(h, coolingCred, '/v1/addresses')).status, 200);
        assert.equal(await liveKeys(h, cred.accountId), 2);
    } finally { await h.close(); }
});

test('revokeKey_SettledRevokesSettledWhileKeyCools_409Cooling', async () => {
    const h = await start();
    try {
        const cred = await agent(h), settled = agentKey(), cooling = agentKey();
        await added(h, cred, settled);
        await settle(h, settled.kid);
        await added(h, cred, cooling);
        await problem(await revoke(h, cred, settled.kid), 409, {code: 'conflict', field: 'kid', reason: 'cooling'});
        const settledCred = await issue(h, cred.accountId, settled);
        await problem(await revoke(h, settledCred, cred.key.kid), 409, {code: 'conflict', field: 'kid', reason: 'cooling'});
        assert.equal(await liveKeys(h, cred.accountId), 3);
    } finally { await h.close(); }
});

test('revokeKey_CoolingKeyFirstThenSettled_BothSucceed', async () => {
    const h = await start();
    try {
        const cred = await agent(h), settled = agentKey(), cooling = agentKey();
        await added(h, cred, settled);
        await settle(h, settled.kid);
        await added(h, cred, cooling);
        assert.equal((await revoke(h, cred, cooling.kid)).status, 200);
        assert.equal((await revoke(h, cred, settled.kid)).status, 200);
        assert.equal(await liveKeys(h, cred.accountId), 1);
    } finally { await h.close(); }
});

test('revokeKey_CoolingEnded_SettledKeyRevocable', async () => {
    const h = await start();
    try {
        const cred = await agent(h), cooling = agentKey();
        await added(h, cred, cooling);
        const until = BigInt(await h.db.prepare('SELECT CAST(cooling_until AS TEXT) t FROM agent_keys WHERE kid=?').bind(cooling.kid).first<string>('t') ?? '0');
        const env = {db: h.db} as unknown as Env, auth = {accountId: cred.accountId, kid: cred.key.kid};
        await assert.rejects(revokeAgentKey(env, auth, cred.key.kid, () => until - 1n),
            (error: unknown) => error instanceof Problem && error.status === 409 && error.detail?.reason === 'cooling');
        // Cooling ends exactly at coolingUntil.
        const result = await revokeAgentKey(env, auth, cred.key.kid, () => until);
        assert.equal(result.kid, cred.key.kid);
        assert.equal(await liveKeys(h, cred.accountId), 1);
    } finally { await h.close(); }
});

test('revokeKey_LastLiveKey_409', async () => {
    const h = await start();
    try {
        const cred = await agent(h);
        await problem(await revoke(h, cred, cred.key.kid), 409, {code: 'conflict', field: 'kid', reason: 'last-live-key'});
        assert.equal(await liveKeys(h, cred.accountId), 1);
        assert.equal((await call(h, cred, '/v1/addresses')).status, 200);
    } finally { await h.close(); }
});

test('revokeKey_ConcurrentLastTwo_OneSucceeds', async () => {
    const h = await start();
    try {
        const cred = await agent(h), second = agentKey();
        await added(h, cred, second);
        await settle(h, second.kid);
        // Both callers are already authenticated, so the race is decided inside the revocation transaction.
        const env = {db: h.db} as unknown as Env;
        const outcomes = await Promise.allSettled([
            revokeAgentKey(env, {accountId: cred.accountId, kid: cred.key.kid}, second.kid),
            revokeAgentKey(env, {accountId: cred.accountId, kid: second.kid}, cred.key.kid)]);
        assert.deepEqual(outcomes.map(status).sort(), [200, 409]);
        const refused = outcomes.find(outcome => outcome.status === 'rejected') as PromiseRejectedResult;
        assert.deepEqual(refused.reason.detail, {field: 'kid', reason: 'last-live-key'});
        assert.equal(await liveKeys(h, cred.accountId), 1);
    } finally { await h.close(); }
});

test('revokeKey_Repeat_Idempotent', async () => {
    const h = await start();
    try {
        const cred = await agent(h), second = agentKey();
        await added(h, cred, second);
        const first = await revoke(h, cred, second.kid);
        assert.equal(first.status, 200, await first.clone().text());
        const repeat = await revoke(h, cred, second.kid);
        assert.equal(repeat.status, 200, await repeat.clone().text());
        assert.deepEqual(await repeat.json(), await first.json());
    } finally { await h.close(); }
});

test('revokeKey_OtherAgentsKid_404', async () => {
    const h = await start();
    try {
        const cred = await agent(h), other = await registerAgent(h);
        await problem(await revoke(h, cred, other.key.kid), 404, {code: 'unavailable'});
        await problem(await revoke(h, cred, agentKey().kid), 404, {code: 'unavailable'});
        await problem(await revoke(h, cred, 'not-a-kid'), 404, {code: 'unavailable'});
        await problem(await revoke(h, cred, cred.key.kid + 'A'), 404, {code: 'unavailable'});
        assert.equal(await liveKeys(h, other.accountId), 1);
    } finally { await h.close(); }
});

test('listKeys_LiveKeysOnlyOldestFirst', async () => {
    const h = await start();
    try {
        const cred = await agent(h), revoked = agentKey(), kept = agentKey();
        await added(h, cred, revoked);
        const keptSummary = await added(h, cred, kept, {label: 'kept'});
        assert.equal((await revoke(h, cred, revoked.kid)).status, 200);
        const response = await call(h, cred, '/v1/keys');
        assert.equal(response.status, 200, await response.clone().text());
        const {items} = await response.json() as {items: Summary[]};
        assert.deepEqual(items.map(item => item.kid), [cred.key.kid, kept.kid]);
        assert.deepEqual(items[1], keptSummary);
        assert.equal(items[0].coolingUntil, null);
        assert.notEqual(items[0].lastUsedAt, null);
    } finally { await h.close(); }
});
