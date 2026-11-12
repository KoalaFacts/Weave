/** The one owner-approved additive schema change a completed-only release may apply: `schema-366`, the
 * `external_action_keys` table and its unique `action_id` index (approved 2026-11-11; applying it to production
 * authorized 2026-11-12). The opt-in is explicit per run and off by default. It grants nothing else: any other
 * pending statement, or an existing table or index that differs from the approved definition, stops the release.
 */
import {resolve} from 'node:path';
import {pathToFileURL} from 'node:url';
import type {StorageDatabase} from './latest-schema.ts';

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
const tableName = 'external_action_keys', indexName = 'external_action_keys_action';
export const liveSchemaSql = "SELECT type,name,tbl_name,sql FROM sqlite_master WHERE type IN ('table','index')";
export const liveTriggerSql = "SELECT name FROM sqlite_master WHERE type='trigger' AND tbl_name='external_action_keys'";

/** Whole-statement normal form: every token, in order, separated by one space. Identifier quoting and keyword case
 * are ignored; string literals, numbers, punctuation and every column, type, default, constraint and index term count.
 * Comments are not stripped, so a commented statement never matches. */
export function normalizedDdl(sql: string): string {
    const tokens = sql.trim().replace(/;$/, '').match(/'(?:''|[^'])*'|"(?:""|[^"])*"|`(?:``|[^`])*`|[A-Za-z_][A-Za-z0-9_]*|\d+|\S/g) ?? [];
    return tokens.map(token => token.startsWith("'") ? token :
        token.startsWith('"') || token.startsWith('`') ? token.slice(1, -1).replaceAll(token[0] + token[0], token[0]).toLowerCase() :
        token.toLowerCase()).join(' ');
}
const approvedForms = approvedExternalActionKeysStatements.map(normalizedDdl);
const refuse = (detail: string): never => {
    throw Error('Only the approved external_action_keys schema (table and unique index) may be applied: ' + detail + '; no schema statements applied.');
};

/** `WEAVE_APPROVED_EXTERNAL_ACTION_KEYS_SCHEMA`: absent, empty or `false` is off; `true` is on; anything else fails. */
export function externalActionKeysOptIn(value: string | undefined): boolean {
    if (value === undefined || value === '' || value === 'false') return false;
    if (value === 'true') return true;
    throw Error('Invalid external_action_keys schema opt-in; use true or false. No mutation attempted.');
}

/** A pending plan the opt-in may apply: a non-empty, in-order subset of the approved statements (both, or only the
 * index when the exact table already exists). Returns the approved statement text to execute, not the plan's. */
export function approvedExternalActionKeysPlan(statements: readonly string[]): string[] {
    if (!statements.length) return [];
    const forms = statements.map(normalizedDdl);
    let from = 0;
    const indexes = forms.map(form => {
        const at = approvedForms.indexOf(form, from);
        if (at < 0) refuse('the plan contains a statement that is not the approved definition');
        from = at + 1;
        return at;
    });
    return indexes.map(at => approvedExternalActionKeysStatements[at]);
}

/** The live table and index, where they exist, must match the approved definitions in full, with no other index or
 * trigger on the table. With `required`, both must exist. Uses only metadata reads. */
export async function assertLiveExternalActionKeys(db: StorageDatabase, required: boolean): Promise<void> {
    const objects = (await db.prepare(liveSchemaSql).all<{type: string; name: string; tbl_name: string; sql: string | null}>()).results;
    const table = objects.find(row => row.type === 'table' && row.name === tableName);
    const index = objects.find(row => row.type === 'index' && row.name === indexName);
    if (table && normalizedDdl(table.sql ?? '') !== approvedForms[0]) refuse('the existing external_action_keys table differs from the approved definition');
    if (index && (index.tbl_name !== tableName || normalizedDdl(index.sql ?? '') !== approvedForms[1]))
        refuse('the existing external_action_keys_action index differs from the approved definition');
    // SQLite's own primary-key index has no SQL; any other index or trigger on the table changes its behaviour.
    if (objects.some(row => row.type === 'index' && row.tbl_name === tableName && row.name !== indexName && row.sql !== null))
        refuse('the existing external_action_keys table has an unapproved index');
    if (table && (await db.prepare(liveTriggerSql).all<{name: string}>()).results.length)
        refuse('the existing external_action_keys table has a trigger');
    if (required && (!table || !index)) refuse('the approved table and index are not both present');
}

export interface ExternalActionKeysReleaseIntent {
    eventName: string;
    ref: string;
    configurationScope: string;
    firstPartyOAuth: string;
    enabled: string;
}

/** Workflow gate: the opt-in only on a manual dispatch from main into protected production, and never together with
 * the one-time OAuth bootstrap. The deploy script itself refuses the opt-in outside completed-only mode. */
export function validateExternalActionKeysRelease(input: ExternalActionKeysReleaseIntent): boolean {
    if (!externalActionKeysOptIn(input.enabled)) return false;
    if (input.eventName !== 'workflow_dispatch' || input.ref !== 'refs/heads/main' ||
        input.configurationScope !== 'production' || input.firstPartyOAuth !== 'false')
        throw Error('The external_action_keys schema opt-in requires a manual main dispatch into protected production, without the OAuth bootstrap.');
    return true;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
    try {
        if (process.argv.length !== 2) throw Error('Unexpected release gate argument.');
        const enabled = validateExternalActionKeysRelease({
            eventName: process.env.GITHUB_EVENT_NAME ?? '',
            ref: process.env.GITHUB_REF ?? '',
            configurationScope: process.env.WEAVE_RELEASE_CONFIGURATION_SCOPE ?? '',
            firstPartyOAuth: process.env.WEAVE_RELEASE_FIRST_PARTY_OAUTH ?? '',
            enabled: process.env.WEAVE_RELEASE_EXTERNAL_ACTION_KEYS_SCHEMA ?? ''
        });
        console.log(enabled ? 'external_action_keys schema opt-in accepted for this run; the deploy applies only the approved statements.' :
            'external_action_keys schema opt-in off for this release.');
    } catch (error) {
        console.error((error as Error).message + ' No cloud changes attempted.');
        process.exitCode = 1;
    }
}
