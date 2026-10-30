import assert from 'node:assert/strict';
import { test } from 'node:test';
import { execFile } from 'node:child_process';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';

test('failed fixture initialization preserves its error and releases the actual runtime', async () => {
    const cwd = await mkdtemp(join(tmpdir(), 'weave-failed-fixture-'));
    try {
        await writeFile(join(cwd, 'worker.js'), 'export default { fetch() { return new Response("lifecycle fixture"); } };');
        const script = `
            import { start } from ${JSON.stringify(new URL('./harness.ts', import.meta.url).href)};
            try {
                const h = await start({ legacy: true, scriptPath: 'worker.js' });
                await h.close();
                throw new Error('Missing fixture unexpectedly initialized.');
            } catch (error) {
                if (error.code !== 'ENOENT' || !error.path.endsWith('legacy-mailbox.sql')) throw error;
                console.log('Original fixture error retained.');
            }
        `;
        await writeFile(join(cwd, 'probe.ts'), script);
        const result = await promisify(execFile)(process.execPath,
            ['--require', fileURLToPath(new URL('./local-miniflare.ts', import.meta.url)), join(cwd, 'probe.ts')],
            { cwd, timeout: 60000, maxBuffer: 1024 * 1024 });
        assert.equal(result.stdout.trim(), 'Original fixture error retained.');
        // A leaked workerd/proxy thread keeps the child alive and fails the
        // deadline above. Its successful natural exit proves lifecycle cleanup.
    } finally { await rm(cwd, { recursive: true, force: true }); }
});
