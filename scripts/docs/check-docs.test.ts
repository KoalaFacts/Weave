import assert from 'node:assert/strict';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { anchors, check, links, loadAllowances, loadRepo, slug, type Allowance, type Repo } from './check-docs.ts';

const root = fileURLToPath(new URL('../../', import.meta.url));

function repo(docs: Record<string, string>, extra: string[] = [], npmScripts: Record<string, string[]> = {}): Repo {
    const files = new Set([...Object.keys(docs), ...extra]);
    const exists = (path: string) => files.has(path) || [...files].some((file) => file.startsWith(`${path}/`));
    return {
        docs: new Map(Object.entries(docs)),
        exists,
        npmScripts: new Map(Object.entries(npmScripts).map(([dir, names]) => [dir, new Set(names)])),
    };
}

const base = {
    'README.md': '# Weave\n\n[Docs](docs/README.md)\n',
    'docs/README.md': '# Index\n\n- [Guide](guide.md)\n',
    'docs/guide.md': '# Guide\n\n## Set up the Worker\n\nText.\n',
};

const messages = (docs: Record<string, string>, options: { extra?: string[]; allowances?: Allowance[]; npm?: Record<string, string[]> } = {}) =>
    check(repo(docs, options.extra, options.npm), options.allowances ?? []).map((problem) => `${problem.file}: ${problem.message}`);

test('check_ConsistentDocs_NoProblems', () => {
    assert.deepEqual(messages(base), []);
});

test('check_BrokenRelativeLink_Reported', () => {
    const result = messages({ ...base, 'docs/guide.md': '# Guide\n\nSee [missing](missing.md).\n' });
    assert.deepEqual(result, ['docs/guide.md: broken link: missing.md (no docs/missing.md)']);
});

test('check_LinkEscapingRepository_Reported', () => {
    // Even when a file exists there on disk, a link outside the repository is broken for readers of the repository.
    const result = messages({ ...base, 'docs/guide.md': '# Guide\n\n[out](../../outside.md)\n' }, { extra: ['../outside.md'] });
    assert.equal(result.length, 1);
    assert.match(result[0], /broken link: \.\.\/\.\.\/outside\.md/);
});

test('check_BrokenAnchor_Reported', () => {
    const result = messages({ ...base, 'docs/README.md': '# Index\n\n- [Guide](guide.md#set-up-a-worker)\n' });
    assert.deepEqual(result, ['docs/README.md: broken anchor: guide.md#set-up-a-worker (no heading or id "set-up-a-worker" in docs/guide.md)']);
});

test('check_ValidAnchorsAndExplicitIds_Accepted', () => {
    const result = messages({
        ...base,
        'docs/README.md': '# Index\n\n- [Guide](guide.md#set-up-the-worker) [id](guide.md#custom) [same](#index) [line](../README.md#L3)\n',
        'docs/guide.md': '# Guide\n\n## Set up the Worker\n\n<a id="custom"></a>\n',
    });
    assert.deepEqual(result, []);
});

test('check_LinksInsideCodeAreIgnored', () => {
    const result = messages({ ...base, 'docs/guide.md': '# Guide\n\n```md\n[x](nope.md)\n```\n\nInline `[y](nope.md)` too.\n' });
    assert.deepEqual(result, []);
});

test('check_ExternalLinks_NotResolved', () => {
    assert.deepEqual(messages({ ...base, 'docs/guide.md': '# Guide\n\n[a](https://example.com/x.md) [b](mailto:a@example.com)\n' }), []);
});

test('check_DocUnreachableFromIndex_Reported', () => {
    const result = messages({ ...base, 'docs/orphan.md': '# Orphan\n' });
    assert.deepEqual(result, ['docs/orphan.md: not reachable from docs/README.md; list it there or in a page it links to']);
});

test('check_DocReachableThroughSubIndex_Accepted', () => {
    const result = messages({
        ...base,
        'docs/README.md': '# Index\n\n- [Guide](guide.md)\n- [Records](implementation/README.md)\n',
        'docs/implementation/README.md': '# Records\n\n- [One](2026-01-01-one.md)\n',
        'docs/implementation/2026-01-01-one.md': '# One\n',
    });
    assert.deepEqual(result, []);
});

test('check_RootReadmeWithoutIndexLink_Reported', () => {
    const result = messages({ ...base, 'README.md': '# Weave\n' });
    assert.deepEqual(result, ['README.md: does not link to the docs index docs/README.md']);
});

test('check_MissingIndex_Reported', () => {
    const { 'docs/README.md': _index, ...rest } = base;
    const result = messages({ ...rest, 'README.md': '# Weave\n' });
    assert.ok(result.includes('docs/README.md: the docs index is missing'));
});

test('check_NamedPathMissingInCurrentDoc_Reported', () => {
    const result = messages({ ...base, 'docs/guide.md': '# Guide\n\nEdit `csharp/src/Gone/Thing.cs` and `typescript/mailbox-worker/src/`.\n' }, { extra: ['typescript/mailbox-worker/src/index.ts'] });
    assert.deepEqual(result, ['docs/guide.md: named path does not exist: csharp/src/Gone/Thing.cs']);
});

test('check_NamedPathRelativeToPackageReadme_Accepted', () => {
    const result = messages({ ...base, 'typescript/w/README.md': '# W\n\nRun `scripts/deploy.ts`.\n' }, { extra: ['typescript/w/scripts/deploy.ts'] });
    assert.deepEqual(result, []);
});

test('check_PlaceholdersAndGlobs_NotTreatedAsPaths', () => {
    const result = messages({ ...base, 'docs/guide.md': '# Guide\n\n`docs/implementation/*.md`, `csharp/src/{Feature}/`, `scripts/<name>.ts`.\n' });
    assert.deepEqual(result, []);
});

test('check_HistoricalDocs_PathsNotChecked_LinksStillChecked', () => {
    const result = messages({
        ...base,
        'docs/README.md': '# Index\n\n- [Guide](guide.md)\n- [Old](old.md)\n',
        'docs/old.md': '# Old\n\n> **Historical guide.** Old layout.\n\nSee `csharp/src/Foundation/`. [gone](gone.md)\n',
    });
    assert.deepEqual(result, ['docs/old.md: broken link: gone.md (no docs/gone.md)']);
});

test('check_NpmScriptMissingInOwnPackage_Reported', () => {
    const result = messages({ ...base, 'typescript/w/README.md': '# W\n\nRun `npm run test` then `npm run deploy:everything`.\n' }, { npm: { 'typescript/w': ['test'] } });
    assert.deepEqual(result, ['typescript/w/README.md: npm script does not exist: deploy:everything in typescript/w/package.json']);
});

test('check_NpmScriptNamedOutsidePackage_AcceptedWhenAnyPackageHasIt', () => {
    assert.deepEqual(messages({ ...base, 'docs/guide.md': '# Guide\n\n`npm run build`\n' }, { npm: { 'typescript/w': ['build'] } }), []);
    assert.equal(messages({ ...base, 'docs/guide.md': '# Guide\n\n`npm run nothing`\n' }, { npm: { 'typescript/w': ['build'] } }).length, 1);
});

test('check_AllowanceExcusesExactFinding', () => {
    const allowances = [{ file: 'docs/guide.md', target: 'gone.md', reason: 'historical' }];
    assert.deepEqual(messages({ ...base, 'docs/guide.md': '# Guide\n\n[gone](gone.md)\n' }, { allowances }), []);
});

test('check_AllowanceForOtherFile_DoesNotExcuse', () => {
    const allowances = [{ file: 'docs/other.md', target: 'gone.md', reason: 'historical' }];
    const result = messages({ ...base, 'docs/guide.md': '# Guide\n\n[gone](gone.md)\n' }, { allowances });
    assert.deepEqual(result, ['docs/guide.md: broken link: gone.md (no docs/gone.md)', 'docs/other.md: stale allowance: gone.md is no longer reported here; remove it']);
});

test('check_StaleAllowance_Reported', () => {
    const result = messages(base, { allowances: [{ file: 'docs/guide.md', target: 'fixed.md', reason: 'was broken' }] });
    assert.deepEqual(result, ['docs/guide.md: stale allowance: fixed.md is no longer reported here; remove it']);
});

test('slug_MatchesGitHubAnchors', () => {
    assert.equal(slug('Ownership and composition'), 'ownership-and-composition');
    assert.equal(slug('Build & verification'), 'build--verification');
    assert.equal(slug('`npm run build` (local)'), 'npm-run-build-local');
    assert.equal(slug('Weave ⇄ Envoylet'), 'weave--envoylet');
});

test('anchors_DuplicateHeadingsNumbered', () => {
    const found = anchors('# A\n\n## Steps\n\n## Steps\n\n```\n# not a heading\n```\n');
    assert.deepEqual([...found].sort(), ['a', 'steps', 'steps-1']);
});

test('links_ReferenceDefinitionsAndHtml', () => {
    const found = links('[a][ref]\n\n[ref]: target.md\n<a href="other.md">x</a>\n').map((link) => link.target);
    assert.deepEqual(found, ['target.md', 'other.md']);
});

test('loadAllowances_RequiresReason', () => {
    assert.throws(() => loadAllowances(fileURLToPath(new URL('./fixtures/allowance-without-reason.json', import.meta.url))), /needs file, target and a reason/);
});

test('repository_DocsAreConsistent', () => {
    const problems = check(loadRepo(root), loadAllowances(fileURLToPath(new URL('./allowances.json', import.meta.url))));
    assert.deepEqual(problems, []);
});

test('links_AngleBracketsTitlesAndDoubleBacktickCode', () => {
    const found = links('[a](<with space.md> "Title") [b](plain.md "t") ``[c](code.md) ` `` and [d](after.md)\n').map((link) => link.target);
    assert.deepEqual(found, ['with space.md', 'plain.md', 'after.md']);
});

test('anchors_ClosingHashesAndAttributeOrder', () => {
    const found = anchors('## Closing hashes ##\n\n# C# notes\n\n<a class="x" name="legacy"></a>\n');
    assert.deepEqual([...found].sort(), ['c-notes', 'closing-hashes', 'legacy']);
});

test('check_NamedPathWithLineSuffixOrTrailingPunctuation_ResolvesFile', () => {
    const docs = { ...base, 'docs/guide.md': '# Guide\n\nSee `scripts/x.ts:12-20`, `scripts/x.ts#L4` and `scripts/x.ts).`\n' };
    assert.deepEqual(messages(docs, { extra: ['scripts/x.ts'] }), []);
});

test('check_LongPathologicalLines_FinishQuickly', () => {
    const line = '#'.repeat(6) + ' ' + ' '.repeat(20000) + 'x' + ' #'.repeat(5000) + '<a ' + 'b="'.repeat(5000) + '](' + ' '.repeat(20000);
    const started = performance.now();
    messages({ ...base, 'docs/guide.md': `# Guide\n\n${line}\n\`${'`'.repeat(5000)}\n` });
    assert.ok(performance.now() - started < 2000);
});
