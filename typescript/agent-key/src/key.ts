import { constants } from 'node:fs';
import { open, unlink } from 'node:fs/promises';

export type PublicJwk = {kty: 'OKP'; crv: 'Ed25519'; x: string};
/** A loaded agent key. The private key is a non-extractable CryptoKey held only inside `sign`. */
export type AgentKey = {readonly kid: string; readonly publicJwk: PublicJwk; sign(data: Uint8Array<ArrayBuffer>): Promise<Uint8Array<ArrayBuffer>>};

/** A key file that cannot be created or used; the message never contains key material. */
export class KeyFileError extends Error {
    override name = 'KeyFileError';
}

const maxKeyFileBytes = 4096;
const privateMembers = ['crv', 'd', 'kty', 'x'];
const ed25519 = {name: 'Ed25519'};
const encoder = new TextEncoder();

export const base64url = (bytes: Uint8Array): string => Buffer.from(bytes).toString('base64url');
const encodeJson = (value: unknown): string => base64url(encoder.encode(JSON.stringify(value)));

/** RFC 7638 thumbprint over the required Ed25519 members in lexicographic order. */
export async function thumbprint(jwk: PublicJwk): Promise<string> {
    const canonical = JSON.stringify({crv: jwk.crv, kty: jwk.kty, x: jwk.x});
    return base64url(new Uint8Array(await crypto.subtle.digest('SHA-256', encoder.encode(canonical))));
}

/** Generates an Ed25519 key and writes its private JWK to a new owner-only file; an existing file is never overwritten. */
export async function createKey(path: string): Promise<{kid: string}> {
    const pair = await crypto.subtle.generateKey(ed25519, true, ['sign', 'verify']) as CryptoKeyPair;
    const {kty, crv, x, d} = await crypto.subtle.exportKey('jwk', pair.privateKey);
    if (kty !== 'OKP' || crv !== 'Ed25519' || typeof x !== 'string' || typeof d !== 'string') throw new KeyFileError('The runtime did not produce an Ed25519 key.');
    await writeNewKeyFile(path, JSON.stringify({kty, crv, x, d}) + '\n');
    return {kid: await thumbprint({kty, crv, x})};
}

/** File operations used by `writeNewKeyFile`; replaceable only to inject failures in tests. */
export type KeyFileSystem = {open: typeof open; unlink: typeof unlink};

/** Creates `path` exclusively with mode 0600 and writes `text`; on any failure the partial file is removed and the original error rethrown. */
export async function writeNewKeyFile(path: string, text: string, fs: KeyFileSystem = {open, unlink}): Promise<void> {
    let file;
    try {
        file = await fs.open(path, 'wx', 0o600);
    } catch (error) {
        if ((error as NodeJS.ErrnoException).code === 'EEXIST') throw new KeyFileError(`Key file ${path} already exists; it is never overwritten.`);
        throw error;
    }
    try {
        try {
            // The creation mode is reduced by the umask but never widened; chmod pins it regardless.
            if (process.platform !== 'win32') await file.chmod(0o600);
            await file.writeFile(text, 'utf8');
            await file.sync();
        } catch (error) {
            // Clean-up must not mask the write failure; a close error here adds nothing.
            await file.close().catch(() => undefined);
            throw error;
        }
        await file.close();
    } catch (error) {
        // Only this call created the file. An unlink failure must not replace the original error.
        await fs.unlink(path).catch(() => undefined);
        throw error;
    }
}

function refuse(path: string): never {
    throw new KeyFileError(`Key file ${path} does not hold an Ed25519 private JWK.`);
}

/** Reads a private JWK written by `createKey`; only a regular file is read, and on POSIX one accessible by group or others is refused. */
export async function loadKey(path: string): Promise<AgentKey> {
    // Non-blocking, so a FIFO or device (directly or behind a symlink) is opened at once and refused by the regular-file check.
    const file = await open(path, constants.O_RDONLY | (constants.O_NONBLOCK ?? 0));
    let text: string;
    try {
        const info = await file.stat();
        if (!info.isFile() || info.size > maxKeyFileBytes) refuse(path);
        // Windows reports synthetic modes; its ACLs are not inspected here.
        if (process.platform !== 'win32' && (info.mode & 0o077) !== 0)
            throw new KeyFileError(`Key file ${path} is accessible by group or others (mode ${(info.mode & 0o777).toString(8)}); restrict it with chmod 600.`);
        text = await file.readFile('utf8');
    } finally { await file.close(); }
    let value: unknown;
    try { value = JSON.parse(text); } catch { refuse(path); }
    if (typeof value !== 'object' || value === null || Array.isArray(value)) refuse(path);
    const jwk = value as Record<string, unknown>;
    if (Object.keys(jwk).sort().join() !== privateMembers.join() || jwk.kty !== 'OKP' || jwk.crv !== 'Ed25519' || typeof jwk.x !== 'string' || typeof jwk.d !== 'string') refuse(path);
    const publicJwk: PublicJwk = {kty: 'OKP', crv: 'Ed25519', x: jwk.x};
    let privateKey: CryptoKey, publicKey: CryptoKey;
    try {
        privateKey = await crypto.subtle.importKey('jwk', {kty: 'OKP', crv: 'Ed25519', x: jwk.x, d: jwk.d}, ed25519, false, ['sign']);
        publicKey = await crypto.subtle.importKey('jwk', publicJwk, ed25519, false, ['verify']);
    } catch { refuse(path); }
    // A file whose x does not belong to d would sign proofs the relay rejects for a key it never registered.
    const probe = encoder.encode('weave-agent-key');
    if (!await crypto.subtle.verify(ed25519, publicKey, await crypto.subtle.sign(ed25519, privateKey, probe), probe)) refuse(path);
    const kid = await thumbprint(publicJwk);
    return Object.freeze({kid, publicJwk: Object.freeze(publicJwk), sign: async (data: Uint8Array<ArrayBuffer>) => new Uint8Array(await crypto.subtle.sign(ed25519, privateKey, data))});
}

/** A compact JWS (RFC 7515) signed with EdDSA over `base64url(header).base64url(payload)`. */
export async function signJws(key: AgentKey, header: Record<string, unknown>, payload: Record<string, unknown>): Promise<string> {
    const signingInput = `${encodeJson({alg: 'EdDSA', ...header})}.${encodeJson(payload)}`;
    return `${signingInput}.${base64url(await key.sign(encoder.encode(signingInput)))}`;
}
