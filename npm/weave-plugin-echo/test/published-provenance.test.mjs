import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import test from 'node:test';
import { checkPublishedProvenance } from '../scripts/check-published-provenance.mjs';

const packageName = '@koalafacts/weave-plugin-echo';
const version = '0.1.1';
const sourceSha = 'a'.repeat(40);

function evidence() {
    const digest = createHash('sha512').update('published tarball').digest();
    const statement = {
        _type: 'https://in-toto.io/Statement/v1',
        subject: [{
            name: `pkg:npm/%40koalafacts/weave-plugin-echo@${version}`,
            digest: { sha512: digest.toString('hex') }
        }],
        predicate: {
            buildDefinition: {
                externalParameters: {
                    workflow: {
                        repository: 'https://github.com/KoalaFacts/Weave',
                        path: '.github/workflows/publish-npm-plugin-echo.yml',
                        ref: 'refs/heads/main'
                    }
                },
                resolvedDependencies: [{ digest: { gitCommit: sourceSha } }]
            }
        }
    };
    return {
        report: {
            invalid: [],
            missing: [],
            verified: [{
                name: packageName,
                version,
                attestations: { provenance: { predicateType: 'https://slsa.dev/provenance/v1' } },
                attestationBundles: [{
                    predicateType: 'https://slsa.dev/provenance/v1',
                    bundle: { dsseEnvelope: { payload: Buffer.from(JSON.stringify(statement)).toString('base64') } }
                }]
            }]
        },
        lock: {
            packages: {
                [`node_modules/${packageName}`]: {
                    version,
                    integrity: `sha512-${digest.toString('base64')}`
                }
            }
        }
    };
}

test('checkPublishedProvenance_ExactPublishedPackage_Accepts', () => {
    const { report, lock } = evidence();
    assert.doesNotThrow(() => checkPublishedProvenance(report, lock, version, sourceSha));
});

test('checkPublishedProvenance_MissingAttestation_Denies', () => {
    const { report, lock } = evidence();
    report.verified[0].attestationBundles = [];
    assert.throws(() => checkPublishedProvenance(report, lock, version, sourceSha));
});

test('checkPublishedProvenance_DifferentSourceCommit_Denies', () => {
    const { report, lock } = evidence();
    assert.throws(() => checkPublishedProvenance(report, lock, version, 'b'.repeat(40)));
});

test('checkPublishedProvenance_MissingSourceCommit_Denies', () => {
    const { report, lock } = evidence();
    assert.throws(() => checkPublishedProvenance(report, lock, version));
});

test('checkPublishedProvenance_DifferentTarball_Denies', () => {
    const { report, lock } = evidence();
    lock.packages[`node_modules/${packageName}`].integrity =
        `sha512-${createHash('sha512').update('other tarball').digest('base64')}`;
    assert.throws(() => checkPublishedProvenance(report, lock, version, sourceSha));
});

test('checkPublishedProvenance_DifferentRepository_Denies', () => {
    const { report, lock } = evidence();
    const envelope = report.verified[0].attestationBundles[0].bundle.dsseEnvelope;
    const statement = JSON.parse(Buffer.from(envelope.payload, 'base64').toString('utf8'));
    statement.predicate.buildDefinition.externalParameters.workflow.repository =
        'https://github.com/example/untrusted';
    envelope.payload = Buffer.from(JSON.stringify(statement)).toString('base64');
    assert.throws(() => checkPublishedProvenance(report, lock, version, sourceSha));
});

test('checkPublishedProvenance_InvalidSignature_Denies', () => {
    const { report, lock } = evidence();
    report.invalid.push({ name: packageName });
    assert.throws(() => checkPublishedProvenance(report, lock, version, sourceSha));
});
