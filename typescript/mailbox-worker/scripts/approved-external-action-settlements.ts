/** The `external_action_settlements` table and its `settled_by` index (approved by the owner on 2026-11-25, option 1 on
 * #560: the owner records on Weave's own action page what they found on the platform for their own `unknown` action, at
 * most once), as one approved additive schema change (approved-schema-change.ts). Applying it stays a per-run opt-in on a
 * manual production dispatch. It references only `external_actions` and `humans`, which every release already has.
 */
import {resolve} from 'node:path';
import {pathToFileURL} from 'node:url';
import {approvedSchemaChange, runReleaseGate} from './approved-schema-change.ts';

// The reviewed statements, byte for byte as `src/schema.ts` generates them; a test pins the generator to these.
export const approvedExternalActionSettlementsStatements = [
    'CREATE TABLE `external_action_settlements` (\n' +
    '\t`action_id` text PRIMARY KEY NOT NULL,\n' +
    '\t`finding` text NOT NULL,\n' +
    '\t`post_url` text,\n' +
    '\t`settled_by` text NOT NULL,\n' +
    '\t`settled_at` integer NOT NULL,\n' +
    '\tFOREIGN KEY (`action_id`) REFERENCES `external_actions`(`action_id`) ON UPDATE no action ON DELETE restrict,\n' +
    '\tFOREIGN KEY (`settled_by`) REFERENCES `humans`(`human_id`) ON UPDATE no action ON DELETE restrict,\n' +
    '\tCONSTRAINT "external_action_settlements_finding" CHECK(finding IN (\'posted\',\'not_posted\')),\n' +
    '\tCONSTRAINT "external_action_settlements_post_url" CHECK(post_url IS NULL OR (finding=\'posted\' AND post_url LIKE \'https://%\' AND length(post_url)<=2048))\n' +
    ');\n',
    'CREATE INDEX "external_action_settlements_human" ON "external_action_settlements" ("settled_by");'
] as const;
export const externalActionSettlementsChange = approvedSchemaChange('external_action_settlements', 'external_action_settlements_human', approvedExternalActionSettlementsStatements);

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href)
    runReleaseGate(externalActionSettlementsChange, 'WEAVE_RELEASE_EXTERNAL_ACTION_SETTLEMENTS_SCHEMA');
