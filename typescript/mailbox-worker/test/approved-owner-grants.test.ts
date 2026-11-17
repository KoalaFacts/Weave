import assert from 'node:assert/strict';
import { test } from 'node:test';
import { latestSchemaStatements } from '../scripts/latest-schema.ts';
import { approvedOwnerGrantsStatements, ownerGrantsChange } from '../scripts/approved-owner-grants.ts';
import { externalActionKeysChange, approvedExternalActionKeysStatements } from '../scripts/approved-external-action-keys.ts';
import { approvedPlan } from '../scripts/approved-schema-change.ts';

const [table, index] = approvedOwnerGrantsStatements;
const [keysTable, keysIndex] = approvedExternalActionKeysStatements;

test('the pinned approved statements are exactly what src/schema.ts generates for owner_grants', async () => {
    const generated = (await latestSchemaStatements()).filter(sql => /[`"]owner_grants[`"]/.test(sql.split('\n')[0]));
    assert.deepEqual(generated, [...approvedOwnerGrantsStatements]);
});

test('the owner_grants opt-in accepts both statements, or only the index, and returns the approved text', () => {
    assert.deepEqual(ownerGrantsChange.plan([]), []);
    assert.deepEqual(ownerGrantsChange.plan([table, index]), [table, index]);
    assert.deepEqual(ownerGrantsChange.plan([index.toLowerCase()]), [index]);
});

for (const [name, statements] of [
    ['the external_action_keys table', [keysTable]],
    ['the index before the table', [index, table]],
    ['a renamed column', [table.replace('client_name` text', 'unapproved_name` text'), index]],
    ['a unique index', [table, index.replace('CREATE INDEX', 'CREATE UNIQUE INDEX')]],
    ['a cascading foreign key', [table.replace('ON DELETE restrict', 'ON DELETE cascade'), index]]
] as [string, string[]][]) test('the owner_grants opt-in refuses ' + name, () => {
    assert.throws(() => ownerGrantsChange.plan(statements), /approved owner_grants schema \(table and index\)/);
});

test('combined plans: each statement must belong to an enabled change, in that change\'s order', () => {
    const both = [externalActionKeysChange, ownerGrantsChange];
    assert.deepEqual(approvedPlan([table, keysTable, index, keysIndex], both), [table, keysTable, index, keysIndex]);
    assert.deepEqual(approvedPlan([keysIndex.toLowerCase(), index], both), [keysIndex, index]);
    assert.throws(() => approvedPlan([table, index], [externalActionKeysChange]), /approved external_action_keys schema/);
    assert.throws(() => approvedPlan([keysTable, keysIndex], [ownerGrantsChange]), /approved owner_grants schema/);
    assert.throws(() => approvedPlan([index, table, keysTable, keysIndex], both), /approved owner_grants schema/);
    assert.throws(() => approvedPlan([table, index, 'CREATE TABLE "unrelated" (id integer);'], both), /approved external_action_keys schema/);
    assert.throws(() => approvedPlan([table], []), /empty schema plan/);
    assert.deepEqual(approvedPlan([], []), []);
});

test('the owner_grants opt-in value and workflow gate follow the same rules as external_action_keys', () => {
    for (const value of [undefined, '', 'false']) assert.equal(ownerGrantsChange.optIn(value), false);
    assert.equal(ownerGrantsChange.optIn('true'), true);
    for (const value of ['TRUE', '1', 'yes']) assert.throws(() => ownerGrantsChange.optIn(value), /Invalid owner_grants schema opt-in/);
    const approved = {eventName: 'workflow_dispatch', ref: 'refs/heads/main', configurationScope: 'production', firstPartyOAuth: 'false', enabled: 'true'};
    assert.equal(ownerGrantsChange.validateRelease(approved), true);
    assert.equal(ownerGrantsChange.validateRelease({...approved, enabled: 'false'}), false);
    for (const fields of [{eventName: 'issue_comment'}, {ref: 'refs/heads/feature'}, {configurationScope: 'repository'}, {firstPartyOAuth: 'true'}])
        assert.throws(() => ownerGrantsChange.validateRelease({...approved, ...fields}), /owner_grants schema opt-in requires a manual main dispatch/);
});
