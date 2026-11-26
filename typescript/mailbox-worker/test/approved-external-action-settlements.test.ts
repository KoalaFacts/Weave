import assert from 'node:assert/strict';
import { test } from 'node:test';
import { latestSchemaStatements } from '../scripts/latest-schema.ts';
import { approvedExternalActionSettlementsStatements, externalActionSettlementsChange } from '../scripts/approved-external-action-settlements.ts';
import { approvedOwnerGrantDecisionsStatements, ownerGrantDecisionsChange } from '../scripts/approved-owner-grant-decisions.ts';
import { approvedPlan } from '../scripts/approved-schema-change.ts';

const [table, index] = approvedExternalActionSettlementsStatements;
const [decisionsTable, decisionsIndex] = approvedOwnerGrantDecisionsStatements;

test('the pinned approved statements are exactly what src/schema.ts generates for external_action_settlements', async () => {
    const generated = (await latestSchemaStatements()).filter(sql => /[`"]external_action_settlements[`"]/.test(sql.split('\n')[0]));
    assert.deepEqual(generated, [...approvedExternalActionSettlementsStatements]);
});

test('the external_action_settlements opt-in accepts both statements, or only the index, and returns the approved text', () => {
    assert.deepEqual(externalActionSettlementsChange.plan([]), []);
    assert.deepEqual(externalActionSettlementsChange.plan([table, index]), [table, index]);
    assert.deepEqual(externalActionSettlementsChange.plan([index.toLowerCase()]), [index]);
});

for (const [name, statements] of [
    ['another approved table', [decisionsTable]],
    ['the index before the table', [index, table]],
    ['a settlement time filled in by default', [table.replace('`settled_at` integer NOT NULL', '`settled_at` integer DEFAULT 0 NOT NULL'), index]],
    ['a missing owner', [table.replace('`settled_by` text NOT NULL', '`settled_by` text'), index]],
    ['more than one settlement per action', [table.replace('`action_id` text PRIMARY KEY NOT NULL', '`action_id` text NOT NULL'), index]],
    ['any finding', [table.replace("CHECK(finding IN ('posted','not_posted'))", 'CHECK(finding IS NOT NULL)'), index]],
    ['a post link on "not posted"', [table.replace("finding='posted' AND ", ''), index]],
    ['a unique index', [table, index.replace('CREATE INDEX', 'CREATE UNIQUE INDEX')]],
    ['a cascading foreign key on the action', [table.replace('(`action_id`) ON UPDATE no action ON DELETE restrict', '(`action_id`) ON UPDATE no action ON DELETE cascade'), index]],
    ['a no-action foreign key on the owner', [table.replace('(`settled_by`) REFERENCES `humans`(`human_id`) ON UPDATE no action ON DELETE restrict', '(`settled_by`) REFERENCES `humans`(`human_id`) ON UPDATE no action ON DELETE no action'), index]]
] as [string, string[]][]) test('the external_action_settlements opt-in refuses ' + name, () => {
    assert.throws(() => externalActionSettlementsChange.plan(statements), /approved external_action_settlements schema \(table and index\)/);
});

test('combined with owner_grant_decisions: each statement must belong to an enabled change', () => {
    const both = [ownerGrantDecisionsChange, externalActionSettlementsChange];
    assert.deepEqual(approvedPlan([decisionsTable, table, decisionsIndex, index], both), [decisionsTable, table, decisionsIndex, index]);
    assert.throws(() => approvedPlan([decisionsTable, decisionsIndex, table, index], [externalActionSettlementsChange]), /approved external_action_settlements schema/);
    assert.throws(() => approvedPlan([table, index], [ownerGrantDecisionsChange]), /approved owner_grant_decisions schema/);
    assert.throws(() => approvedPlan([table], []), /empty schema plan/);
});

test('the external_action_settlements opt-in value and workflow gate follow the same rules as the other approved changes', () => {
    for (const value of [undefined, '', 'false']) assert.equal(externalActionSettlementsChange.optIn(value), false);
    assert.equal(externalActionSettlementsChange.optIn('true'), true);
    for (const value of ['TRUE', '1', 'yes']) assert.throws(() => externalActionSettlementsChange.optIn(value), /Invalid external_action_settlements schema opt-in/);
    const approved = {eventName: 'workflow_dispatch', ref: 'refs/heads/main', configurationScope: 'production', firstPartyOAuth: 'false', enabled: 'true'};
    assert.equal(externalActionSettlementsChange.validateRelease(approved), true);
    assert.equal(externalActionSettlementsChange.validateRelease({...approved, enabled: 'false'}), false);
    for (const fields of [{eventName: 'issue_comment'}, {ref: 'refs/heads/feature'}, {configurationScope: 'repository'}, {firstPartyOAuth: 'true'}])
        assert.throws(() => externalActionSettlementsChange.validateRelease({...approved, ...fields}), /external_action_settlements schema opt-in requires a manual main dispatch/);
});
