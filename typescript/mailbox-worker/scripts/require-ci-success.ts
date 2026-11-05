/**
 * Release gate: a verification workflow must have passed on main for this exact commit. The deploy relies on that run
 * instead of repeating the same suite. A run still in progress is awaited up to a deadline; a failed, cancelled or
 * missing run stops the release.
 */
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

/** The workflows a release can require. OAuth verification skips commits outside its paths, so a manual run on main counts. */
export const verifications = {
    'ci.yml': {label: 'Complete CI and coverage', events: ['push'], missing: 'run full CI on main before releasing'},
    'worker-browser.yml': {label: 'Worker verification', events: ['push'], missing: 'deploy a commit whose main CI has run'},
    'oauth-auth-worker.yml': {label: 'OAuth verification', events: ['push', 'workflow_dispatch'],
        missing: 'it skips commits outside its paths, so run it manually on main first'}
} as const;
export type Verification = keyof typeof verifications;
export const defaultTimeoutMs = 25 * 60_000, defaultPollMs = 30_000;

type Run = {head_sha?: unknown; head_branch?: unknown; event?: unknown; status?: unknown; conclusion?: unknown; html_url?: unknown};
export type CiGate = {workflow: Verification; repository: string; sha: string; token: string; fetchImpl?: typeof fetch; sleep?: (ms: number) => Promise<void>;
    now?: () => number; timeoutMs?: number; pollMs?: number; log?: (line: string) => void};

/** Resolves with the passing run's URL; throws with the reason the release must not proceed. */
export async function requireCiSuccess({workflow, repository, sha, token, fetchImpl = fetch, sleep = ms => new Promise(resolve => setTimeout(resolve, ms)),
    now = Date.now, timeoutMs = defaultTimeoutMs, pollMs = defaultPollMs, log = console.log}: CiGate): Promise<string> {
    if (!Object.hasOwn(verifications, workflow) || !/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository) || !/^[0-9a-f]{40}$/.test(sha) || !token)
        throw Error('Workflow, repository, commit SHA and token are required.');
    const {label, events, missing} = verifications[workflow];
    const url = `https://api.github.com/repos/${repository}/actions/workflows/${workflow}/runs?` + new URLSearchParams({head_sha: sha, branch: 'main', per_page: '20'});
    const deadline = now() + timeoutMs;
    while (true) {
        let response: Response;
        try { response = await fetchImpl(url, {headers: {Authorization: 'Bearer ' + token, Accept: 'application/vnd.github+json'}, redirect: 'error'}); }
        catch { throw Error(`${label} lookup failed; no release attempted.`); }
        if (!response.ok) throw Error(`${label} lookup failed (HTTP ${response.status}); no release attempted.`);
        const body = await response.json().catch(() => null) as {workflow_runs?: Run[]} | null;
        if (!body || !Array.isArray(body.workflow_runs)) throw Error(`${label} lookup returned an invalid result; no release attempted.`);
        // Only main-branch runs of this exact commit, started by an accepted event, count.
        const runs = body.workflow_runs.filter(run => run.head_sha === sha && run.head_branch === 'main' && (events as readonly unknown[]).includes(run.event));
        const passed = runs.find(run => run.status === 'completed' && run.conclusion === 'success');
        if (passed) return typeof passed.html_url === 'string' ? passed.html_url : '';
        const pending = runs.some(run => run.status !== 'completed');
        if (!pending) throw Error(runs.length === 0
            ? `No ${label} run on main for this commit; ${missing}.`
            : `${label} did not pass on main for this commit (` + runs.map(run => String(run.conclusion)).join(', ') + '); no release attempted.');
        if (now() + pollMs > deadline) throw Error(`${label} for this commit is still running; retry once it has finished.`);
        log(`${label} for this commit is still running; waiting.`);
        await sleep(pollMs);
    }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
    const workflow = process.argv[2] as Verification;
    const url = await requireCiSuccess({workflow, repository: process.env.GITHUB_REPOSITORY ?? '', sha: process.env.GITHUB_SHA ?? '', token: process.env.GITHUB_TOKEN ?? ''});
    console.log(`${verifications[workflow].label} passed on main for this commit: ` + url);
}
