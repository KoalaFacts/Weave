/** `schema-366`, the `external_action_keys` table and its unique `action_id` index (approved 2026-11-11; applying it to
 * production authorized 2026-11-12), as one approved additive schema change (approved-schema-change.ts).
 */
import {resolve} from 'node:path';
import {pathToFileURL} from 'node:url';
import {approvedSchemaChange, runReleaseGate, type ReleaseIntent} from './approved-schema-change.ts';
export {liveSchemaSql, normalizedDdl} from './approved-schema-change.ts';

// The reviewed statements, byte for byte as `src/schema.ts` generates them. A test pins the generator to these, so a
// schema edit cannot silently widen what the opt-in applies.
export const approvedExternalActionKeysStatements = [
    'CREATE TABLE `external_action_keys` (\n' +
    '\t`account_id` text NOT NULL,\n' +
    '\t`idempotency_key` text NOT NULL,\n' +
    '\t`request_hash` text NOT NULL,\n' +
    '\t`action_id` text NOT NULL,\n' +
    '\t`created_at` integer NOT NULL,\n' +
    '\tPRIMARY KEY(`account_id`, `idempotency_key`),\n' +
    '\tFOREIGN KEY (`account_id`) REFERENCES `accounts`(`account_id`) ON UPDATE no action ON DELETE restrict,\n' +
    '\tFOREIGN KEY (`action_id`) REFERENCES `external_actions`(`action_id`) ON UPDATE no action ON DELETE restrict,\n' +
    '\tCONSTRAINT "external_action_keys_key" CHECK(length(idempotency_key) BETWEEN 1 AND 128 AND idempotency_key NOT GLOB \'*[^A-Za-z0-9._:-]*\'),\n' +
    '\tCONSTRAINT "external_action_keys_hash" CHECK(length(request_hash)=64 AND request_hash NOT GLOB \'*[^0-9a-f]*\')\n' +
    ');\n',
    'CREATE UNIQUE INDEX "external_action_keys_action" ON "external_action_keys" ("action_id");'
] as const;
export const externalActionKeysChange = approvedSchemaChange('external_action_keys', 'external_action_keys_action', approvedExternalActionKeysStatements);
export const liveTriggerSql = externalActionKeysChange.liveTriggerSql;
export const externalActionKeysOptIn = externalActionKeysChange.optIn;
export const approvedExternalActionKeysPlan = externalActionKeysChange.plan;
export const assertLiveExternalActionKeys = externalActionKeysChange.assertLive;
export type ExternalActionKeysReleaseIntent = ReleaseIntent;
export const validateExternalActionKeysRelease = externalActionKeysChange.validateRelease;

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href)
    runReleaseGate(externalActionKeysChange, 'WEAVE_RELEASE_EXTERNAL_ACTION_KEYS_SCHEMA');
