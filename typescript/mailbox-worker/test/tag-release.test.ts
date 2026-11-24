import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { releaseNotes, tagRelease, workersVersion } from '../scripts/tag-release.ts';

const sha = 'a'.repeat(40), other = 'b'.repeat(40);
const changelog = '# Changelog\n\nIntro.\n\n## Unreleased\n\n- next\n\n## 0.2.0 — 2026-11-25\n\n### Added\n\n- thing (#1)\n\n## 0.1.0 — 2026-11-24\n\n- first\n';

type Answer = {status: number; body?: unknown};
/** Answers each GitHub path with a fixed response (404 when unlisted) and records the calls. */
function github(answers: Record<string, Answer>) {
    const calls: {path: string; method: string; body: unknown; auth: string | null; redirect?: string}[] = [];
    const fetchImpl = (async (url: string, init?: RequestInit) => {
        const parsed = new URL(String(url));
        const path = parsed.pathname.replace('/repos/KoalaFacts/Weave', '') + parsed.search;
        calls.push({path, method: init?.method ?? 'GET', body: init?.body ? JSON.parse(String(init.body)) : undefined,
            auth: new Headers(init?.headers).get('Authorization'), redirect: init?.redirect});
        const key = Object.keys(answers).find(prefix => (init?.method ?? 'GET') + ' ' + path === prefix || ((init?.method ?? 'GET') + ' ' + path).startsWith(prefix + '?'));
        const answer = key ? answers[key] : {status: 404, body: {message: 'Not Found'}};
        return Response.json(answer.body ?? null, {status: answer.status});
    }) as typeof fetch;
    return {calls, options: {repository: 'KoalaFacts/Weave', sha, token: 'ghs_synthetic', version: '0.2.0', notes: 'notes', log: () => {}, fetchImpl}};
}
const deployed = (state = 'success', deployments: unknown[] = [{id: 7, sha, ref: 'main'}]): Record<string, Answer> => ({
    'GET /deployments': {status: 200, body: deployments},
    'GET /deployments/7/statuses': {status: 200, body: [{state}, {state: 'success'}]},
    'GET /deployments/9/statuses': {status: 200, body: [{state: 'failure'}]}
});
const created = {'POST /releases': {status: 201, body: {html_url: 'https://github.com/KoalaFacts/Weave/releases/tag/v0.2.0'}}};

test('releaseNotes_VersionSection_ReturnsItsBodyOnly', () => {
    assert.equal(releaseNotes(changelog, '0.2.0'), '### Added\n\n- thing (#1)');
    assert.equal(releaseNotes(changelog, '0.1.0'), '- first');
});

test('releaseNotes_MissingOrEmptySection_Throws', () => {
    assert.throws(() => releaseNotes(changelog, '0.3.0'), /no "## 0\.3\.0" section/);
    assert.throws(() => releaseNotes(changelog, '0.1'), /no "## 0\.1" section/);
    assert.throws(() => releaseNotes('## 0.2.0\n\n## 0.1.0\n- x\n', '0.2.0'), /section is empty/);
});

test('tagRelease_DeployedCommitWithoutTag_CreatesReleaseAtThatCommit', async () => {
    const g = github({...deployed(), ...created});
    assert.equal(await tagRelease(g.options), 'https://github.com/KoalaFacts/Weave/releases/tag/v0.2.0');
    const deploymentsQuery = new URLSearchParams(g.calls[0].path.split('?')[1]);
    assert.deepEqual([deploymentsQuery.get('environment'), deploymentsQuery.get('sha')], ['production', sha]);
    assert.deepEqual([g.calls[0].auth, g.calls[0].redirect], ['Bearer ghs_synthetic', 'error']);
    const post = g.calls.find(call => call.method === 'POST');
    assert.deepEqual(post?.body, {tag_name: 'v0.2.0', target_commitish: sha, name: 'v0.2.0', body: 'notes', draft: false, prerelease: false, make_latest: 'legacy'});
});

test('tagRelease_NewestDeploymentFailedAfterAnOlderSuccess_RecordsNothing', async () => {
    const g = github({...deployed('success', [{id: 7, sha, ref: 'main'}, {id: 9, sha, ref: 'main'}]), ...created});
    await assert.rejects(tagRelease(g.options), /newest production deployment is failure/);
    assert.equal(g.calls.some(call => call.method === 'POST'), false);
});

test('tagRelease_NoSuccessfulProductionDeployment_RecordsNothing', async () => {
    for (const [answers, reason] of [
        [deployed('in_progress'), /is in_progress, not success/],
        [deployed('success', []), /No production deployment of this commit/],
        [deployed('success', [{id: 7, sha, ref: 'weave/feature'}]), /No production deployment of this commit/],
        [deployed('success', [{id: 7, sha: other, ref: 'main'}]), /No production deployment of this commit/],
        [{'GET /deployments': {status: 500}}, /HTTP 500/]
    ] as const) {
        const g = github({...answers, ...created});
        await assert.rejects(tagRelease(g.options), reason);
        assert.equal(g.calls.some(call => call.method === 'POST'), false);
    }
});

test('tagRelease_IfDeployedWithoutProductionDeployment_SkipsButStillRefusesAFailedOne', async () => {
    const skipped = github({...deployed('success', [{id: 7, sha, ref: 'weave/feature'}]), ...created});
    assert.equal(await tagRelease({...skipped.options, ifDeployed: true}), '');
    assert.equal(skipped.calls.some(call => call.method === 'POST'), false);
    await assert.rejects(tagRelease({...github({...deployed('failure'), ...created}).options, ifDeployed: true}), /is failure, not success/);
});

test('tagRelease_TagOnAnotherCommit_RefusesToReuseTheVersion', async () => {
    for (const tagAnswers of [
        {'GET /git/ref/tags/v0.2.0': {status: 200, body: {object: {type: 'commit', sha: other}}}},
        {'GET /git/ref/tags/v0.2.0': {status: 200, body: {object: {type: 'tag', sha: 'c'.repeat(40)}}},
            ['GET /git/tags/' + 'c'.repeat(40)]: {status: 200, body: {object: {type: 'commit', sha: other}}}}
    ]) {
        const g = github({...deployed(), ...tagAnswers, ...created});
        await assert.rejects(tagRelease(g.options), new RegExp(`v0\\.2\\.0 already tags commit ${other}; bump the version`));
        assert.equal(g.calls.some(call => call.method === 'POST'), false);
    }
});

test('tagRelease_TagAndReleaseAlreadyForThisCommit_ReturnsExistingRelease', async () => {
    const g = github({...deployed(), 'GET /git/ref/tags/v0.2.0': {status: 200, body: {object: {type: 'commit', sha}}},
        'GET /releases/tags/v0.2.0': {status: 200, body: {html_url: 'https://example.test/existing'}}, ...created});
    assert.equal(await tagRelease(g.options), 'https://example.test/existing');
    assert.equal(g.calls.some(call => call.method === 'POST'), false);
});

test('tagRelease_TagForThisCommitWithoutRelease_CreatesTheRelease', async () => {
    const g = github({...deployed(), 'GET /git/ref/tags/v0.2.0': {status: 200, body: {object: {type: 'commit', sha}}}, ...created});
    assert.equal(await tagRelease(g.options), 'https://github.com/KoalaFacts/Weave/releases/tag/v0.2.0');
});

test('tagRelease_TagLookupOrCreateFails_Throws', async () => {
    await assert.rejects(tagRelease(github({...deployed(), 'GET /git/ref/tags/v0.2.0': {status: 403}, ...created}).options), /git\/ref\/tags failed \(HTTP 403\)/);
    await assert.rejects(tagRelease(github({...deployed(), 'POST /releases': {status: 422}}).options), /\/releases failed \(HTTP 422\)/);
    const g = github({...deployed(), 'GET /git/ref/tags/v0.2.0': {status: 200, body: {object: {type: 'commit', sha}}}, 'GET /releases/tags/v0.2.0': {status: 500}, ...created});
    await assert.rejects(tagRelease(g.options), /releases\/tags failed \(HTTP 500\)/);
    assert.equal(g.calls.some(call => call.method === 'POST'), false);
});

test('tagRelease_PrereleaseVersion_MarksPrerelease', async () => {
    const g = github({...deployed(), ...created});
    await tagRelease({...g.options, version: '0.2.0-rc.1'});
    assert.equal((g.calls.find(call => call.method === 'POST')?.body as {prerelease?: unknown}).prerelease, true);
});

test('tagRelease_InvalidInput_CallsNothing', async () => {
    for (const bad of [{sha: 'abc'}, {version: '0.2'}, {version: 'v0.2.0'}, {token: ''}, {notes: ''}, {repository: 'not a repo'}]) {
        const g = github({...deployed(), ...created});
        await assert.rejects(tagRelease({...g.options, ...bad}), /are required/);
        assert.equal(g.calls.length, 0);
    }
});

test('workersVersion_SameOrDifferentVersions_ReturnsOrThrows', async () => {
    const root = await mkdtemp(join(tmpdir(), 'tag-release-'));
    const write = async (name: string, version: string) => {
        await mkdir(join(root, 'typescript', name), {recursive: true});
        await writeFile(join(root, 'typescript', name, 'package.json'), JSON.stringify({version}));
    };
    await write('mailbox-worker', '0.2.0'); await write('auth-worker', '0.2.0');
    assert.equal(await workersVersion(root), '0.2.0');
    await write('auth-worker', '0.1.0');
    await assert.rejects(workersVersion(root), /versions differ \(mailbox 0\.2\.0, auth 0\.1\.0\)/);
});
