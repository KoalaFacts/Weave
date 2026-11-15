import assert from 'node:assert/strict';
import { test } from 'node:test';
import { openSecret, sealSecret, type VaultBinding } from '../src/credential-vault.ts';

const keyOf = (bytes: number) => Buffer.from(crypto.getRandomValues(new Uint8Array(bytes))).toString('base64url');
const binding: VaultBinding = {accountId: crypto.randomUUID(), credentialRef: 'main', provider: 'postiz', revision: 'r1'};

test('openSecret_EachBindingMember_BoundIntoTheCiphertext', async () => {
    const key = keyOf(32);
    const sealed = await sealSecret(key, binding, 'secret-value');
    assert.equal(await openSecret(key, binding, sealed), 'secret-value', 'the same binding opens it');
    for (const member of ['accountId', 'credentialRef', 'provider', 'revision'] as const)
        await assert.rejects(openSecret(key, {...binding, [member]: `${binding[member]}x`}, sealed), member);
    // The members are framed, so moving characters between them is another binding.
    await assert.rejects(openSecret(key, {...binding, credentialRef: 'mai', provider: 'npostiz'}, sealed), 'framing');
    await assert.rejects(openSecret(keyOf(32), binding, sealed), 'another key');
});

test('sealSecret_SamePlaintext_FreshIvEachTime', async () => {
    const key = keyOf(32);
    const first = await sealSecret(key, binding, 'secret-value'), second = await sealSecret(key, binding, 'secret-value');
    assert.equal(first.byteLength, 12 + 'secret-value'.length + 16, 'IV, ciphertext, 16-byte tag');
    assert.notDeepEqual(first.slice(0, 12), second.slice(0, 12), 'a GCM nonce is never reused under one key');
    assert.equal(await openSecret(key, binding, second), 'secret-value');
});

test('openSecret_TamperedOrTruncatedBytes_Rejects', async () => {
    const key = keyOf(32);
    const sealed = await sealSecret(key, binding, 'secret-value');
    for (const index of [0, 11, 12, sealed.byteLength - 1]) {
        const tampered = sealed.slice();
        tampered[index] ^= 1;
        await assert.rejects(openSecret(key, binding, tampered), `byte ${index}`);
    }
    await assert.rejects(openSecret(key, binding, sealed.slice(0, 12)), /truncated/);
    await assert.rejects(openSecret(key, binding, sealed.slice(0, 28)), 'IV and a tag but no ciphertext');
});

test('sealSecret_KeyNotThirtyTwoBytes_Throws', async () => {
    for (const key of [undefined, '', keyOf(16), keyOf(24), keyOf(31), keyOf(33), `${keyOf(32)}=`, 'not base64url!']) {
        await assert.rejects(sealSecret(key, binding, 'secret-value'), /EXTERNAL_CREDENTIAL_KEY must be 32 bytes/, String(key));
        await assert.rejects(openSecret(key, binding, new Uint8Array(40)), /EXTERNAL_CREDENTIAL_KEY must be 32 bytes/, String(key));
    }
});

test('openSecret_UnicodePlaintext_RoundTrips', async () => {
    const key = keyOf(32), plaintext = 'κλειδί 🔑 '.repeat(200);
    assert.equal(await openSecret(key, binding, await sealSecret(key, binding, plaintext)), plaintext);
});
