import assert from 'node:assert/strict';
import { test } from 'node:test';
import { latestSchemaStatements } from '../scripts/latest-schema.ts';
import {
    approvedExternalActionKeysPlan, approvedExternalActionKeysStatements, externalActionKeysOptIn, normalizedDdl,
    validateExternalActionKeysRelease, type ExternalActionKeysReleaseIntent
} from '../scripts/approved-external-action-keys.ts';

const [table, index] = approvedExternalActionKeysStatements;

test('the pinned approved statements are exactly what src/schema.ts generates for external_action_keys', async () => {
    const generated = (await latestSchemaStatements()).filter(sql => /[`"]external_action_keys[`"]/.test(sql.split('\n')[0]));
    assert.deepEqual(generated, [...approvedExternalActionKeysStatements]);
});

test('normalized DDL ignores identifier quoting, keyword case and whitespace but keeps every term', () => {
    assert.equal(normalizedDdl(index), normalizedDdl('create unique index external_action_keys_action on `external_action_keys` ( action_id )'));
    assert.notEqual(normalizedDdl('a NOT NULL'), normalizedDdl('a NOTNULL'));
    assert.notEqual(normalizedDdl("CHECK(x GLOB '*[^a]*')"), normalizedDdl("CHECK(x GLOB '*[^A]*')"), 'string literals keep their case');
    assert.notEqual(normalizedDdl(table), normalizedDdl(table.replace(');\n', ') -- note\n;')), 'comments never match');
});

test('the opt-in accepts both approved statements, or only the index, and returns the approved text', () => {
    assert.deepEqual(approvedExternalActionKeysPlan([]), []);
    assert.deepEqual(approvedExternalActionKeysPlan([table, index]), [table, index]);
    assert.deepEqual(approvedExternalActionKeysPlan([index.toLowerCase()]), [index]);
});

for (const [name, statements] of [
    ['only another statement', ['CREATE INDEX "messages_identity" ON "messages" ("x");']],
    ['an extra statement', [table, index, 'CREATE TABLE "unrelated" (id integer);']],
    ['the table twice', [table, table]],
    ['the index before the table', [index, table]],
    ['a renamed column', [table.replace('request_hash` text', 'unapproved_hash` text'), index]],
    ['CHECK(1) under the approved constraint names', [table.replace(/CHECK\(length\(idempotency_key\)[^\n]*\)(?=,\n)/, 'CHECK(1)'), index]],
    ['an extra UNIQUE constraint', [table.replace('PRIMARY KEY(', 'UNIQUE(`created_at`),\n\tPRIMARY KEY('), index]],
    ['a default', [table.replace('`created_at` integer NOT NULL', '`created_at` integer DEFAULT 0 NOT NULL'), index]],
    ['a changed foreign key action', [table.replace('ON DELETE restrict,\n\tFOREIGN', 'ON DELETE cascade,\n\tFOREIGN'), index]],
    ['a non-unique index', [table, index.replace('UNIQUE ', '')]],
    ['another index column', [table, index.replace('("action_id")', '("action_id","created_at")')]],
    ['a partial index', [table, index.replace(';', ' WHERE created_at > 0;')]]
] as [string, string[]][]) test('the opt-in refuses ' + name, () => {
    assert.notDeepEqual(statements, [table, index]);
    assert.throws(() => approvedExternalActionKeysPlan(statements), /approved external_action_keys schema/);
});

test('the opt-in value is explicit: absent, empty or false is off, true is on, anything else fails', () => {
    for (const value of [undefined, '', 'false']) assert.equal(externalActionKeysOptIn(value), false);
    assert.equal(externalActionKeysOptIn('true'), true);
    for (const value of ['TRUE', '1', 'yes', ' true']) assert.throws(() => externalActionKeysOptIn(value), /opt-in/);
});

const approved: ExternalActionKeysReleaseIntent = {
    eventName: 'workflow_dispatch', ref: 'refs/heads/main', configurationScope: 'production',
    firstPartyOAuth: 'false', enabled: 'true'
};
test('the workflow gate allows the opt-in only on a manual main dispatch into protected production', () => {
    assert.equal(validateExternalActionKeysRelease(approved), true);
    for (const enabled of ['', 'false']) {
        assert.equal(validateExternalActionKeysRelease({...approved, enabled}), false);
        assert.equal(validateExternalActionKeysRelease({...approved, enabled, eventName: 'issue_comment'}), false);
    }
    for (const fields of [
        {eventName: 'issue_comment'}, {eventName: 'push', ref: 'refs/heads/publish/demo'}, {ref: 'refs/heads/feature'},
        {configurationScope: 'repository'}, {firstPartyOAuth: 'true'}, {firstPartyOAuth: ''}
    ]) assert.throws(() => validateExternalActionKeysRelease({...approved, ...fields}), /manual main dispatch into protected production/);
    assert.throws(() => validateExternalActionKeysRelease({...approved, enabled: 'yes'}), /opt-in/);
});
