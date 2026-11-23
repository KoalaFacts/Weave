/** The `app_webhooks` table and its `grant_id` index (approved by the owner on 2026-11-23 for the push webhook, W1), as
 * one approved additive schema change (approved-schema-change.ts). Applying it stays the owner's per-run opt-in. It
 * references `owner_grants`, so a release applies it with or after `apply_owner_grants_schema`.
 */
import {resolve} from 'node:path';
import {pathToFileURL} from 'node:url';
import {approvedSchemaChange, runReleaseGate} from './approved-schema-change.ts';

// The reviewed statements, byte for byte as `src/schema.ts` generates them; a test pins the generator to these.
export const approvedAppWebhooksStatements = [
    'CREATE TABLE `app_webhooks` (\n' +
    '\t`webhook_id` text PRIMARY KEY NOT NULL,\n' +
    '\t`grant_id` text NOT NULL,\n' +
    '\t`url` text NOT NULL,\n' +
    '\t`secret_sealed` blob NOT NULL,\n' +
    '\t`created_at` integer NOT NULL,\n' +
    '\t`revoked_at` integer,\n' +
    '\tFOREIGN KEY (`grant_id`) REFERENCES `owner_grants`(`grant_id`) ON UPDATE no action ON DELETE restrict,\n' +
    '\tCONSTRAINT "app_webhooks_url" CHECK(substr(url,1,8)=\'https://\' AND length(url) BETWEEN 9 AND 2048),\n' +
    '\tCONSTRAINT "app_webhooks_secret" CHECK(length(secret_sealed) BETWEEN 29 AND 512),\n' +
    '\tCONSTRAINT "app_webhooks_revocation" CHECK(revoked_at IS NULL OR revoked_at>=created_at)\n' +
    ');\n',
    'CREATE INDEX "app_webhooks_grant" ON "app_webhooks" ("grant_id");'
] as const;
export const appWebhooksChange = approvedSchemaChange('app_webhooks', 'app_webhooks_grant', approvedAppWebhooksStatements);

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href)
    runReleaseGate(appWebhooksChange, 'WEAVE_RELEASE_APP_WEBHOOKS_SCHEMA');
