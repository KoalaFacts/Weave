const MAX_BYTES = 2 * 1024 * 1024;
const MAX_CHANGES = 2000;
const IDENTITY_FIELDS = ['change_type', 'manifest', 'ecosystem', 'name', 'version', 'package_url'];
const unavailable = failure_code => ({ status: 'unavailable', failure_code });

function canonical(value) {
  if (value === null || typeof value === 'string' || typeof value === 'boolean') {
    return JSON.stringify(value);
  }
  if (typeof value === 'number' && Number.isFinite(value) &&
      (!Number.isInteger(value) || Number.isSafeInteger(value))) {
    return Object.is(value, -0) ? '-0' : JSON.stringify(value);
  }
  if (Array.isArray(value)) return `[${value.map(canonical).join(',')}]`;
  if (typeof value === 'object' && value !== null) {
    return `{${Object.keys(value).sort().map(key =>
      `${JSON.stringify(key)}:${canonical(value[key])}`).join(',')}}`;
  }
  throw new TypeError('Invalid comparison value');
}

function distinctRecords(records) {
  const identities = new Map();
  const distinct = new Map();
  for (const record of records) {
    if (record === null || typeof record !== 'object' || Array.isArray(record) ||
        !['added', 'removed'].includes(record.change_type) ||
        typeof record.manifest !== 'string' || !record.manifest || record.manifest.length > 4096) {
      throw new TypeError('Invalid comparison record');
    }
    const identity = canonical(IDENTITY_FIELDS.map(key =>
      [key, Object.hasOwn(record, key), Object.hasOwn(record, key) ? record[key] : null]));
    const complete = canonical(record);
    if (identities.has(identity) && identities.get(identity) !== complete) return undefined;
    identities.set(identity, complete);
    distinct.set(complete, record);
  }
  return distinct;
}

export function inspectComparison(changesPayload, evidencePayload) {
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
        !Array.isArray(evidence.dependency_records) || evidence.dependency_records.length > MAX_CHANGES ||
        !Number.isSafeInteger(evidence.dependency_changes) ||
        evidence.dependency_changes !== evidence.dependency_records.length) {
      return unavailable('incomplete-dependency-comparison');
    }
    // Preserve every field/value; sorting key names never assigns untrusted object properties.
    canonical(evidence);
    const actual = distinctRecords(changes);
    const expected = distinctRecords(evidence.dependency_records);
    if (!actual || !expected || actual.size !== expected.size ||
        [...expected.keys()].some(record => !actual.has(record))) {
      return unavailable('incomplete-dependency-comparison');
    }
    return {
      status: 'available', changes: [...actual.values()],
      raw_changes: changes.length, independent_raw_changes: evidence.dependency_changes,
    };
  } catch {
    return unavailable('invalid-license-evidence');
  }
}
