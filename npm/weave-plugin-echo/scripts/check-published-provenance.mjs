import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const packageName = '@koalafacts/weave-plugin-echo';
const provenanceType = 'https://slsa.dev/provenance/v1';

export function checkPublishedProvenance(report, lock, version, sourceSha = '') {
    assert.match(version, /^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/);
    assert.deepEqual(report.invalid, []);
    assert.deepEqual(report.missing, []);
    assert.ok(Array.isArray(report.verified));

    const installed = lock.packages?.[`node_modules/${packageName}`];
    assert.equal(installed?.version, version);
    assert.match(installed.integrity, /^sha512-[A-Za-z0-9+/]+={0,2}$/);
    const installedDigest = Buffer.from(installed.integrity.slice('sha512-'.length), 'base64').toString('hex');

    const verified = report.verified.find(entry => entry.name === packageName && entry.version === version);
    assert.ok(verified, 'The installed Echo package must have a verified signature');
    assert.equal(verified.attestations?.provenance?.predicateType, provenanceType);
    const provenance = verified.attestationBundles?.find(entry => entry.predicateType === provenanceType);
    assert.ok(provenance?.bundle?.dsseEnvelope?.payload, 'The verified provenance bundle is required');
    const statement = JSON.parse(Buffer.from(provenance.bundle.dsseEnvelope.payload, 'base64').toString('utf8'));
    assert.equal(statement._type, 'https://in-toto.io/Statement/v1');
    assert.ok(statement.subject?.some(subject =>
        subject.name === `pkg:npm/%40koalafacts/weave-plugin-echo@${version}` &&
        subject.digest?.sha512 === installedDigest), 'Provenance must bind the installed tarball');

    const workflow = statement.predicate?.buildDefinition?.externalParameters?.workflow;
    assert.equal(workflow?.repository, 'https://github.com/KoalaFacts/Weave');
    assert.equal(workflow?.path, '.github/workflows/publish-npm-plugin-echo.yml');
    assert.equal(workflow?.ref, 'refs/heads/main');
    if (sourceSha) {
        assert.match(sourceSha, /^[0-9a-f]{40}$/);
        assert.ok(statement.predicate?.buildDefinition?.resolvedDependencies?.some(dependency =>
            dependency.digest?.gitCommit === sourceSha), 'Provenance must name the triggering release commit');
    }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    try {
        const [reportPath, lockPath, version, sourceSha = ''] = process.argv.slice(2);
        if (!reportPath || !lockPath || !version)
            throw new Error('Usage: check-published-provenance.mjs REPORT LOCK VERSION [SOURCE_SHA]');
        const report = JSON.parse(readFileSync(reportPath, 'utf8'));
        const lock = JSON.parse(readFileSync(lockPath, 'utf8'));
        checkPublishedProvenance(report, lock, version, sourceSha);
        process.stdout.write(`Verified published ${packageName}@${version} provenance.\n`);
    } catch (error) {
        process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
        process.exitCode = 1;
    }
}
