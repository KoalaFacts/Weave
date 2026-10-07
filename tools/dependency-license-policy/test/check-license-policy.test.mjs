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

const inspectScenario = (independentRecords, actionRecords = structuredClone(independentRecords)) => inspectPolicy(
  JSON.stringify(actionRecords),
  JSON.stringify({
    status: 'available', dependency_changes: independentRecords.length, dependency_records: independentRecords,
  }),
);

test('inspectPolicy_PermittedLicenseAndEmptyDelta_Available', () => {
  assert.equal(inspectScenario([change('MIT')]).status, 'available');
  assert.equal(inspectScenario([]).status, 'available');
});

test('inspectPolicy_ProhibitedLicensesInExpressions_Blocked', () => {
  for (const license of ['GPL-2.0', 'GPL-3.0', 'AGPL-3.0',
    'MIT OR GPL-3.0', '(MIT AND Apache-2.0) OR AGPL-3.0',
    'GPL-3.0-only', 'GPL-3.0-or-later']) {
    assert.equal(inspectScenario([change(license)]).failure_code, 'license-policy-denied', license);
  }
  assert.equal(inspectScenario([change('GPL-3.0+ WITH Classpath-exception-2.0')]).status,
    'available');
  assert.equal(inspectScenario([change('GPL-3.0', 'removed')]).status, 'available');
  assert.equal(inspectScenario([{ change_type: 'removed', manifest: 'fixture.lock' }]).status,
    'available');
});

test('inspectPolicy_UnknownOrMissingLicense_Unavailable', () => {
  for (const license of [null, '', 'NOASSERTION', 'not-an-spdx-license']) {
    assert.equal(inspectScenario([change(license)]).status, 'unavailable', String(license));
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
  assert.equal(inspectScenario([pinned]).status, 'available');
  assert.equal(inspectScenario([{ ...pinned, version: 'different' }]).status, 'unavailable');
  assert.equal(inspectScenario([{ ...pinned, package_url: `pkg:githubactions/other/setup-node@${pin}` }]).status,
    'unavailable');
  assert.equal(inspectScenario([{ ...pinned, ecosystem: 'npm' }]).status, 'unavailable');
  assert.equal(inspectScenario([{ ...pinned, license: 'GPL-3.0' }]).status, 'blocked');
});

test('inspectPolicy_IncompleteComparison_Unavailable', () => {
  const added = [change('MIT')];
  assert.equal(inspectPolicy(JSON.stringify(added), '').status, 'unavailable');
  assert.equal(inspectPolicy(JSON.stringify(added),
    JSON.stringify({ status: 'available', dependency_changes: 2, dependency_records: added })).status, 'unavailable');
  assert.equal(inspectPolicy('{',
    JSON.stringify({ status: 'available', dependency_changes: 1, dependency_records: added })).status, 'unavailable');
  assert.equal(inspectPolicy(JSON.stringify(added),
    JSON.stringify({ status: 'available', dependency_changes: 1 })).failure_code,
    'incomplete-dependency-comparison');
});

test('inspectPolicy_ReportNeverContainsPackageOrLicenseText', () => {
  const report = inspectScenario([{
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
    const independentRecords = [{
      ...change('GPL-3.0'), name: 'private-package-name',
    }];
    writeFileSync(evidence, JSON.stringify({
      status: 'available', dependency_changes: 1, dependency_records: independentRecords,
    }));
    writeFileSync(changes, JSON.stringify(structuredClone(independentRecords)));
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

const runFilePolicy = (payload, independentRecords, dependencyChanges = independentRecords.length) => {
  const directory = mkdtempSync(join(tmpdir(), 'weave-license-policy-file-'));
  try {
    const changes = join(directory, 'changes.json');
    const evidence = join(directory, 'evidence.json');
    const report = join(directory, 'report.json');
    if (payload !== undefined) writeFileSync(changes, payload);
    writeFileSync(evidence, JSON.stringify({
      status: 'available', dependency_changes: dependencyChanges, dependency_records: independentRecords,
    }));
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
  const independentRecords = largeChanges();
  independentRecords[0] = change('GPL-3.0', 'removed');
  const payload = JSON.stringify(structuredClone(independentRecords));
  assert.ok(Buffer.byteLength(payload) > 128 * 1024);
  const checked = runFilePolicy(payload, independentRecords);
  assert.equal(checked.result.status, 0, checked.result.stderr);
  assert.deepEqual(checked.report, {
    status: 'available', added: 198, removed: 1, forbidden: 0, unknown: 0,
    raw_changes: 199, distinct_changes: 199, independent_raw_changes: 199,
  });
});

test('cli_LargeFileWithDeniedOrUnknownFinalChange_FailsClosedWithoutTruncating', () => {
  for (const [license, status, code, forbidden, unknown] of [
    ['GPL-3.0', 'blocked', 'license-policy-denied', 1, 0],
    [null, 'unavailable', 'license-evidence-unavailable', 0, 1],
  ]) {
    const independentRecords = largeChanges();
    independentRecords[independentRecords.length - 1].license = license;
    const checked = runFilePolicy(JSON.stringify(structuredClone(independentRecords)), independentRecords);
    assert.equal(checked.result.status, 1);
    assert.deepEqual(checked.report, {
      status, failure_code: code, added: 199, removed: 0, forbidden, unknown,
      raw_changes: 199, distinct_changes: 199, independent_raw_changes: 199,
    });
    assert.doesNotMatch(checked.result.stdout + checked.result.stderr + JSON.stringify(checked.report),
      /private-|GPL-3\.0/);
  }
});

test('cli_MissingMalformedOversizedOrIncompleteFile_RemainsUnavailable', () => {
  for (const [payload, independentRecords, count, code] of [
    [undefined, [change('MIT')], 1, 'invalid-license-evidence'],
    ['{', [change('MIT')], 1, 'invalid-license-evidence'],
    [' '.repeat(2 * 1024 * 1024 + 1), [], 0, 'invalid-license-evidence'],
    [JSON.stringify([change('MIT')]), [change('MIT')], 2, 'incomplete-dependency-comparison'],
    [JSON.stringify(Array.from({ length: 2001 }, () => change('MIT'))), [change('MIT')], 1,
      'incomplete-dependency-comparison'],
  ]) {
    const checked = runFilePolicy(payload, independentRecords, count);
    assert.equal(checked.result.status, 1);
    assert.deepEqual(checked.report, { status: 'unavailable', failure_code: code });
  }
});

test('cli_Duplicate395And591Rows_MatchIndependentRecordsAndReportBothCounts', () => {
  const independentRecords = largeChanges();
  for (const copies of [2, 3]) {
    const actual = [
      ...Array.from({ length: copies }, () => structuredClone(independentRecords.slice(0, 196))).flat(),
      ...structuredClone(independentRecords.slice(196)),
    ];
    const checked = runFilePolicy(JSON.stringify(actual), independentRecords);
    assert.equal(checked.result.status, 0, checked.result.stderr);
    assert.deepEqual(checked.report, {
      status: 'available', added: 199, removed: 0, forbidden: 0, unknown: 0,
      raw_changes: copies === 2 ? 395 : 591, distinct_changes: 199, independent_raw_changes: 199,
    });
  }
});

test('cli_Duplicate591RowsWithDeniedOrUnknownFinalDistinctRecord_PreservesPolicy', () => {
  for (const [license, status, code, forbidden, unknown] of [
    ['GPL-3.0', 'blocked', 'license-policy-denied', 1, 0],
    [null, 'unavailable', 'license-evidence-unavailable', 0, 1],
  ]) {
    const independentRecords = largeChanges();
    independentRecords[198].license = license;
    const actual = [
      ...Array.from({ length: 3 }, () => structuredClone(independentRecords.slice(0, 196))).flat(),
      ...structuredClone(independentRecords.slice(196)),
    ];
    const checked = runFilePolicy(JSON.stringify(actual), independentRecords);
    assert.equal(checked.result.status, 1);
    assert.deepEqual(checked.report, {
      status, failure_code: code, added: 199, removed: 0, forbidden, unknown,
      raw_changes: 591, distinct_changes: 199, independent_raw_changes: 199,
    });
  }
});

test('cli_SameCountSubstitutionOrConflictingLicense_RemainsIncomplete', () => {
  const independentRecords = largeChanges();
  const substituted = structuredClone(independentRecords);
  substituted[198].name = 'substituted-action-record';
  const conflicting = [...structuredClone(independentRecords), { ...independentRecords[0], license: 'GPL-3.0' }];
  for (const actual of [substituted, conflicting]) {
    const checked = runFilePolicy(JSON.stringify(actual), independentRecords);
    assert.equal(checked.result.status, 1);
    assert.deepEqual(checked.report, {
      status: 'unavailable', failure_code: 'incomplete-dependency-comparison',
    });
  }
});
