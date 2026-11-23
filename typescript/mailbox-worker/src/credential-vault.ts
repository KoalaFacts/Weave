/**
 * Encryption of external credential secrets at rest. AES-256-GCM under EXTERNAL_CREDENTIAL_KEY (a Worker secret,
 * 32 bytes base64url). The additional data binds the row: a ciphertext copied to another agent, reference, provider or
 * revision fails to decrypt, so an old secret can never stand in for a newer revision.
 */
import { fromBase64url } from './jose.ts';

export type VaultBinding = {accountId: string; credentialRef: string; provider: string; revision: string};

const ivBytes = 12;
const aad = (binding: VaultBinding) => new TextEncoder().encode(JSON.stringify(['weave-credential-v1', binding.accountId,
    binding.credentialRef, binding.provider, binding.revision]));

async function key(secret: string | undefined): Promise<CryptoKey> {
    const raw = secret === undefined ? null : fromBase64url(secret);
    if (!raw || raw.byteLength !== 32) throw Error('EXTERNAL_CREDENTIAL_KEY must be 32 bytes of base64url.');
    return crypto.subtle.importKey('raw', raw, 'AES-GCM', false, ['encrypt', 'decrypt']);
}

/** The IV followed by the ciphertext and tag. */
export async function sealSecret(keyText: string | undefined, binding: VaultBinding, plaintext: string): Promise<Uint8Array> {
    return seal(keyText, aad(binding), plaintext);
}

/** An app webhook's signing secret, bound to its webhook and owner grant (push webhook W1). */
const webhookAad = (webhookId: string, grantId: string) => new TextEncoder().encode(JSON.stringify(['weave-webhook-v1', webhookId, grantId]));
export const sealWebhookSecret = (keyText: string | undefined, webhookId: string, grantId: string, plaintext: string) =>
    seal(keyText, webhookAad(webhookId, grantId), plaintext);
export const openWebhookSecret = (keyText: string | undefined, webhookId: string, grantId: string, sealed: Uint8Array) =>
    open(keyText, webhookAad(webhookId, grantId), sealed);

async function seal(keyText: string | undefined, additionalData: Uint8Array<ArrayBuffer>, plaintext: string): Promise<Uint8Array> {
    const iv = crypto.getRandomValues(new Uint8Array(ivBytes));
    const sealed = new Uint8Array(await crypto.subtle.encrypt({name: 'AES-GCM', iv, additionalData},
        await key(keyText), new TextEncoder().encode(plaintext)));
    const out = new Uint8Array(ivBytes + sealed.byteLength);
    out.set(iv); out.set(sealed, ivBytes);
    return out;
}

/** Throws when the key, the row binding or the bytes do not match. */
export async function openSecret(keyText: string | undefined, binding: VaultBinding, sealed: Uint8Array): Promise<string> {
    return open(keyText, aad(binding), sealed);
}

async function open(keyText: string | undefined, additionalData: Uint8Array<ArrayBuffer>, sealed: Uint8Array): Promise<string> {
    if (sealed.byteLength <= ivBytes) throw Error('Sealed credential is truncated.');
    const plain = await crypto.subtle.decrypt({name: 'AES-GCM', iv: sealed.slice(0, ivBytes), additionalData},
        await key(keyText), sealed.slice(ivBytes));
    return new TextDecoder('utf-8', {fatal: true}).decode(plain);
}
