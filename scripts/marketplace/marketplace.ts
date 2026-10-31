import { lstatSync, readFileSync, readdirSync, openSync, closeSync, writeFileSync, renameSync, unlinkSync } from 'node:fs';
import { dirname, join, resolve, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';

type ObjectValue = Record<string, unknown>;
class BindingError extends Error {}
export type Inspection = { status: 'invalid' | 'setup-only' | 'app-reference-configured'; errors: string[] };
const catalogPath = '.agents/plugins/marketplace.json';
const pluginPath = 'plugins/weave';
const manifestPath = `${pluginPath}/.codex-plugin/plugin.json`;
const appPath = `${pluginPath}/.app.json`;
const files = new Set(['.codex-plugin/plugin.json', '.app.json', 'README.md', 'skills/setup/SKILL.md', 'skills/weave/SKILL.md', 'references/safety.md']);
const record = (value: unknown): value is ObjectValue => value !== null && typeof value === 'object' && !Array.isArray(value);
const keys = (value: ObjectValue, expected: string[]) => Object.keys(value).sort().join(',') === [...expected].sort().join(',');
const appId = (value: unknown): value is string => typeof value === 'string' && /^asdk_app_[0-9a-f]{32}$/.test(value) && value !== 'asdk_app_' + '0'.repeat(32);

function safeFile(root: string, path: string): string {
    const base = resolve(root), target = resolve(base, path);
    if (!target.startsWith(base + '/') && !target.startsWith(base + '\\')) throw Error('Invalid package path.');
    let current = base;
    if (lstatSync(current).isSymbolicLink()) throw Error('Symbolic links are not allowed.');
    for (const part of relative(base, target).split(/[\\/]/)) {
        current = join(current, part);
        if (lstatSync(current).isSymbolicLink()) throw Error('Symbolic links are not allowed.');
    }
    const stat = lstatSync(target);
    if (!stat.isFile() || stat.size > 65536) throw Error('Expected a bounded regular file.');
    return target;
}
function json(root: string, path: string): ObjectValue {
    const value: unknown = JSON.parse(readFileSync(safeFile(root, path), 'utf8'));
    if (!record(value)) throw Error('Expected a JSON object.');
    return value;
}
function packageFiles(root: string, directory = pluginPath): string[] {
    const entries = readdirSync(join(root, directory), { withFileTypes: true });
    return entries.flatMap(entry => {
        if (entry.isSymbolicLink()) throw Error('Symbolic links are not allowed.');
        const path = `${directory}/${entry.name}`;
        return entry.isDirectory() ? packageFiles(root, path) : [path.slice(pluginPath.length + 1)];
    });
}

/** Offline structural validation only. Never registers, links, or calls a service. */
export function inspect(root: string, requireApp = false): Inspection {
    const errors: string[] = [];
    let configured = false;
    const check = (condition: unknown, message: string) => { if (!condition) errors.push(message); };
    try {
        const catalog = json(root, catalogPath);
        check(catalog.name === 'weave-plugins', 'Unexpected marketplace name.');
        const entries = catalog.plugins;
        check(Array.isArray(entries) && entries.length === 1, 'The reviewed catalog must contain exactly one Weave plugin.');
        const entry = Array.isArray(entries) ? entries[0] : undefined;
        check(record(entry) && entry.name === 'weave' && entry.category === 'Productivity', 'Invalid Weave catalog entry.');
        check(record(entry) && record(entry.source) && keys(entry.source, ['source', 'path']) && entry.source.source === 'local' && entry.source.path === './plugins/weave', 'Unexpected plugin source; paths resolve from the repository root.');
        check(record(entry) && record(entry.policy) && keys(entry.policy, ['installation', 'authentication']) && entry.policy.installation === 'AVAILABLE' && entry.policy.authentication === 'ON_INSTALL', 'Installation must be explicit and authentication requested on install.');
        const manifest = json(root, manifestPath);
        check(manifest.name === 'weave' && typeof manifest.version === 'string' && /^\d+\.\d+\.\d+$/.test(manifest.version), 'Invalid plugin identity or version.');
        check(manifest.skills === './skills/' && manifest.apps === './.app.json', 'Invalid plugin component paths.');
        check(manifest.license === 'Apache-2.0', 'Preserve repository licensing.');
        check(!('mcpServers' in manifest) && !('hooks' in manifest) && !('extensions' in manifest), 'This web-targeted package must not bundle runtimes, hooks, or an unreviewed overlay.');
        for (const path of packageFiles(root)) check(files.has(path), 'Unreviewed package file; do not bundle MCP runtimes, hooks, or credentials.');
        for (const path of files) safeFile(root, `${pluginPath}/${path}`);
        const mapping = json(root, appPath);
        check(keys(mapping, ['apps']) && record(mapping.apps), 'Invalid app mapping.');
        if (record(mapping.apps)) {
            const names = Object.keys(mapping.apps);
            check(names.length === 0 || names.length === 1 && names[0] === 'weave', 'Unexpected app dependency.');
            if (names.length === 1 && names[0] === 'weave') {
                const app = mapping.apps.weave;
                configured = record(app) && keys(app, ['id']) && appId(app.id);
                check(configured, 'Use the exact registered app ID; do not use plugin IDs, placeholders, or credentials.');
            }
        }
    } catch { errors.push('Invalid or missing package file (JSON, size, path, or symbolic link).'); }
    if (requireApp && !configured) errors.push('A registered Weave app is required.');
    return { status: errors.length ? 'invalid' : configured ? 'app-reference-configured' : 'setup-only', errors };
}

/** Binds a known public app ID, not an OAuth token or agent key. */
export function bind(root: string, id: string): 'bound' | 'unchanged' {
    if (!appId(id)) throw new BindingError('Supply the exact registered app ID (asdk_app followed by its identifier), not a plugin ID or a secret.');
    if (inspect(root).errors.length) throw new BindingError('The marketplace package is invalid; no changes were made.');
    const path = safeFile(root, appPath), lock = `${path}.lock`;
    let fd: number;
    try { fd = openSync(lock, 'wx', 0o600); }
    catch (error) {
        if (record(error) && error.code === 'EEXIST') throw new BindingError('App binding is already locked; inspect any interrupted operation before retrying.');
        throw new BindingError('Cannot create the binding lock; check filesystem permissions.');
    }
    let temp: string | undefined;
    try {
        const before = readFileSync(safeFile(root, appPath), 'utf8');
        const current: unknown = JSON.parse(before);
        if (!record(current) || !keys(current, ['apps'])) throw new BindingError('The app mapping changed; no changes were made.');
        const entries = current.apps;
        if (!record(entries) || Object.keys(entries).some(key => key !== 'weave')) throw new BindingError('The app mapping changed; no changes were made.');
        if (Object.hasOwn(entries, 'weave')) {
            const existing = entries.weave;
            if (record(existing) && keys(existing, ['id']) && existing.id === id) return 'unchanged';
            throw new BindingError('Weave is already bound; review and remove the old mapping explicitly before replacing it.');
        }
        temp = join(dirname(path), `.app.json.tmp-${randomUUID()}`);
        writeFileSync(temp, JSON.stringify({ apps: { weave: { id } } }, null, 2) + '\n', { flag: 'wx', mode: 0o644 });
        if (readFileSync(safeFile(root, appPath), 'utf8') !== before) throw new BindingError('The app mapping changed; no changes were made.');
        renameSync(temp, path); temp = undefined;
        return 'bound';
    } catch (error) {
        if (error instanceof BindingError) throw error;
        throw new BindingError('App binding failed; inspect the mapping before retrying.');
    } finally {
        try { if (temp) unlinkSync(temp); }
        finally { try { closeSync(fd); } finally { unlinkSync(lock); } }
    }
}

function main(args: string[]): number {
    const root = fileURLToPath(new URL('../../', import.meta.url));
    if (args[0] === 'check' && (args.length === 1 || args.length === 2 && args[1] === '--require-app')) {
        const result = inspect(root, args.length === 2);
        if (result.errors.length) { for (const error of result.errors) console.error(error); return 1; }
        console.log(`${result.status}: offline package checks passed. Live connection, authorization and messaging remain unverified.`);
        return 0;
    }
    if (args[0] === 'bind' && args.length === 2) {
        try { console.log(`${bind(root, args[1])}: app reference saved. Verify the actual Weave connection before import.`); return 0; }
        catch (error) { console.error(error instanceof BindingError ? error.message : 'Binding failed; inspect the mapping before retrying.'); return 1; }
    }
    console.error('Usage: node scripts/marketplace/marketplace.ts check [--require-app] | bind <registered-app-id>');
    return 1;
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) process.exitCode = main(process.argv.slice(2));
