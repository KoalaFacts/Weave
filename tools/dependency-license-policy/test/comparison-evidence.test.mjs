import assert from 'node:assert/strict';
import { test } from 'node:test';
import { inspectComparison } from '../comparison-evidence.mjs';

const change = index => ({
  change_type: index < 62 || index >= 196 ? 'added' : 'removed',
  manifest: 'fixture/packages.lock.json',
  ecosystem: 'nuget',
  name: `fixture-${index}`,
  version: '1.0.0',
  package_url: `pkg:nuget/fixture-${index}@1.0.0`,
  license: 'MIT',
  source_repository_url: null,
  scope: 'runtime',
  vulnerabilities: [],
});
const records = () => Array.from({ length: 199 }, (_, index) => change(index));
const repeated = (rows, copies) => [
  ...Array.from({ length: copies }, () => rows.slice(0, 196)).flat(), ...rows.slice(196),
];
const evidence = rows => ({ status: 'available', dependency_changes: rows.length, dependency_records: rows });
const inspect = (actual, expected, overrides = {}) => inspectComparison(
  JSON.stringify(actual), JSON.stringify({ ...evidence(expected), ...overrides }),
);
const unavailable = { status: 'unavailable', failure_code: 'incomplete-dependency-comparison' };

test('inspectComparison_Observed395And591Rows_ExactlyMatchIndependent199Records', () => {
  const expected = records();
  for (const copies of [2, 3]) {
    const actual = repeated(expected, copies);
    assert.equal(actual.length, copies === 2 ? 395 : 591);
    const checked = inspect(actual, expected);
    assert.equal(checked.status, 'available');
    assert.equal(checked.raw_changes, actual.length);
    assert.equal(checked.independent_raw_changes, expected.length);
    assert.deepEqual(checked.changes, expected);
    assert.equal(checked.changes.filter(row => row.change_type === 'added').length, 65);
    assert.equal(checked.changes.filter(row => row.change_type === 'removed').length, 134);
  }
});

test('inspectComparison_EmptyIndependentComparison_Available', () => {
  assert.deepEqual(inspect([], []), {
    status: 'available', changes: [], raw_changes: 0, independent_raw_changes: 0,
  });
});

test('inspectComparison_IndependentExactDuplicates_PreserveIndependentRawCount', () => {
  const scenario = records();
  const independent = repeated(scenario, 2);
  const checked = inspect(repeated(scenario, 3), independent);
  assert.equal(checked.status, 'available');
  assert.equal(checked.raw_changes, 591);
  assert.equal(checked.independent_raw_changes, 395);
  assert.deepEqual(checked.changes, scenario);
});

test('inspectComparison_DuplicateInflatedMissingOrSubstitutedRecords_Unavailable', () => {
  const expected = records();
  const missing = [...expected.slice(0, 198), expected[0]];
  const substituted = [...expected.slice(0, 198), change(999)];
  for (const actual of [missing, repeated(missing, 3), substituted, [...expected, change(999)]]) {
    assert.deepEqual(inspect(actual, expected), unavailable);
  }
});

test('inspectComparison_MissingIndependentRecordsOrWrongIndependentRawCount_Unavailable', () => {
  const expected = records();
  for (const overrides of [
    { dependency_records: undefined }, { dependency_records: null },
    { dependency_changes: 198 }, { dependency_changes: 591 },
    { dependency_changes: -1 }, { status: 'unavailable' },
  ]) {
    assert.deepEqual(inspect(repeated(expected, 3), expected, overrides), unavailable);
  }
});

test('inspectComparison_ConflictingLicenseAdvisoryOrMetadataRows_Unavailable', () => {
  const expected = [change(0)];
  for (const replacement of [
    { license: 'Apache-2.0' },
    { vulnerabilities: [{ severity: 'high', advisory_ghsa_id: 'fixture-advisory' }] },
    { metadata: { origin: 'different' } },
    { scope: 'development' },
  ]) {
    const conflicting = { ...expected[0], ...replacement };
    assert.deepEqual(inspect([...expected, conflicting], expected), unavailable);
    assert.deepEqual(inspect(expected, [...expected, conflicting]), unavailable);
    assert.deepEqual(inspect([conflicting], expected), unavailable);
  }
});

test('inspectComparison_ObjectKeyOrderDoesNotChangeCompleteRecords', () => {
  const expected = [{ ...change(0), metadata: { first: true, second: null, ratio: 0.5 } }];
  const actual = [{ metadata: { ratio: 0.5, second: null, first: true }, ...change(0) }];
  assert.equal(inspect(actual, expected).status, 'available');
});

test('inspectComparison_FieldPresenceArrayOrderAndValueTypes_RemainDistinct', () => {
  const row = change(0);
  for (const [left, right] of [
    [{ ...row, metadata: null }, row],
    [{ ...row, metadata: [1, 2] }, { ...row, metadata: [2, 1] }],
    [{ ...row, metadata: 1 }, { ...row, metadata: '1' }],
    [{ ...row, metadata: false }, { ...row, metadata: 0 }],
  ]) {
    assert.deepEqual(inspect([left], [right]), unavailable);
  }
});

test('inspectComparison_PrototypeNamedFields_AreComparedWithoutMutation', () => {
  const row = JSON.parse(JSON.stringify(change(0)).slice(0, -1) +
    ',"__proto__":{"polluted":true},"constructor":{"prototype":{"polluted":true}}}');
  assert.equal(inspect([row, row], [row]).status, 'available');
  assert.equal({}.polluted, undefined);
  assert.deepEqual(inspect([change(0)], [row]), unavailable);
});

test('inspectComparison_DeniedFinalDistinctRecord_IsRetainedForUnchangedPolicy', () => {
  const expected = records();
  expected[198].license = 'GPL-3.0';
  const checked = inspect(repeated(expected, 3), expected);
  assert.equal(checked.status, 'available');
  assert.equal(checked.changes.length, 199);
  assert.equal(checked.changes[198].license, 'GPL-3.0');
});

test('inspectComparison_RawLimitsApplyBeforeCollapsingDuplicates', () => {
  const row = change(0);
  const padded = { ...row, metadata: 'x'.repeat(1500) };
  assert.deepEqual(inspect(Array.from({ length: 2001 }, () => row), [row]), unavailable);
  assert.deepEqual(inspect([row], Array.from({ length: 2001 }, () => row)), unavailable);
  const oversized = JSON.stringify(Array.from({ length: 1500 }, () => padded));
  assert.ok(Buffer.byteLength(oversized) > 2 * 1024 * 1024);
  const invalid = { status: 'unavailable', failure_code: 'invalid-license-evidence' };
  assert.deepEqual(inspectComparison(oversized, JSON.stringify(evidence([padded]))), invalid);
  assert.deepEqual(inspectComparison(JSON.stringify([padded]), JSON.stringify({
    ...evidence([padded]), metadata: 'x'.repeat(2 * 1024 * 1024),
  })), invalid);
});

test('inspectComparison_MalformedOrNonfiniteData_FailsClosed', () => {
  const row = change(0);
  const invalid = { status: 'unavailable', failure_code: 'invalid-license-evidence' };
  for (const payload of ['', '{', JSON.stringify([null]), JSON.stringify([{}]),
    JSON.stringify([row]).replace('"license":"MIT"', '"metadata":1e999,"license":"MIT"')]) {
    assert.deepEqual(inspectComparison(payload, JSON.stringify(evidence([row]))), invalid);
  }
  assert.deepEqual(inspectComparison(JSON.stringify([row]),
    JSON.stringify(evidence([row])).replace('"license":"MIT"', '"metadata":1e999,"license":"MIT"')), invalid);
  for (const number of ['9007199254740992', '9007199254740993']) {
    assert.deepEqual(inspectComparison(
      JSON.stringify([row]).replace('"license":"MIT"', `"metadata":${number},"license":"MIT"`),
      JSON.stringify(evidence([row]))), invalid);
  }
});
