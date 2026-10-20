import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync, readFileSync, mkdtempSync, mkdirSync, writeFileSync, cpSync, rmSync, symlinkSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const root = fileURLToPath(new URL('../../', import.meta.url));
const readJson = (path: string) => JSON.parse(readFileSync(path, 'utf8'));
const catalog = '.agents/plugins/marketplace.json';
const manifest = 'plugins/weave/.codex-plugin/plugin.json';
const apps = 'plugins/weave/.app.json';
const syntheticApp = 'asdk_app_' + 'a'.repeat(32); // Synthetic fixture, never installed.
const otherApp = 'asdk_app_' + 'b'.repeat(32);

function fixture(t: { after: (fn: () => void) => void }) {
    const dir = mkdtempSync(join(tmpdir(), 'weave-marketplace-'));
    t.after(() => rmSync(dir, { recursive: true, force: true }));
    for (const path of ['.agents', 'plugins']) cpSync(join(root, path), join(dir, path), { recursive: true });
    // Tests exercise the setup-only state even after an operator binds the real app.
    writeFileSync(join(dir, apps), '{"apps":{}}\n');
    return dir;
}
function change(dir: string, path: string, mutate: (value: any) => void) {
    const value = readJson(join(dir, path));
    mutate(value);
    writeFileSync(join(dir, path), JSON.stringify(value, null, 2) + '\n');
}
const api = () => import('./marketplace.ts');

test('repository exposes a GitHub-importable Weave marketplace', () => {
    assert.ok(existsSync(join(root, catalog)), 'Missing GitHub marketplace catalog');
    const value = readJson(join(root, catalog));
    assert.equal(value.name, 'weave-plugins');
    assert.equal(value.plugins.length, 1);
    assert.equal(value.plugins[0].source.path, './plugins/weave');
});
test('setup preview is structurally valid but is not a connected release', async t => {
    const dir = fixture(t), { inspect } = await api();
    const result = inspect(dir);
    assert.deepEqual(result.errors, []);
    assert.equal(result.status, 'setup-only');
    assert.ok(inspect(dir, true).errors.includes('A registered Weave app is required.'));
});
test('packaged skills are self-contained after copying just the plugin', () => {
    for (const path of ['skills/setup/SKILL.md', 'skills/weave/SKILL.md']) {
        const text = readFileSync(join(root, 'plugins/weave', path), 'utf8');
        assert.match(text, /^---\nname: /);
        assert.doesNotMatch(text, /\.\.\/\.\.\/\.\.\/|\$REPO_ROOT|\$\{PLUGIN_ROOT\}/);
        assert.match(text, /not connected|no.*tools|tools.*unavailable/i);
    }
});
test('binder writes the exact app ID and is idempotent without changing other files', async t => {
    const dir = fixture(t), { bind, inspect } = await api();
    const before = readFileSync(join(dir, manifest), 'utf8');
    assert.equal(bind(dir, syntheticApp), 'bound');
    assert.deepEqual(readJson(join(dir, apps)), { apps: { weave: { id: syntheticApp } } });
    assert.equal(bind(dir, syntheticApp), 'unchanged');
    assert.equal(readFileSync(join(dir, manifest), 'utf8'), before);
    assert.equal(inspect(dir, true).status, 'app-reference-configured');
    assert.deepEqual(inspect(dir, true).errors, []);
});
test('binder refuses to replace a different existing connection', async t => {
    const dir = fixture(t), { bind } = await api();
    bind(dir, syntheticApp);
    assert.throws(() => bind(dir, otherApp), /already bound/);
    assert.equal(readJson(join(dir, apps)).apps.weave.id, syntheticApp);
});
for (const invalid of ['plugin_asdk_app_' + 'a'.repeat(32), 'asdk_app_REPLACE_ME', 'asdk_app_' + '0'.repeat(32), 'https://example.com/mcp', 'sk-proj-sensitive-test', '', ' ' + syntheticApp]) {
    test('binder rejects unsupported or placeholder IDs without writing: ' + invalid.slice(0, 18), async t => {
        const dir = fixture(t), { bind } = await api();
        const before = readFileSync(join(dir, apps), 'utf8');
        assert.throws(() => bind(dir, invalid), /registered app ID/);
        assert.equal(readFileSync(join(dir, apps), 'utf8'), before);
    });
}
for (const file of ['mcp.json', '.mcp.json', 'hooks/hooks.json']) {
    test('web package rejects a bundled runtime: ' + file, async t => {
        const dir = fixture(t), { inspect } = await api();
        mkdirSync(resolve(dir, 'plugins/weave', file, '..'), { recursive: true });
        writeFileSync(join(dir, 'plugins/weave', file), '{}');
        assert.ok(inspect(dir).errors.length > 0);
    });
}
for (const path of ['./plugins/../outside', '/tmp/elsewhere', '../outside', './plugins/weave/']) {
    test('catalog cannot silently redirect Weave outside its reviewed package: ' + path, async t => {
        const dir = fixture(t), { inspect } = await api();
        change(dir, catalog, value => value.plugins[0].source.path = path);
        assert.ok(inspect(dir).errors.length > 0);
    });
}
test('catalog rejects extra plugins and unexpected installation policy', async t => {
    const dir = fixture(t), { inspect } = await api();
    change(dir, catalog, value => value.plugins.push(value.plugins[0]));
    assert.ok(inspect(dir).errors.length > 0);
});
test('catalog requires installation to be explicit and authentication on install', async t => {
    const dir = fixture(t), { inspect } = await api();
    change(dir, catalog, value => value.plugins[0].policy.installation = 'INSTALLED_BY_DEFAULT');
    assert.ok(inspect(dir).errors.length > 0);
});
test('manifest cannot inject a local process or installation hook', async t => {
    const dir = fixture(t), { inspect } = await api();
    change(dir, manifest, value => value.mcpServers = './.mcp.json');
    assert.ok(inspect(dir).errors.length > 0);
});
test('extra app dependencies cannot be smuggled into the package', async t => {
    const dir = fixture(t), { bind, inspect } = await api();
    change(dir, apps, value => value.apps.other = { id: otherApp });
    assert.ok(inspect(dir).errors.length > 0);
    assert.throws(() => bind(dir, syntheticApp), /invalid/i);
});
test('secrets or extra properties cannot be added to an app mapping', async t => {
    const dir = fixture(t), { inspect } = await api();
    change(dir, apps, value => value.apps.weave = { id: syntheticApp, token: 'fixture-do-not-use' });
    assert.ok(inspect(dir).errors.length > 0);
});
test('malformed JSON fails with a bounded error that does not echo file content', async t => {
    const dir = fixture(t), { inspect } = await api();
    writeFileSync(join(dir, apps), '{secret: "fixture-do-not-echo"}');
    const result = inspect(dir);
    assert.ok(result.errors.length > 0);
    assert.doesNotMatch(JSON.stringify(result), /fixture-do-not-echo/);
});
test('missing required skill prevents binding', async t => {
    const dir = fixture(t), { bind, inspect } = await api();
    rmSync(join(dir, 'plugins/weave/skills/setup/SKILL.md'));
    assert.ok(inspect(dir).errors.length > 0);
    assert.throws(() => bind(dir, syntheticApp), /invalid/i);
});
test('symlinked app files are rejected without modifying their targets', async t => {
    const dir = fixture(t), { bind, inspect } = await api();
    const target = join(dir, 'outside.json');
    writeFileSync(target, '{"apps":{}}\n');
    rmSync(join(dir, apps)); symlinkSync(target, join(dir, apps));
    assert.ok(inspect(dir).errors.length > 0);
    assert.throws(() => bind(dir, syntheticApp), /invalid/i);
    assert.equal(readFileSync(target, 'utf8'), '{"apps":{}}\n');
});
test('strict CLI exits nonzero for an unbound package without modifying it', t => {
    const dir = fixture(t);
    cpSync(join(root, 'scripts/marketplace'), join(dir, 'scripts/marketplace'), { recursive: true });
    const before = readFileSync(join(dir, apps), 'utf8');
    const result = spawnSync(process.execPath, ['--experimental-strip-types', join(dir, 'scripts/marketplace/marketplace.ts'), 'check', '--require-app'], { encoding: 'utf8' });
    assert.equal(result.status, 1);
    assert.match(result.stderr, /registered Weave app/);
    assert.equal(readFileSync(join(dir, apps), 'utf8'), before);
});
test('CLI rejects unknown commands and does not echo credential-like arguments', () => {
    const result = spawnSync(process.execPath, ['--experimental-strip-types', join(root, 'scripts/marketplace/marketplace.ts'), 'bind', 'sk-proj-never-log-this'], { encoding: 'utf8' });
    assert.equal(result.status, 1);
    assert.doesNotMatch(result.stdout + result.stderr, /sk-proj-never-log-this/);
});

test('binding reports a safe error if the mapping becomes malformed after validation', async t => {
    const dir = fixture(t), { bind } = await api();
    const original = JSON.parse;
    let reads = 0;
    t.mock.method(JSON, 'parse', (text: string) => {
        if (++reads === 4) throw new SyntaxError('Untrusted fixture-do-not-echo JSON');
        return original(text);
    });
    assert.throws(() => bind(dir, syntheticApp), error => error instanceof Error && !error.message.includes('fixture-do-not-echo') && !(error instanceof SyntaxError));
    t.mock.restoreAll();
    assert.equal(readJson(join(dir, apps)).apps.weave, undefined);
    assert.equal(existsSync(join(dir, apps) + '.lock'), false);
});

test('existing Worker verification runs and typechecks the marketplace tools', () => {
    const pkg = readJson(join(root, 'hosts/Weave.Mailbox.Worker/package.json'));
    const config = readJson(join(root, 'hosts/Weave.Mailbox.Worker/tsconfig.tools.json'));
    assert.match(pkg.scripts.test, /^npm run test:marketplace && /);
    assert.equal(pkg.scripts['test:marketplace'], 'node ../../scripts/marketplace/marketplace.ts check && node --test ../../scripts/marketplace/marketplace.test.ts');
    assert.ok(config.include.includes('../../scripts/marketplace/*.ts'));
});
