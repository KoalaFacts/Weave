import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync, mkdtempSync, rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {spawnSync} from 'node:child_process';

const workflow = readFileSync(new URL('../../.github/workflows/inspect-weave-token-management.yml', import.meta.url), 'utf8');
test('read-only token inspection protects production credentials from PRs', () => {
    const [verify, live] = workflow.split('\n  inspect:\n');
    assert.ok(live); assert.doesNotMatch(verify, /secrets\./);
    assert.match(workflow, /permissions:\n  contents: read\n/);
    assert.doesNotMatch(workflow, /write-all|continue-on-error|pull_request_target|CLOUDFLARE_ACCESS_TRIAL_TOKEN/);
    assert.match(live, /needs: verify/);
    assert.match(live, /github\.ref == 'refs\/heads\/main'/);
    assert.match(live, /github\.event_name != 'pull_request'/);
    assert.match(live, /environment: production/);
    assert.match(live, /run: node experiments\/cloudflare-managed-oauth\/token-management\.ts/);
});

test('the actual workflow shell refuses stale main and accepts exact main in a real Git fixture', () => {
    const gate = workflow.match(/id: guard\n        shell: bash\n        run: \|\n([\s\S]*?)(?=      - name:)/)?.[1];
    assert.ok(gate);
    const script = gate.split('\n').map(line => line.replace(/^          /, '')).join('\n');
    const directory = mkdtempSync(join(tmpdir(), 'weave-token-main-'));
    function run(command: string, args: string[], cwd: string) {
        const result = spawnSync(command, args, {cwd, encoding: 'utf8', timeout: 10000});
        assert.equal(result.status, 0, result.stderr); return result.stdout.trim();
    }
    try {
        run('git', ['init', '-q', '-b', 'main', 'origin'], directory);
        const origin = join(directory, 'origin');
        run('git', ['config', 'user.name', 'Fixture'], origin);
        run('git', ['config', 'user.email', 'fixture@example.invalid'], origin);
        run('git', ['commit', '-q', '--allow-empty', '-m', 'first'], origin);
        const first = run('git', ['rev-parse', 'HEAD'], origin);
        run('git', ['clone', '-q', origin, 'checkout'], directory);
        run('git', ['commit', '-q', '--allow-empty', '-m', 'main advanced'], origin);
        const second = run('git', ['rev-parse', 'HEAD'], origin);
        const cwd = join(directory, 'checkout');
        const stale = spawnSync('bash', ['-c', script], {cwd, env: {...process.env, GITHUB_SHA: first}, timeout: 10000});
        assert.notEqual(stale.status, 0);
        run('git', ['checkout', '-q', second], cwd);
        const current = spawnSync('bash', ['-c', script], {cwd, env: {...process.env, GITHUB_SHA: second}, encoding: 'utf8', timeout: 10000});
        assert.equal(current.status, 0, current.stderr);
    } finally { rmSync(directory, {recursive: true, force: true}); }
});
