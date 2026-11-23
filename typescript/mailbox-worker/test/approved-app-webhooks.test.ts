import assert from 'node:assert/strict';
import { test } from 'node:test';
import { latestSchemaStatements } from '../scripts/latest-schema.ts';
import { approvedAppWebhooksStatements, appWebhooksChange } from '../scripts/approved-app-webhooks.ts';
import { approvedOwnerGrantsStatements, ownerGrantsChange } from '../scripts/approved-owner-grants.ts';
import { approvedPlan } from '../scripts/approved-schema-change.ts';

const [table, index] = approvedAppWebhooksStatements;
const [grantsTable, grantsIndex] = approvedOwnerGrantsStatements;

test('the pinned approved statements are exactly what src/schema.ts generates for app_webhooks', async () => {
    const generated = (await latestSchemaStatements()).filter(sql => /[`"]app_webhooks[`"]/.test(sql.split('\n')[0]));
    assert.deepEqual(generated, [...approvedAppWebhooksStatements]);
});

test('the app_webhooks opt-in accepts both statements, or only the index, and returns the approved text', () => {
    assert.deepEqual(appWebhooksChange.plan([]), []);
    assert.deepEqual(appWebhooksChange.plan([table, index]), [table, index]);
    assert.deepEqual(appWebhooksChange.plan([index.toLowerCase()]), [index]);
});

for (const [name, statements] of [
    ['the owner_grants table', [grantsTable]],
    ['the index before the table', [index, table]],
    ['a plain-http callback check', [table.replace("'https://'", "'http://'"), index]],
    ['a looser secret length', [table.replace('BETWEEN 29 AND 512', 'BETWEEN 1 AND 512'), index]],
    ['a unique index', [table, index.replace('CREATE INDEX', 'CREATE UNIQUE INDEX')]],
    ['a cascading foreign key', [table.replace('ON DELETE restrict', 'ON DELETE cascade'), index]]
] as [string, string[]][]) test('the app_webhooks opt-in refuses ' + name, () => {
    assert.throws(() => appWebhooksChange.plan(statements), /approved app_webhooks schema \(table and index\)/);
});

test('combined with owner_grants: each statement must belong to an enabled change', () => {
    const both = [ownerGrantsChange, appWebhooksChange];
    assert.deepEqual(approvedPlan([grantsTable, table, grantsIndex, index], both), [grantsTable, table, grantsIndex, index]);
    assert.throws(() => approvedPlan([grantsTable, grantsIndex, table, index], [appWebhooksChange]), /approved app_webhooks schema/);
    assert.throws(() => approvedPlan([table, index], [ownerGrantsChange]), /approved owner_grants schema/);
    assert.throws(() => approvedPlan([table], []), /empty schema plan/);
});

test('the app_webhooks opt-in value and workflow gate follow the same rules as the other approved changes', () => {
    for (const value of [undefined, '', 'false']) assert.equal(appWebhooksChange.optIn(value), false);
    assert.equal(appWebhooksChange.optIn('true'), true);
    for (const value of ['TRUE', '1', 'yes']) assert.throws(() => appWebhooksChange.optIn(value), /Invalid app_webhooks schema opt-in/);
    const approved = {eventName: 'workflow_dispatch', ref: 'refs/heads/main', configurationScope: 'production', firstPartyOAuth: 'false', enabled: 'true'};
    assert.equal(appWebhooksChange.validateRelease(approved), true);
    assert.equal(appWebhooksChange.validateRelease({...approved, enabled: 'false'}), false);
    for (const fields of [{eventName: 'issue_comment'}, {ref: 'refs/heads/feature'}, {configurationScope: 'repository'}, {firstPartyOAuth: 'true'}])
        assert.throws(() => appWebhooksChange.validateRelease({...approved, ...fields}), /app_webhooks schema opt-in requires a manual main dispatch/);
});
