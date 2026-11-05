import assert from 'node:assert/strict';
import { test } from 'node:test';
import { requireCiSuccess } from '../scripts/require-ci-success.ts';

const sha = 'a'.repeat(40), other = 'b'.repeat(40);
const run = (fields: Record<string, unknown>) => ({head_sha: sha, head_branch: 'main', event: 'push', status: 'completed', conclusion: 'success', html_url: 'https://github.com/o/r/actions/runs/1', ...fields});
/** Answers each lookup with the next page of runs and records what was asked. */
function gate(pages: unknown[][], extra: Record<string, unknown> = {}) {
    const asked: {url: string; auth: string | null; redirect?: string}[] = [], slept: number[] = [];
    let clock = 0;
    return {asked, slept, options: {workflow: 'worker-browser.yml' as const, repository: 'KoalaFacts/Weave', sha, token: 'ghs_synthetic', log: () => {}, now: () => clock,
        sleep: async (ms: number) => { slept.push(ms); clock += ms; },
        fetchImpl: (async (url: string, init?: RequestInit) => {
            asked.push({url: String(url), auth: new Headers(init?.headers).get('Authorization'), redirect: init?.redirect});
            return Response.json({workflow_runs: pages[Math.min(asked.length - 1, pages.length - 1)]});
        }) as typeof fetch, ...extra}};
}

test('requireCiSuccess_PassedMainPushRun_Releases', async () => {
    const g = gate([[run({})]]);
    assert.equal(await requireCiSuccess(g.options), 'https://github.com/o/r/actions/runs/1');
    const url = new URL(g.asked[0].url);
    assert.equal(url.pathname, '/repos/KoalaFacts/Weave/actions/workflows/worker-browser.yml/runs');
    assert.deepEqual([url.searchParams.get('head_sha'), url.searchParams.get('branch')], [sha, 'main']);
    assert.deepEqual([g.asked[0].auth, g.asked[0].redirect], ['Bearer ghs_synthetic', 'error']);
});

test('requireCiSuccess_RunInProgress_WaitsForItsResult', async () => {
    const g = gate([[run({status: 'in_progress', conclusion: null})], [run({status: 'queued', conclusion: null})], [run({})]]);
    await requireCiSuccess(g.options);
    assert.equal(g.asked.length, 3);
    assert.deepEqual(g.slept, [30_000, 30_000]);
});

for (const [name, runs, message] of [
    ['failed', [run({conclusion: 'failure'})], /did not pass.*failure/],
    ['cancelled', [run({conclusion: 'cancelled'})], /did not pass.*cancelled/],
    ['missing', [], /No Worker verification run on main/],
    ['another commit', [run({head_sha: other})], /No Worker verification run on main/],
    ['a pull request run', [run({event: 'pull_request', head_branch: 'feature'})], /No Worker verification run on main/],
    ['a manual run', [run({event: 'workflow_dispatch'})], /No Worker verification run on main/],
] as const) test(`requireCiSuccess_${name}_RefusesRelease`, async () => {
    const g = gate([runs as unknown[]]);
    await assert.rejects(requireCiSuccess(g.options), message);
    assert.deepEqual(g.slept, [], 'a finished result is not waited on');
});

test('requireCiSuccess_StillRunningAtDeadline_RefusesRelease', async () => {
    const g = gate([[run({status: 'in_progress', conclusion: null})]], {timeoutMs: 60_000});
    await assert.rejects(requireCiSuccess(g.options), /still running/);
    assert.equal(g.slept.length, 2);
});

test('requireCiSuccess_LookupFailsOrMalformed_RefusesRelease', async () => {
    const base = gate([[run({})]]).options;
    await assert.rejects(requireCiSuccess({...base, fetchImpl: (async () => new Response('no', {status: 403})) as typeof fetch}), /HTTP 403/);
    await assert.rejects(requireCiSuccess({...base, fetchImpl: (async () => Response.json({})) as typeof fetch}), /invalid result/);
    await assert.rejects(requireCiSuccess({...base, fetchImpl: (async () => { throw Error('offline'); }) as typeof fetch}), /lookup failed/);
    await assert.rejects(requireCiSuccess({...base, sha: 'HEAD'}), /required/);
    await assert.rejects(requireCiSuccess({...base, token: ''}), /required/);
    await assert.rejects(requireCiSuccess({...base, workflow: 'unknown.yml' as never}), /required/, 'only a known verification workflow can gate a release');
});

test('requireCiSuccess_FullCI_RequiresPassedExactMainPushWithCoverage', async () => {
    const valid = gate([[run({})]], {workflow: 'ci.yml'});
    assert.equal(await requireCiSuccess(valid.options), 'https://github.com/o/r/actions/runs/1');
    assert.equal(new URL(valid.asked[0].url).pathname, '/repos/KoalaFacts/Weave/actions/workflows/ci.yml/runs');
    const running = gate([[run({status: 'in_progress', conclusion: null})], [run({})]], {workflow: 'ci.yml'});
    await requireCiSuccess(running.options);
    assert.deepEqual(running.slept, [30_000]);
    for (const source of [[run({conclusion: 'failure'})], [run({event: 'pull_request'})], [run({event: 'workflow_dispatch'})]]) {
        await assert.rejects(requireCiSuccess(gate([source], {workflow: 'ci.yml'}).options),
            /Complete CI and coverage (?:did not pass|run on main)/);
    }
});

test('requireCiSuccess_OAuthVerification_AcceptsAPushOrManualMainRunOnly', async () => {
    for (const event of ['push', 'workflow_dispatch']) {
        const g = gate([[run({event})]], {workflow: 'oauth-auth-worker.yml'});
        assert.equal(await requireCiSuccess(g.options), 'https://github.com/o/r/actions/runs/1', event);
        assert.equal(new URL(g.asked[0].url).pathname, '/repos/KoalaFacts/Weave/actions/workflows/oauth-auth-worker.yml/runs');
    }
    await assert.rejects(requireCiSuccess(gate([[run({event: 'pull_request'})]], {workflow: 'oauth-auth-worker.yml'}).options), /No OAuth verification run on main.*run it manually on main/);
    await assert.rejects(requireCiSuccess(gate([[run({event: 'workflow_dispatch', conclusion: 'failure'})]], {workflow: 'oauth-auth-worker.yml'}).options), /OAuth verification did not pass/);
});
