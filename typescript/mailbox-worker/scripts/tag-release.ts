/**
 * Release record: after a production deploy, tag the deployed commit `v<version>` and publish a GitHub Release whose
 * notes are that version's CHANGELOG section. It runs only for a commit whose newest production deployment from main
 * succeeded (Actions does not mark older deployments inactive, so this proves it deployed, not that it is still served), and never moves an existing tag: a tag that names another commit means the version was not bumped.
 */
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const semver = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z.-]+)?$/;

/** The body of `## <version>` up to the next `## ` heading, trimmed; throws when the section is missing or empty. */
export function releaseNotes(changelog: string, version: string): string {
    const lines = changelog.split(/\r?\n/);
    const start = lines.findIndex(line => line === `## ${version}` || line.startsWith(`## ${version} `));
    if (start < 0) throw Error(`CHANGELOG.md has no "## ${version}" section; no release recorded.`);
    const end = lines.findIndex((line, index) => index > start && line.startsWith('## '));
    const notes = lines.slice(start + 1, end < 0 ? undefined : end).join('\n').trim();
    if (!notes) throw Error(`CHANGELOG.md's "## ${version}" section is empty; no release recorded.`);
    return notes;
}

export type TagRelease = {repository: string; sha: string; token: string; version: string; notes: string;
    fetchImpl?: typeof fetch; log?: (line: string) => void};

/** Resolves with the release URL (created or already present for this commit); throws with the reason nothing was recorded. */
export async function tagRelease({repository, sha, token, version, notes, fetchImpl = fetch, log = console.log}: TagRelease): Promise<string> {
    if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository) || !/^[0-9a-f]{40}$/.test(sha) || !token || !semver.test(version) || !notes)
        throw Error('Repository, commit SHA, token, SemVer version and notes are required.');
    const api = `https://api.github.com/repos/${repository}`;
    const call = async (path: string, init: RequestInit = {}): Promise<{status: number; body: unknown}> => {
        let response: Response;
        try {
            response = await fetchImpl(api + path, {...init, redirect: 'error', headers: {Authorization: 'Bearer ' + token,
                Accept: 'application/vnd.github+json', ...(init.body ? {'Content-Type': 'application/json'} : {})}});
        } catch { throw Error(`GitHub request ${path} failed; no release recorded.`); }
        return {status: response.status, body: await response.json().catch(() => null)};
    };
    const ok = (result: {status: number}, path: string): void => {
        if (result.status < 200 || result.status > 299) throw Error(`GitHub request ${path} failed (HTTP ${result.status}); no release recorded.`);
    };

    // The commit must have deployed: its newest production deployment from main ended in success (statuses come newest first).
    const deploymentsPath = '/deployments?' + new URLSearchParams({environment: 'production', sha, per_page: '20'});
    const deployments = await call(deploymentsPath);
    ok(deployments, '/deployments');
    const own = Array.isArray(deployments.body)
        ? (deployments.body as {id?: unknown; sha?: unknown; ref?: unknown}[]).filter(d => d.sha === sha && d.ref === 'main' && Number.isSafeInteger(d.id))
        : [];
    if (own.length === 0) throw Error('No production deployment of this commit from main; deploy it before recording a release.');
    const newest = own.reduce((a, b) => (b.id as number) > (a.id as number) ? b : a);
    const statuses = await call(`/deployments/${newest.id as number}/statuses?per_page=1`);
    ok(statuses, '/deployments/statuses');
    const state = Array.isArray(statuses.body) ? (statuses.body[0] as {state?: unknown} | undefined)?.state : undefined;
    if (state !== 'success') throw Error(`This commit's newest production deployment is ${String(state ?? 'without status')}, not success; no release recorded.`);

    const tag = `v${version}`;
    const ref = await call(`/git/ref/tags/${tag}`);
    if (ref.status === 200) {
        let target = ref.body as {object?: {type?: unknown; sha?: unknown}} | null;
        if (target?.object?.type === 'tag') {
            const annotated = await call(`/git/tags/${String(target.object.sha)}`);
            ok(annotated, '/git/tags');
            target = annotated.body as typeof target;
        }
        if (target?.object?.sha !== sha)
            throw Error(`${tag} already tags commit ${String(target?.object?.sha)}; bump the version in CHANGELOG.md and the Workers' package.json.`);
        const existing = await call(`/releases/tags/${tag}`);
        if (existing.status === 200) {
            const url = (existing.body as {html_url?: unknown} | null)?.html_url;
            log(`${tag} is already released for this commit.`);
            return typeof url === 'string' ? url : '';
        }
        if (existing.status !== 404) ok(existing, '/releases/tags');
    } else if (ref.status !== 404) ok(ref, '/git/ref/tags');

    const created = await call('/releases', {method: 'POST',
        body: JSON.stringify({tag_name: tag, target_commitish: sha, name: tag, body: notes, draft: false, prerelease: version.includes('-'), make_latest: 'legacy'})});
    ok(created, '/releases');
    const url = (created.body as {html_url?: unknown} | null)?.html_url;
    return typeof url === 'string' ? url : '';
}

/** The Workers' shared version; throws unless both package.json files agree. */
export async function workersVersion(root: string): Promise<string> {
    const read = async (name: string) => (JSON.parse(await readFile(resolve(root, 'typescript', name, 'package.json'), 'utf8')) as {version?: unknown}).version;
    const [mailbox, auth] = await Promise.all([read('mailbox-worker'), read('auth-worker')]);
    if (typeof mailbox !== 'string' || mailbox !== auth) throw Error(`The Workers' versions differ (mailbox ${String(mailbox)}, auth ${String(auth)}); no release recorded.`);
    return mailbox;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
    // The release checkout may be an older commit than this script's (recording a release deployed earlier).
    const root = resolve(process.env.WEAVE_RELEASE_ROOT || resolve(import.meta.dirname, '../../..'));
    const version = await workersVersion(root);
    const notes = releaseNotes(await readFile(resolve(root, 'CHANGELOG.md'), 'utf8'), version);
    const url = await tagRelease({repository: process.env.GITHUB_REPOSITORY ?? '', sha: process.env.WEAVE_RELEASE_SHA ?? '', token: process.env.GITHUB_TOKEN ?? '',
        version, notes});
    console.log(`Released v${version}: ${url}`);
}
