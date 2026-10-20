import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { satisfiesAny } from '@onebeyond/spdx-license-satisfies';
import parseSpdx from 'spdx-expression-parse';
import { inspectComparison } from './comparison-evidence.ts';

const DENIED = ['GPL-2.0', 'GPL-3.0', 'AGPL-3.0'];
// GitHub's dependency graph omits the license for this action. The MIT license
// was checked at the pinned source commit: https://github.com/actions/setup-node/blob/249970729cb0ef3589644e2896645e5dc5ba9c38/LICENSE
const VERIFIED_ACTION_LICENSES = new Map([
  // Apache-2.0 verified at https://github.com/EnricoMi/publish-unit-test-result-action/blob/7622c5bf3d112a72ab4fa3951c2aa5723d8ab92d/LICENSE
  ['pkg:githubactions/EnricoMi/publish-unit-test-result-action@7622c5bf3d112a72ab4fa3951c2aa5723d8ab92d', {
    name: 'EnricoMi/publish-unit-test-result-action',
    version: '7622c5bf3d112a72ab4fa3951c2aa5723d8ab92d',
    license: 'Apache-2.0',
  }],
  // MIT verified at https://github.com/actions/checkout/blob/3d3c42e5aac5ba805825da76410c181273ba90b1/LICENSE
  ['pkg:githubactions/actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1', {
    name: 'actions/checkout',
    version: '3d3c42e5aac5ba805825da76410c181273ba90b1',
    license: 'MIT',
  }],
  // MIT verified at https://github.com/actions/setup-node/blob/949feb2413d6458794dcd2491c4babbbce0c15c1/LICENSE
  ['pkg:githubactions/actions/setup-node@949feb2413d6458794dcd2491c4babbbce0c15c1', {
    name: 'actions/setup-node',
    version: '949feb2413d6458794dcd2491c4babbbce0c15c1',
    license: 'MIT',
  }],
  // MIT verified at https://github.com/actions/upload-artifact/blob/043fb46d1a93c77aae656e7c1c64a875d1fc6a0a/LICENSE
  ['pkg:githubactions/actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a', {
    name: 'actions/upload-artifact',
    version: '043fb46d1a93c77aae656e7c1c64a875d1fc6a0a',
    license: 'MIT',
  }],
  // MIT verified at https://github.com/actions/upload-artifact/blob/cf430e030ddbb5b0abf93d22962f4752f3646cd9/LICENSE
  ['pkg:githubactions/actions/upload-artifact@cf430e030ddbb5b0abf93d22962f4752f3646cd9', {
    name: 'actions/upload-artifact',
    version: 'cf430e030ddbb5b0abf93d22962f4752f3646cd9',
    license: 'MIT',
  }],
  // MIT verified at https://github.com/actions/download-artifact/blob/9000827ccba6bdab643e8b6fd33ac0654aef8333/LICENSE
  ['pkg:githubactions/actions/download-artifact@9000827ccba6bdab643e8b6fd33ac0654aef8333', {
    name: 'actions/download-artifact',
    version: '9000827ccba6bdab643e8b6fd33ac0654aef8333',
    license: 'MIT',
  }],
  ['pkg:githubactions/actions/setup-node@249970729cb0ef3589644e2896645e5dc5ba9c38', {
    name: 'actions/setup-node',
    version: '249970729cb0ef3589644e2896645e5dc5ba9c38',
    license: 'MIT',
  }],
]);

interface PolicyReport {status:'available'|'unavailable'|'blocked';failure_code?:string;[key:string]:string|number|undefined;}
const unavailable = (failure_code:string):PolicyReport => ({ status: 'unavailable', failure_code });

export function inspectPolicy(changesPayload:unknown, evidencePayload:unknown):PolicyReport {
  try {
    const comparison = inspectComparison(changesPayload, evidencePayload);
    if (comparison.status !== 'available') return comparison;
    const changes = comparison.changes;

    let added = 0;
    let removed = 0;
    let forbidden = 0;
    let unknown = 0;
    for (const change of changes) {
      if (change === null || typeof change !== 'object' || Array.isArray(change) ||
          !['added', 'removed'].includes(change.change_type as string) ||
          typeof change.manifest !== 'string' || !change.manifest ||
          change.manifest.length > 4096) {
        return unavailable('invalid-license-evidence');
      }
      if (change.change_type === 'removed') {
        removed++;
        continue;
      }
      if (['name', 'version', 'package_url'].some(key =>
        typeof change[key] !== 'string' || !change[key] || (change[key] as string).length > 4096)) {
        return unavailable('invalid-license-evidence');
      }
      added++;
      const verifiedAction = change.ecosystem === 'actions'
        ? VERIFIED_ACTION_LICENSES.get(change.package_url as string)
        : undefined;
      const license = change.license === null &&
        verifiedAction?.name === change.name && verifiedAction!.version === change.version
        ? verifiedAction!.license
        : change.license;
      if (typeof license !== 'string' || !license || license.length > 4096 ||
          license === 'NOASSERTION') {
        unknown++;
        continue;
      }
      try {
        const expression = license.replace(/(?<![\w-])OTHER(?![\w-])/g,
          'LicenseRef-clearlydefined-OTHER');
        parseSpdx(expression);
        if (satisfiesAny(expression, DENIED)) forbidden++;
      } catch {
        unknown++;
      }
    }
    const counts = {
      added, removed, forbidden, unknown,
      raw_changes: comparison.raw_changes, distinct_changes: changes.length,
      independent_raw_changes: comparison.independent_raw_changes,
    };
    if (forbidden) return { status: 'blocked', failure_code: 'license-policy-denied', ...counts };
    if (unknown) return { ...unavailable('license-evidence-unavailable'), ...counts };
    return { status: 'available', ...counts };
  } catch {
    return unavailable('invalid-license-evidence');
  }
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const [changesPath, evidencePath, reportPath] = process.argv.slice(2);
  if (!changesPath || !evidencePath || !reportPath || process.argv.length !== 5) {
    console.error('::error::License policy check requires changes, evidence and report paths.');
    process.exitCode = 2;
  } else {
    let report;
    try {
      report = inspectPolicy(readFileSync(changesPath, 'utf8'),
        readFileSync(evidencePath, 'utf8'));
    } catch {
      report = unavailable('invalid-license-evidence');
    }
    try {
      writeFileSync(reportPath, `${JSON.stringify(report, null, 2)}\n`, 'utf8');
    } catch {
      report = unavailable('invalid-license-evidence');
    }
    if (report.status === 'available') {
      console.log('Dependency license policy available for the reviewed changes.');
    } else {
      const diagnostic:Record<string,string|number> = {
        status: report.status === 'blocked' ? 'blocked' : 'unavailable',
        failure_code: ['license-policy-denied', 'license-evidence-unavailable',
          'incomplete-dependency-comparison', 'invalid-license-evidence'].includes(report.failure_code!)
          ? report.failure_code! : 'invalid-license-evidence',
      };
      for (const key of ['added', 'removed', 'forbidden', 'unknown',
        'raw_changes', 'distinct_changes', 'independent_raw_changes']) {
        if (Number.isSafeInteger(report[key]) && (report[key] as number) >= 0) diagnostic[key] = report[key]!;
      }
      console.log(`::error::Dependency license policy failed: ${JSON.stringify(diagnostic)}`);
    }
    process.exitCode = report.status === 'available' ? 0 : 1;
  }
}
