/**
 * Release gate: the Worker verification workflow (typecheck, the full Worker suite, audit and browser checks) must have
 * passed on main for this exact commit. The deploy relies on that run instead of repeating the same suite. A run still
 * in progress is awaited up to a deadline; a failed, cancelled or missing run stops the release.
 */
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const workflow = 'worker-browser.yml';
export const defaultTimeoutMs = 25 * 60_000, defaultPollMs = 30_000;

type Run = {head_sha?: unknown; head_branch?: unknown; event?: unknown; status?: unknown; conclusion?: unknown; html_url?: unknown};
export type CiGate = {repository: string; sha: string; token: string; fetchImpl?: typeof fetch; sleep?: (ms: number) => Promise<void>;
    now?: () => number; timeoutMs?: number; pollMs?: number; log?: (line: string) => void};

/** Resolves with the passing run's URL; throws with the reason the release must not proceed. */
export async function requireCiSuccess({repository, sha, token, fetchImpl = fetch, sleep = ms => new Promise(resolve => setTimeout(resolve, ms)),
    now = Date.now, timeoutMs = defaultTimeoutMs, pollMs = defaultPollMs, log = console.log}: CiGate): Promise<string> {
    if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository) || !/^[0-9a-f]{40}$/.test(sha) || !token) throw Error('Repository, commit SHA and token are required.');
    const url = `https://api.github.com/repos/${repository}/actions/workflows/${workflow}/runs?` + new URLSearchParams({head_sha: sha, branch: 'main', event: 'push', per_page: '20'});
    const deadline = now() + timeoutMs;
    while (true) {
        let response: Response;
        try { response = await fetchImpl(url, {headers: {Authorization: 'Bearer ' + token, Accept: 'application/vnd.github+json'}, redirect: 'error'}); }
        catch { throw Error('Worker verification lookup failed; no release attempted.'); }
        if (!response.ok) throw Error(`Worker verification lookup failed (HTTP ${response.status}); no release attempted.`);
        const body = await response.json().catch(() => null) as {workflow_runs?: Run[]} | null;
        if (!body || !Array.isArray(body.workflow_runs)) throw Error('Worker verification lookup returned an invalid result; no release attempted.');
        // Only main-branch push runs of this exact commit count.
        const runs = body.workflow_runs.filter(run => run.head_sha === sha && run.head_branch === 'main' && run.event === 'push');
        const passed = runs.find(run => run.status === 'completed' && run.conclusion === 'success');
        if (passed) return typeof passed.html_url === 'string' ? passed.html_url : '';
        const pending = runs.some(run => run.status !== 'completed');
        if (!pending) throw Error(runs.length === 0
            ? 'No Worker verification run on main for this commit; deploy a commit whose main CI has run.'
            : 'Worker verification did not pass on main for this commit (' + runs.map(run => String(run.conclusion)).join(', ') + '); no release attempted.');
        if (now() + pollMs > deadline) throw Error('Worker verification for this commit is still running; retry once it has finished.');
        log('Worker verification for this commit is still running; waiting.');
        await sleep(pollMs);
    }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
    const url = await requireCiSuccess({repository: process.env.GITHUB_REPOSITORY ?? '', sha: process.env.GITHUB_SHA ?? '', token: process.env.GITHUB_TOKEN ?? ''});
    console.log('Worker verification passed on main for this commit: ' + url);
}
