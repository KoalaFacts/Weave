import assert from 'node:assert/strict';
import { test } from 'node:test';
import { latestSchemaStatements } from '../scripts/latest-schema.ts';
import { approvedOwnerGrantApprovalsStatements, ownerGrantApprovalsChange } from '../scripts/approved-owner-grant-approvals.ts';
import { approvedOwnerGrantsStatements, ownerGrantsChange } from '../scripts/approved-owner-grants.ts';
import { approvedPlan } from '../scripts/approved-schema-change.ts';

const [table, index] = approvedOwnerGrantApprovalsStatements;
const [grantsTable, grantsIndex] = approvedOwnerGrantsStatements;

test('the pinned approved statements are exactly what src/schema.ts generates for owner_grant_approvals', async () => {
    const generated = (await latestSchemaStatements()).filter(sql => /[`"]owner_grant_approvals[`"]/.test(sql.split('\n')[0]));
    assert.deepEqual(generated, [...approvedOwnerGrantApprovalsStatements]);
});

test('the owner_grant_approvals opt-in accepts both statements, or only the index, and returns the approved text', () => {
    assert.deepEqual(ownerGrantApprovalsChange.plan([]), []);
    assert.deepEqual(ownerGrantApprovalsChange.plan([table, index]), [table, index]);
    assert.deepEqual(ownerGrantApprovalsChange.plan([index.toLowerCase()]), [index]);
});

for (const [name, statements] of [
    ['the owner_grants table', [grantsTable]],
    ['the index before the table', [index, table]],
    ['a row that is on by default for every grant', [table.replace('`created_at` integer NOT NULL', '`created_at` integer DEFAULT 0 NOT NULL'), index]],
    ['a revocation before the permission was given', [table.replace('revoked_at>=created_at', 'revoked_at>=0'), index]],
    ['a missing human', [table.replace('`human_id` text NOT NULL', '`human_id` text'), index]],
    ['a unique index', [table, index.replace('CREATE INDEX', 'CREATE UNIQUE INDEX')]],
    ['a cascading foreign key on the grant', [table.replace('(`grant_id`) ON UPDATE no action ON DELETE restrict', '(`grant_id`) ON UPDATE no action ON DELETE cascade'), index]],
    ['a cascading foreign key on the human', [table.replace('(`human_id`) ON UPDATE no action ON DELETE restrict', '(`human_id`) ON UPDATE no action ON DELETE cascade'), index]]
] as [string, string[]][]) test('the owner_grant_approvals opt-in refuses ' + name, () => {
    assert.throws(() => ownerGrantApprovalsChange.plan(statements), /approved owner_grant_approvals schema \(table and index\)/);
});

test('combined with owner_grants: each statement must belong to an enabled change', () => {
    const both = [ownerGrantsChange, ownerGrantApprovalsChange];
    assert.deepEqual(approvedPlan([grantsTable, table, grantsIndex, index], both), [grantsTable, table, grantsIndex, index]);
    assert.throws(() => approvedPlan([grantsTable, grantsIndex, table, index], [ownerGrantApprovalsChange]), /approved owner_grant_approvals schema/);
    assert.throws(() => approvedPlan([table, index], [ownerGrantsChange]), /approved owner_grants schema/);
    assert.throws(() => approvedPlan([table], []), /empty schema plan/);
});

test('the owner_grant_approvals opt-in value and workflow gate follow the same rules as the other approved changes', () => {
    for (const value of [undefined, '', 'false']) assert.equal(ownerGrantApprovalsChange.optIn(value), false);
    assert.equal(ownerGrantApprovalsChange.optIn('true'), true);
    for (const value of ['TRUE', '1', 'yes']) assert.throws(() => ownerGrantApprovalsChange.optIn(value), /Invalid owner_grant_approvals schema opt-in/);
    const approved = {eventName: 'workflow_dispatch', ref: 'refs/heads/main', configurationScope: 'production', firstPartyOAuth: 'false', enabled: 'true'};
    assert.equal(ownerGrantApprovalsChange.validateRelease(approved), true);
    assert.equal(ownerGrantApprovalsChange.validateRelease({...approved, enabled: 'false'}), false);
    for (const fields of [{eventName: 'issue_comment'}, {ref: 'refs/heads/feature'}, {configurationScope: 'repository'}, {firstPartyOAuth: 'true'}])
        assert.throws(() => ownerGrantApprovalsChange.validateRelease({...approved, ...fields}), /owner_grant_approvals schema opt-in requires a manual main dispatch/);
});
