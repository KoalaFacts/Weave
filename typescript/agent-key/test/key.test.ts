import assert from 'node:assert/strict';
import { access, chmod, mkdtemp, readFile, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { open, unlink } from 'node:fs/promises';
import { createKey, KeyFileError, loadKey } from '../src/index.ts';
import { writeNewKeyFile } from '../src/key.ts';

const posixOnly = process.platform === 'win32' ? 'POSIX file modes are not enforced on Windows; this runs on Linux/macOS CI' : false;

async function scratch(): Promise<string> { return mkdtemp(join(tmpdir(), 'weave-agent-key-')); }

test('createKey_NewFile_WritesPrivateJwkAndReturnsThumbprint', async () => {
    const dir = await scratch();
    try {
        const path = join(dir, 'agent.jwk');
        const {kid} = await createKey(path);
        const jwk = JSON.parse(await readFile(path, 'utf8'));
        assert.deepEqual(Object.keys(jwk).sort(), ['crv', 'd', 'kty', 'x']);
        assert.equal(jwk.kty, 'OKP');
        assert.equal(jwk.crv, 'Ed25519');
        const key = await loadKey(path);
        assert.equal(key.kid, kid);
        assert.match(kid, /^[A-Za-z0-9_-]{43}$/);
        assert.deepEqual(key.publicJwk, {kty: 'OKP', crv: 'Ed25519', x: jwk.x});
        assert.ok(!JSON.stringify(key).includes(jwk.d));
    } finally { await rm(dir, {recursive: true, force: true}); }
});

test('createKey_ExistingFile_Refused', async () => {
    const dir = await scratch();
    try {
        const path = join(dir, 'agent.jwk');
        await writeFile(path, 'keep me');
        await assert.rejects(createKey(path), KeyFileError);
        assert.equal(await readFile(path, 'utf8'), 'keep me');
        const {kid} = await createKey(join(dir, 'second.jwk'));
        await assert.rejects(createKey(join(dir, 'second.jwk')), KeyFileError);
        assert.equal((await loadKey(join(dir, 'second.jwk'))).kid, kid);
    } finally { await rm(dir, {recursive: true, force: true}); }
});

test('createKey_Posix_FileModeIsOwnerOnly', {skip: posixOnly}, async () => {
    const dir = await scratch();
    try {
        const path = join(dir, 'agent.jwk');
        await createKey(path);
        assert.equal((await stat(path)).mode & 0o777, 0o600);
    } finally { await rm(dir, {recursive: true, force: true}); }
});

test('loadKey_WorldReadable_Refused', {skip: posixOnly}, async () => {
    const dir = await scratch();
    try {
        const path = join(dir, 'agent.jwk');
        await createKey(path);
        for (const mode of [0o644, 0o640, 0o604, 0o620]) {
            await chmod(path, mode);
            await assert.rejects(loadKey(path), (error: unknown) => error instanceof KeyFileError && /group or others/.test(error.message), mode.toString(8));
        }
        await chmod(path, 0o600);
        await loadKey(path);
    } finally { await rm(dir, {recursive: true, force: true}); }
});

test('loadKey_NotAPrivateEd25519Jwk_RefusedWithoutEchoingContent', async () => {
    const dir = await scratch();
    try {
        const source = join(dir, 'agent.jwk');
        await createKey(source);
        const jwk = JSON.parse(await readFile(source, 'utf8'));
        const otherPath = join(dir, 'other.jwk');
        await createKey(otherPath);
        const other = JSON.parse(await readFile(otherPath, 'utf8'));
        const cases: [string, string][] = [
            ['public.jwk', JSON.stringify({kty: 'OKP', crv: 'Ed25519', x: jwk.x})],
            ['extra.jwk', JSON.stringify({...jwk, kid: 'x'})],
            ['mismatch.jwk', JSON.stringify({...jwk, x: other.x})],
            ['garbage.jwk', 'not json ' + jwk.d],
        ];
        for (const [name, content] of cases) {
            const path = join(dir, name);
            await writeFile(path, content, {mode: 0o600});
            await assert.rejects(loadKey(path), (error: unknown) => error instanceof KeyFileError && !error.message.includes(jwk.d), name);
        }
    } finally { await rm(dir, {recursive: true, force: true}); }
});

test('createKey_WriteOrCloseFails_RemovesPartialFileAndRethrowsOriginalError', async () => {
    const dir = await scratch();
    try {
        const failing = (failWrite: boolean, failClose: boolean) => (async (...args: Parameters<typeof open>) => {
            const handle = await open(...args);
            return new Proxy(handle, {get(target, property) {
                if (property === 'writeFile' && failWrite) return async () => { await target.writeFile('partial'); throw new Error('disk full'); };
                if (property === 'close' && failClose) return async () => { await target.close(); throw new Error('close failed'); };
                const value = Reflect.get(target, property, target);
                return typeof value === 'function' ? value.bind(target) : value;
            }});
        }) as typeof open;
        const cases: [string, boolean, boolean, boolean, string][] = [
            ['write.jwk', true, false, false, 'disk full'],
            ['write-and-close.jwk', true, true, false, 'disk full'],
            ['close.jwk', false, true, false, 'close failed'],
            ['unlink-fails.jwk', true, false, true, 'disk full'],
        ];
        for (const [name, failWrite, failClose, failUnlink, message] of cases) {
            const path = join(dir, name);
            const removal = failUnlink ? (async () => { throw new Error('unlink failed'); }) as typeof unlink : unlink;
            await assert.rejects(writeNewKeyFile(path, '{}', {open: failing(failWrite, failClose), unlink: removal}), {message}, name);
            if (failUnlink) await unlink(path);
            else await assert.rejects(access(path), {code: 'ENOENT'}, name);
        }
    } finally { await rm(dir, {recursive: true, force: true}); }
});
