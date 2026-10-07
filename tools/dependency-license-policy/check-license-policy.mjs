import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { satisfiesAny } from '@onebeyond/spdx-license-satisfies';
import parseSpdx from 'spdx-expression-parse';

const MAX_BYTES = 2 * 1024 * 1024;
const MAX_CHANGES = 2000;
const DENIED = ['GPL-2.0', 'GPL-3.0', 'AGPL-3.0'];
// GitHub's dependency graph omits the license for this action. The MIT license
// was checked at the pinned source commit: https://github.com/actions/setup-node/blob/249970729cb0ef3589644e2896645e5dc5ba9c38/LICENSE
const VERIFIED_ACTION_LICENSES = new Map([
  ['pkg:githubactions/actions/setup-node@249970729cb0ef3589644e2896645e5dc5ba9c38', {
    name: 'actions/setup-node',
    version: '249970729cb0ef3589644e2896645e5dc5ba9c38',
    license: 'MIT',
  }],
]);

const unavailable = failure_code => ({ status: 'unavailable', failure_code });

export function inspectPolicy(changesPayload, evidencePayload) {
  try {
    if (typeof changesPayload !== 'string' || typeof evidencePayload !== 'string' ||
        Buffer.byteLength(changesPayload) > MAX_BYTES ||
        Buffer.byteLength(evidencePayload) > MAX_BYTES) {
      return unavailable('invalid-license-evidence');
    }
    const changes = JSON.parse(changesPayload);
    const evidence = JSON.parse(evidencePayload);
    if (!Array.isArray(changes) || changes.length > MAX_CHANGES ||
        evidence?.status !== 'available' ||
        !Number.isSafeInteger(evidence.dependency_changes) ||
        evidence.dependency_changes !== changes.length) {
      return unavailable('incomplete-dependency-comparison');
    }

    let added = 0;
    let removed = 0;
    let forbidden = 0;
    let unknown = 0;
    for (const change of changes) {
      if (change === null || typeof change !== 'object' || Array.isArray(change) ||
          !['added', 'removed'].includes(change.change_type) ||
          typeof change.manifest !== 'string' || !change.manifest ||
          change.manifest.length > 4096) {
        return unavailable('invalid-license-evidence');
      }
      if (change.change_type === 'removed') {
        removed++;
        continue;
      }
      if (['name', 'version', 'package_url'].some(key =>
        typeof change[key] !== 'string' || !change[key] || change[key].length > 4096)) {
        return unavailable('invalid-license-evidence');
      }
      added++;
      const verifiedAction = change.ecosystem === 'actions'
        ? VERIFIED_ACTION_LICENSES.get(change.package_url)
        : undefined;
      const license = change.license === null &&
        verifiedAction?.name === change.name && verifiedAction.version === change.version
        ? verifiedAction.license
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
    const counts = { added, removed, forbidden, unknown };
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
    console.log(report.status === 'available'
      ? 'Dependency license policy available for the reviewed changes.'
      : '::error::Dependency license policy is unavailable or denied.');
    process.exitCode = report.status === 'available' ? 0 : 1;
  }
}
