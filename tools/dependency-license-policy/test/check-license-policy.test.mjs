import assert from 'node:assert/strict';
import { existsSync, mkdtempSync, readFileSync, rmdirSync, unlinkSync, writeFileSync } from 'node:fs';
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
  const evidence = join(directory, 'evidence.json');
  const report = join(directory, 'report.json');
  try {
    writeFileSync(evidence, JSON.stringify({ status: 'available', dependency_changes: 1 }));
    const result = spawnSync(process.execPath,
      [fileURLToPath(new URL('../check-license-policy.mjs', import.meta.url)), evidence, report], {
        env: { ...process.env, DEPENDENCY_CHANGES: JSON.stringify([{
          ...change('GPL-3.0'), name: 'private-package-name',
        }]) },
        encoding: 'utf8',
      });
    assert.equal(result.status, 1, result.stderr);
    assert.equal(JSON.parse(readFileSync(report, 'utf8')).failure_code,
      'license-policy-denied');
    assert.doesNotMatch(result.stdout + result.stderr + readFileSync(report, 'utf8'),
      /private-package-name/);
  } finally {
    if (existsSync(evidence)) unlinkSync(evidence);
    if (existsSync(report)) unlinkSync(report);
    rmdirSync(directory);
  }
});
