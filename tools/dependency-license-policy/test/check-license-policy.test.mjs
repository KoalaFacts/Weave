import assert from 'node:assert/strict';
import { existsSync, mkdtempSync, readFileSync, rmSync, rmdirSync, unlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { inspectPolicy } from '../check-license-policy.mjs';

const change = (license, change_type = 'added') => ({
  change_type,
  manifest: 'fixture.lock',
  name: 'fixture',
  version: '1.0.0',
  package_url: 'pkg:nuget/fixture@1.0.0',
  license,
});

const inspect = changes => inspectPolicy(
  JSON.stringify(changes),
  JSON.stringify({ status: 'available', dependency_changes: changes.length }),
);

test('inspectPolicy_PermittedLicenseAndEmptyDelta_Available', () => {
  assert.equal(inspect([change('MIT')]).status, 'available');
  assert.equal(inspect([]).status, 'available');
});

test('inspectPolicy_ProhibitedLicensesInExpressions_Blocked', () => {
  for (const license of ['GPL-2.0', 'GPL-3.0', 'AGPL-3.0',
    'MIT OR GPL-3.0', '(MIT AND Apache-2.0) OR AGPL-3.0',
    'GPL-3.0-only', 'GPL-3.0-or-later']) {
    assert.equal(inspect([change(license)]).failure_code, 'license-policy-denied', license);
  }
  assert.equal(inspect([change('GPL-3.0+ WITH Classpath-exception-2.0')]).status,
    'available');
  assert.equal(inspect([change('GPL-3.0', 'removed')]).status, 'available');
  assert.equal(inspect([{ change_type: 'removed', manifest: 'fixture.lock' }]).status,
    'available');
});

test('inspectPolicy_UnknownOrMissingLicense_Unavailable', () => {
  for (const license of [null, '', 'NOASSERTION', 'not-an-spdx-license']) {
    assert.equal(inspect([change(license)]).status, 'unavailable', String(license));
  }
});

test('inspectPolicy_PinnedSetupNodeWithVerifiedLicense_Available', () => {
  const pin = '249970729cb0ef3589644e2896645e5dc5ba9c38';
  const pinned = {
    ...change(null),
    ecosystem: 'actions',
    name: 'actions/setup-node',
    version: pin,
    package_url: `pkg:githubactions/actions/setup-node@${pin}`,
  };
  assert.equal(inspect([pinned]).status, 'available');
  assert.equal(inspect([{ ...pinned, version: 'different' }]).status, 'unavailable');
  assert.equal(inspect([{ ...pinned, package_url: `pkg:githubactions/other/setup-node@${pin}` }]).status,
    'unavailable');
  assert.equal(inspect([{ ...pinned, ecosystem: 'npm' }]).status, 'unavailable');
  assert.equal(inspect([{ ...pinned, license: 'GPL-3.0' }]).status, 'blocked');
});

test('inspectPolicy_IncompleteComparison_Unavailable', () => {
  const added = [change('MIT')];
  assert.equal(inspectPolicy(JSON.stringify(added), '').status, 'unavailable');
  assert.equal(inspectPolicy(JSON.stringify(added),
    JSON.stringify({ status: 'available', dependency_changes: 2 })).status, 'unavailable');
  assert.equal(inspectPolicy('{',
    JSON.stringify({ status: 'available', dependency_changes: 1 })).status, 'unavailable');
});

test('inspectPolicy_ReportNeverContainsPackageOrLicenseText', () => {
  const report = inspect([{
    ...change('GPL-3.0'),
    name: 'private-package-name',
    package_url: 'pkg:nuget/private-package-name@1.0.0',
  }]);
  assert.equal(report.status, 'blocked');
  assert.doesNotMatch(JSON.stringify(report), /private-package-name|GPL-3\.0/);
});

test('cli_ProhibitedLicense_ExitsWithoutLeakingPackageText', () => {
  const directory = mkdtempSync(join(tmpdir(), 'weave-license-policy-'));
  const changes = join(directory, 'changes.json');
  const evidence = join(directory, 'evidence.json');
  const report = join(directory, 'report.json');
  try {
    writeFileSync(evidence, JSON.stringify({ status: 'available', dependency_changes: 1 }));
    writeFileSync(changes, JSON.stringify([{
      ...change('GPL-3.0'), name: 'private-package-name',
    }]));
    const result = spawnSync(process.execPath,
      [fileURLToPath(new URL('../check-license-policy.mjs', import.meta.url)), changes, evidence, report], {
        encoding: 'utf8',
      });
    assert.equal(result.status, 1, result.stderr);
    assert.equal(JSON.parse(readFileSync(report, 'utf8')).failure_code,
      'license-policy-denied');
    assert.doesNotMatch(result.stdout + result.stderr + readFileSync(report, 'utf8'),
      /private-package-name/);
  } finally {
    if (existsSync(changes)) unlinkSync(changes);
    if (existsSync(evidence)) unlinkSync(evidence);
    if (existsSync(report)) unlinkSync(report);
    rmdirSync(directory);
  }
});

const runFilePolicy = (payload, dependencyChanges) => {
  const directory = mkdtempSync(join(tmpdir(), 'weave-license-policy-file-'));
  try {
    const changes = join(directory, 'changes.json');
    const evidence = join(directory, 'evidence.json');
    const report = join(directory, 'report.json');
    if (payload !== undefined) writeFileSync(changes, payload);
    writeFileSync(evidence, JSON.stringify({ status: 'available', dependency_changes: dependencyChanges }));
    const result = spawnSync(process.execPath,
      [fileURLToPath(new URL('../check-license-policy.mjs', import.meta.url)), changes, evidence, report], {
        encoding: 'utf8',
        // A stale environment value must never replace file evidence.
        env: { ...process.env, DEPENDENCY_CHANGES: '[]' },
      });
    assert.equal(result.error, undefined);
    return { result, report: JSON.parse(readFileSync(report, 'utf8')) };
  } finally {
    rmSync(directory, { recursive: true });
  }
};

const largeChanges = () => Array.from({ length: 199 }, (_, index) => ({
  ...change('MIT'), name: `private-${index}-${'x'.repeat(750)}`,
}));

test('cli_LargeFileBeyondEnvironmentBoundary_ProcessesEveryChange', () => {
  const changes = largeChanges();
  changes[0] = change('GPL-3.0', 'removed');
  const payload = JSON.stringify(changes);
  assert.ok(Buffer.byteLength(payload) > 128 * 1024);
  const checked = runFilePolicy(payload, changes.length);
  assert.equal(checked.result.status, 0, checked.result.stderr);
  assert.deepEqual(checked.report, {
    status: 'available', added: 198, removed: 1, forbidden: 0, unknown: 0,
  });
});

test('cli_LargeFileWithDeniedOrUnknownFinalChange_FailsClosedWithoutTruncating', () => {
  for (const [license, status, code, forbidden, unknown] of [
    ['GPL-3.0', 'blocked', 'license-policy-denied', 1, 0],
    [null, 'unavailable', 'license-evidence-unavailable', 0, 1],
  ]) {
    const changes = largeChanges();
    changes[changes.length - 1].license = license;
    const checked = runFilePolicy(JSON.stringify(changes), changes.length);
    assert.equal(checked.result.status, 1);
    assert.deepEqual(checked.report, {
      status, failure_code: code, added: 199, removed: 0, forbidden, unknown,
    });
    assert.doesNotMatch(checked.result.stdout + checked.result.stderr + JSON.stringify(checked.report),
      /private-|GPL-3\.0/);
  }
});

test('cli_MissingMalformedOversizedOrIncompleteFile_RemainsUnavailable', () => {
  for (const [payload, count, code] of [
    [undefined, 1, 'invalid-license-evidence'],
    ['{', 1, 'invalid-license-evidence'],
    [' '.repeat(2 * 1024 * 1024 + 1), 0, 'invalid-license-evidence'],
    [JSON.stringify([change('MIT')]), 2, 'incomplete-dependency-comparison'],
    [JSON.stringify(Array.from({ length: 2001 }, () => change('MIT'))), 2001,
      'incomplete-dependency-comparison'],
  ]) {
    const checked = runFilePolicy(payload, count);
    assert.equal(checked.result.status, 1);
    assert.deepEqual(checked.report, { status: 'unavailable', failure_code: code });
  }
});
