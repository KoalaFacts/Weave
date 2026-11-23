import assert from 'node:assert/strict';
import { test } from 'node:test';
import { latestSchemaStatements } from '../scripts/latest-schema.ts';
import { approvedOwnerGrantDecisionsStatements, ownerGrantDecisionsChange } from '../scripts/approved-owner-grant-decisions.ts';
import { approvedOwnerGrantsStatements, ownerGrantsChange } from '../scripts/approved-owner-grants.ts';
import { approvedPlan } from '../scripts/approved-schema-change.ts';

const [table, index] = approvedOwnerGrantDecisionsStatements;
const [grantsTable, grantsIndex] = approvedOwnerGrantsStatements;

test('the pinned approved statements are exactly what src/schema.ts generates for owner_grant_decisions', async () => {
    const generated = (await latestSchemaStatements()).filter(sql => /[`"]owner_grant_decisions[`"]/.test(sql.split('\n')[0]));
    assert.deepEqual(generated, [...approvedOwnerGrantDecisionsStatements]);
});

test('the owner_grant_decisions opt-in accepts both statements, or only the index, and returns the approved text', () => {
    assert.deepEqual(ownerGrantDecisionsChange.plan([]), []);
    assert.deepEqual(ownerGrantDecisionsChange.plan([table, index]), [table, index]);
    assert.deepEqual(ownerGrantDecisionsChange.plan([index.toLowerCase()]), [index]);
});

for (const [name, statements] of [
    ['the owner_grants table', [grantsTable]],
    ['the index before the table', [index, table]],
    ['a decision time filled in by default', [table.replace('`decided_at` integer NOT NULL', '`decided_at` integer DEFAULT 0 NOT NULL'), index]],
    ['a missing grant', [table.replace('`grant_id` text NOT NULL', '`grant_id` text'), index]],
    ['one decision per action only', [table.replace('PRIMARY KEY(`action_id`, `decision`)', 'PRIMARY KEY(`action_id`)'), index]],
    ['any decision value', [table.replace("CHECK(decision IN ('approved','rejected'))", 'CHECK(decision IS NOT NULL)'), index]],
    ['a unique index', [table, index.replace('CREATE INDEX', 'CREATE UNIQUE INDEX')]],
    ['a cascading foreign key on the action', [table.replace('(`action_id`) ON UPDATE no action ON DELETE restrict', '(`action_id`) ON UPDATE no action ON DELETE cascade'), index]],
    ['a no-action foreign key on the action', [table.replace('(`action_id`) ON UPDATE no action ON DELETE restrict', '(`action_id`) ON UPDATE no action ON DELETE no action'), index]],
    ['a no-action foreign key on the grant', [table.replace('(`grant_id`) ON UPDATE no action ON DELETE restrict', '(`grant_id`) ON UPDATE no action ON DELETE no action'), index]],
    ['a cascading foreign key on the grant', [table.replace('(`grant_id`) ON UPDATE no action ON DELETE restrict', '(`grant_id`) ON UPDATE no action ON DELETE cascade'), index]]
] as [string, string[]][]) test('the owner_grant_decisions opt-in refuses ' + name, () => {
    assert.throws(() => ownerGrantDecisionsChange.plan(statements), /approved owner_grant_decisions schema \(table and index\)/);
});

test('combined with owner_grants: each statement must belong to an enabled change', () => {
    const both = [ownerGrantsChange, ownerGrantDecisionsChange];
    assert.deepEqual(approvedPlan([grantsTable, table, grantsIndex, index], both), [grantsTable, table, grantsIndex, index]);
    assert.throws(() => approvedPlan([grantsTable, grantsIndex, table, index], [ownerGrantDecisionsChange]), /approved owner_grant_decisions schema/);
    assert.throws(() => approvedPlan([table, index], [ownerGrantsChange]), /approved owner_grants schema/);
    assert.throws(() => approvedPlan([table], []), /empty schema plan/);
});

test('the owner_grant_decisions opt-in value and workflow gate follow the same rules as the other approved changes', () => {
    for (const value of [undefined, '', 'false']) assert.equal(ownerGrantDecisionsChange.optIn(value), false);
    assert.equal(ownerGrantDecisionsChange.optIn('true'), true);
    for (const value of ['TRUE', '1', 'yes']) assert.throws(() => ownerGrantDecisionsChange.optIn(value), /Invalid owner_grant_decisions schema opt-in/);
    const approved = {eventName: 'workflow_dispatch', ref: 'refs/heads/main', configurationScope: 'production', firstPartyOAuth: 'false', enabled: 'true'};
    assert.equal(ownerGrantDecisionsChange.validateRelease(approved), true);
    assert.equal(ownerGrantDecisionsChange.validateRelease({...approved, enabled: 'false'}), false);
    for (const fields of [{eventName: 'issue_comment'}, {ref: 'refs/heads/feature'}, {configurationScope: 'repository'}, {firstPartyOAuth: 'true'}])
        assert.throws(() => ownerGrantDecisionsChange.validateRelease({...approved, ...fields}), /owner_grant_decisions schema opt-in requires a manual main dispatch/);
});
