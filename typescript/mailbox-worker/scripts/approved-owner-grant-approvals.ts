/** The `owner_grant_approvals` table and its `human_id` index (approved by the owner on 2026-11-23 for A1, an app that
 * approves or rejects on the owner's behalf), as one approved additive schema change (approved-schema-change.ts).
 * Applying it stays the owner's per-run opt-in. It references `owner_grants`, so a release applies it with or after
 * `apply_owner_grants_schema`.
 */
import {resolve} from 'node:path';
import {pathToFileURL} from 'node:url';
import {approvedSchemaChange, runReleaseGate} from './approved-schema-change.ts';

// The reviewed statements, byte for byte as `src/schema.ts` generates them; a test pins the generator to these.
export const approvedOwnerGrantApprovalsStatements = [
    'CREATE TABLE `owner_grant_approvals` (\n' +
    '\t`grant_id` text PRIMARY KEY NOT NULL,\n' +
    '\t`human_id` text NOT NULL,\n' +
    '\t`created_at` integer NOT NULL,\n' +
    '\t`revoked_at` integer,\n' +
    '\tFOREIGN KEY (`grant_id`) REFERENCES `owner_grants`(`grant_id`) ON UPDATE no action ON DELETE restrict,\n' +
    '\tFOREIGN KEY (`human_id`) REFERENCES `humans`(`human_id`) ON UPDATE no action ON DELETE restrict,\n' +
    '\tCONSTRAINT "owner_grant_approvals_revocation" CHECK(revoked_at IS NULL OR revoked_at>=created_at)\n' +
    ');\n',
    'CREATE INDEX "owner_grant_approvals_human" ON "owner_grant_approvals" ("human_id");'
] as const;
export const ownerGrantApprovalsChange = approvedSchemaChange('owner_grant_approvals', 'owner_grant_approvals_human', approvedOwnerGrantApprovalsStatements);

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href)
    runReleaseGate(ownerGrantApprovalsChange, 'WEAVE_RELEASE_OWNER_GRANT_APPROVALS_SCHEMA');
