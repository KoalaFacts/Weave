import assert from 'node:assert/strict';
import { test } from 'node:test';
import { inspectPolicy } from '../check-license-policy.ts';

const records = () => Array.from({ length: 2105 }, (_, index) => ({
  change_type: 'added',
  manifest: 'csharp/src/packages.lock.json',
  ecosystem: 'nuget',
  name: `moved-component-${index}`,
  version: '1.0.0',
  package_url: `pkg:nuget/moved-component-${index}@1.0.0`,
  license: 'MIT',
  vulnerabilities: [],
}));

const check = (independent, actual = structuredClone(independent)) => inspectPolicy(
  JSON.stringify(actual),
  JSON.stringify({
    status: 'available', dependency_changes: independent.length,
    dependency_records: independent,
  }),
);

test('complete 2105-change comparison survives the action/license evidence boundary', () => {
  const all = records();
  const report = check(all);
  assert.equal(report.status, 'available');
  assert.equal(report.added, 2105);
  assert.equal(report.distinct_changes, 2105);
  assert.equal(report.independent_raw_changes, 2105);
});

test('large comparison still fails closed on omitted or forbidden last entry', () => {
  const all = records();
  assert.equal(check(all, all.slice(0, -1)).failure_code,
    'incomplete-dependency-comparison');
  all[all.length - 1].license = 'GPL-3.0';
  assert.equal(check(all).failure_code, 'license-policy-denied');
});
