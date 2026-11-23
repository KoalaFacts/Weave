/** The `owner_grant_decisions` table and its `grant_id` index (approved by the owner on 2026-11-23 for A1, an app that
 * approves or rejects on the owner's behalf: every decision records the grant that made it), as one approved additive
 * schema change (approved-schema-change.ts). Applying it stays the owner's per-run opt-in. It references `owner_grants`,
 * so a release applies it with or after `apply_owner_grants_schema`.
 */
import {resolve} from 'node:path';
import {pathToFileURL} from 'node:url';
import {approvedSchemaChange, runReleaseGate} from './approved-schema-change.ts';

// The reviewed statements, byte for byte as `src/schema.ts` generates them; a test pins the generator to these.
export const approvedOwnerGrantDecisionsStatements = [
    'CREATE TABLE `owner_grant_decisions` (\n' +
    '\t`action_id` text NOT NULL,\n' +
    '\t`decision` text NOT NULL,\n' +
    '\t`grant_id` text NOT NULL,\n' +
    '\t`decided_at` integer NOT NULL,\n' +
    '\tPRIMARY KEY(`action_id`, `decision`),\n' +
    '\tFOREIGN KEY (`action_id`) REFERENCES `external_actions`(`action_id`) ON UPDATE no action ON DELETE restrict,\n' +
    '\tFOREIGN KEY (`grant_id`) REFERENCES `owner_grants`(`grant_id`) ON UPDATE no action ON DELETE restrict,\n' +
    '\tCONSTRAINT "owner_grant_decisions_decision" CHECK(decision IN (\'approved\',\'rejected\'))\n' +
    ');\n',
    'CREATE INDEX "owner_grant_decisions_grant" ON "owner_grant_decisions" ("grant_id");'
] as const;
export const ownerGrantDecisionsChange = approvedSchemaChange('owner_grant_decisions', 'owner_grant_decisions_grant', approvedOwnerGrantDecisionsStatements);

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href)
    runReleaseGate(ownerGrantDecisionsChange, 'WEAVE_RELEASE_OWNER_GRANT_DECISIONS_SCHEMA');
