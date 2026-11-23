import assert from 'node:assert/strict';
import { test } from 'node:test';
import { latestSchemaStatements } from '../scripts/latest-schema.ts';
import { approvedAppWebhookDeliveriesStatements, appWebhookDeliveriesChange } from '../scripts/approved-app-webhook-deliveries.ts';
import { approvedAppWebhooksStatements, appWebhooksChange } from '../scripts/approved-app-webhooks.ts';
import { approvedPlan } from '../scripts/approved-schema-change.ts';

const [table, index] = approvedAppWebhookDeliveriesStatements;
const [webhooksTable, webhooksIndex] = approvedAppWebhooksStatements;

test('the pinned approved statements are exactly what src/schema.ts generates for app_webhook_deliveries', async () => {
    const generated = (await latestSchemaStatements()).filter(sql => /[`"]app_webhook_deliveries[`"]/.test(sql.split('\n')[0]));
    assert.deepEqual(generated, [...approvedAppWebhookDeliveriesStatements]);
});

test('the app_webhook_deliveries opt-in accepts both statements, or only the index, and returns the approved text', () => {
    assert.deepEqual(appWebhookDeliveriesChange.plan([]), []);
    assert.deepEqual(appWebhookDeliveriesChange.plan([table, index]), [table, index]);
    assert.deepEqual(appWebhookDeliveriesChange.plan([index.toLowerCase()]), [index]);
});

for (const [name, statements] of [
    ['the app_webhooks table', [webhooksTable]],
    ['the index before the table', [index, table]],
    ['an unchecked status', [table.replace("'completed','failed','unknown'", "'completed','failed','unknown','sent'"), index]],
    ['an unbounded attempt count', [table.replace('attempts BETWEEN 0 AND 100', 'attempts>=0'), index]],
    ['a delivered row that is still due', [table.replace('next_attempt_at IS NULL AND ', ''), index]],
    ['a unique index', [table, index.replace('CREATE INDEX', 'CREATE UNIQUE INDEX')]],
    ['a cascading foreign key', [table.replace('ON DELETE restrict', 'ON DELETE cascade'), index]],
    ['a foreign key on the action', [table.replace('\tFOREIGN KEY', '\tFOREIGN KEY (`action_id`) REFERENCES `external_actions`(`action_id`) ON UPDATE no action ON DELETE restrict,\n\tFOREIGN KEY'), index]]
] as [string, string[]][]) test('the app_webhook_deliveries opt-in refuses ' + name, () => {
    assert.throws(() => appWebhookDeliveriesChange.plan(statements), /approved app_webhook_deliveries schema \(table and index\)/);
});

test('combined with app_webhooks: each statement must belong to an enabled change', () => {
    const both = [appWebhooksChange, appWebhookDeliveriesChange];
    assert.deepEqual(approvedPlan([webhooksTable, table, webhooksIndex, index], both), [webhooksTable, table, webhooksIndex, index]);
    assert.throws(() => approvedPlan([webhooksTable, webhooksIndex, table, index], [appWebhookDeliveriesChange]), /approved app_webhook_deliveries schema/);
    assert.throws(() => approvedPlan([table, index], [appWebhooksChange]), /approved app_webhooks schema/);
    assert.throws(() => approvedPlan([table], []), /empty schema plan/);
});

test('the app_webhook_deliveries opt-in value and workflow gate follow the same rules as the other approved changes', () => {
    for (const value of [undefined, '', 'false']) assert.equal(appWebhookDeliveriesChange.optIn(value), false);
    assert.equal(appWebhookDeliveriesChange.optIn('true'), true);
    for (const value of ['TRUE', '1', 'yes']) assert.throws(() => appWebhookDeliveriesChange.optIn(value), /Invalid app_webhook_deliveries schema opt-in/);
    const approved = {eventName: 'workflow_dispatch', ref: 'refs/heads/main', configurationScope: 'production', firstPartyOAuth: 'false', enabled: 'true'};
    assert.equal(appWebhookDeliveriesChange.validateRelease(approved), true);
    assert.equal(appWebhookDeliveriesChange.validateRelease({...approved, enabled: 'false'}), false);
    for (const fields of [{eventName: 'issue_comment'}, {ref: 'refs/heads/feature'}, {configurationScope: 'repository'}, {firstPartyOAuth: 'true'}])
        assert.throws(() => appWebhookDeliveriesChange.validateRelease({...approved, ...fields}), /app_webhook_deliveries schema opt-in requires a manual main dispatch/);
});
