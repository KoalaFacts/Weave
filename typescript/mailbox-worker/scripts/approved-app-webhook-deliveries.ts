/** The `app_webhook_deliveries` table and its `next_attempt_at` index (approved by the owner on 2026-11-23 for the push
 * webhook, W2), as one approved additive schema change (approved-schema-change.ts). Applying it stays the owner's
 * per-run opt-in. It references `app_webhooks`, so a release applies it with or after `apply_app_webhooks_schema`.
 */
import {resolve} from 'node:path';
import {pathToFileURL} from 'node:url';
import {approvedSchemaChange, runReleaseGate} from './approved-schema-change.ts';

// The reviewed statements, byte for byte as `src/schema.ts` generates them; a test pins the generator to these.
export const approvedAppWebhookDeliveriesStatements = [
    'CREATE TABLE `app_webhook_deliveries` (\n' +
    '\t`event_id` text PRIMARY KEY NOT NULL,\n' +
    '\t`webhook_id` text NOT NULL,\n' +
    '\t`action_id` text NOT NULL,\n' +
    '\t`status` text NOT NULL,\n' +
    '\t`occurred_at` integer NOT NULL,\n' +
    '\t`attempts` integer DEFAULT 0 NOT NULL,\n' +
    '\t`next_attempt_at` integer,\n' +
    '\t`delivered_at` integer,\n' +
    '\tFOREIGN KEY (`webhook_id`) REFERENCES `app_webhooks`(`webhook_id`) ON UPDATE no action ON DELETE restrict,\n' +
    '\tCONSTRAINT "app_webhook_deliveries_status" CHECK(status IN (\'pending\',\'approved\',\'rejected\',\'dispatching\',\'completed\',\'failed\',\'unknown\')),\n' +
    '\tCONSTRAINT "app_webhook_deliveries_attempts" CHECK(attempts BETWEEN 0 AND 100),\n' +
    '\tCONSTRAINT "app_webhook_deliveries_finish" CHECK(delivered_at IS NULL OR (next_attempt_at IS NULL AND delivered_at>=occurred_at))\n' +
    ');\n',
    'CREATE INDEX "app_webhook_deliveries_due" ON "app_webhook_deliveries" ("next_attempt_at");'
] as const;
export const appWebhookDeliveriesChange = approvedSchemaChange('app_webhook_deliveries', 'app_webhook_deliveries_due', approvedAppWebhookDeliveriesStatements);

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href)
    runReleaseGate(appWebhookDeliveriesChange, 'WEAVE_RELEASE_APP_WEBHOOK_DELIVERIES_SCHEMA');
