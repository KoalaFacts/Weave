/** The `owner_grants` table and its `human_id` index (approved by the owner on 2026-11-17 for owner app grants, AI-2), as
 * one approved additive schema change (approved-schema-change.ts). Applying it stays the owner's per-run opt-in.
 */
import {resolve} from 'node:path';
import {pathToFileURL} from 'node:url';
import {approvedSchemaChange, runReleaseGate} from './approved-schema-change.ts';

// The reviewed statements, byte for byte as `src/schema.ts` generates them; a test pins the generator to these.
export const approvedOwnerGrantsStatements = [
    'CREATE TABLE `owner_grants` (\n' +
    '\t`grant_id` text PRIMARY KEY NOT NULL,\n' +
    '\t`human_id` text NOT NULL,\n' +
    '\t`client_id` text NOT NULL,\n' +
    '\t`client_name` text,\n' +
    '\t`created_at` integer NOT NULL,\n' +
    '\t`revoked_at` integer,\n' +
    '\tFOREIGN KEY (`human_id`) REFERENCES `humans`(`human_id`) ON UPDATE no action ON DELETE restrict,\n' +
    '\tCONSTRAINT "owner_grants_client_id" CHECK(length(client_id) BETWEEN 1 AND 2048),\n' +
    '\tCONSTRAINT "owner_grants_client_name" CHECK(client_name IS NULL OR length(client_name) BETWEEN 1 AND 128),\n' +
    '\tCONSTRAINT "owner_grants_revocation" CHECK(revoked_at IS NULL OR revoked_at>=created_at)\n' +
    ');\n',
    'CREATE INDEX "owner_grants_human" ON "owner_grants" ("human_id");'
] as const;
export const ownerGrantsChange = approvedSchemaChange('owner_grants', 'owner_grants_human', approvedOwnerGrantsStatements);

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href)
    runReleaseGate(ownerGrantsChange, 'WEAVE_RELEASE_OWNER_GRANTS_SCHEMA');
