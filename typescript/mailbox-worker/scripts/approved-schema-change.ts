/** One owner-approved additive schema change a completed-only release may apply: a new table and its one index, each
 * pinned byte for byte as `src/schema.ts` generates it. Each change has its own per-run opt-in, off by default. An opt-in
 * grants nothing else: any statement outside the enabled changes, or an existing table or index that differs from its
 * approved definition, stops the release.
 */
import type {StorageDatabase} from './latest-schema.ts';

export const liveSchemaSql = "SELECT type,name,tbl_name,sql FROM sqlite_master WHERE type IN ('table','index')";

/** Whole-statement normal form: every token, in order, separated by one space. Identifier quoting and keyword case
 * are ignored; string literals, numbers, punctuation and every column, type, default, constraint and index term count.
 * Comments are not stripped, so a commented statement never matches. */
export function normalizedDdl(sql: string): string {
    const tokens = sql.trim().replace(/;$/, '').match(/'(?:''|[^'])*'|"(?:""|[^"])*"|`(?:``|[^`])*`|[A-Za-z_][A-Za-z0-9_]*|\d+|\S/g) ?? [];
    return tokens.map(token => token.startsWith("'") ? token :
        token.startsWith('"') || token.startsWith('`') ? token.slice(1, -1).replaceAll(token[0] + token[0], token[0]).toLowerCase() :
        token.toLowerCase()).join(' ');
}

export interface ReleaseIntent {
    eventName: string;
    ref: string;
    configurationScope: string;
    firstPartyOAuth: string;
    enabled: string;
}

export interface ApprovedSchemaChange {
    readonly table: string;
    readonly index: string;
    /** The table statement, then the index statement. */
    readonly statements: readonly [string, string];
    /** The read of triggers on the table; the completed-only metadata capability allows exactly these reads. */
    readonly liveTriggerSql: string;
    /** The per-run opt-in value: absent, empty or `false` is off; `true` is on; anything else fails. */
    optIn(value: string | undefined): boolean;
    /** True when the statement is one of this change's approved statements. */
    owns(statement: string): boolean;
    /** A pending plan this change may apply: a non-empty, in-order subset of its approved statements (both, or only the
     * index when the exact table already exists). Returns the approved statement text to execute, not the plan's. */
    plan(statements: readonly string[]): string[];
    /** The live table and index, where they exist, must match the approved definitions in full, with no other index or
     * trigger on the table. With `required`, both must exist. Uses only metadata reads. */
    assertLive(db: StorageDatabase, required: boolean): Promise<void>;
    /** Workflow gate: the opt-in only on a manual dispatch from main into protected production, and never together with
     * the one-time OAuth bootstrap. The deploy script itself refuses the opt-in outside completed-only mode. */
    validateRelease(input: ReleaseIntent): boolean;
}

export function approvedSchemaChange(table: string, index: string, statements: readonly [string, string]): ApprovedSchemaChange {
    const forms = statements.map(normalizedDdl);
    const liveTriggerSql = `SELECT name FROM sqlite_master WHERE type='trigger' AND tbl_name='${table}'`;
    const indexKind = /^CREATE UNIQUE INDEX /.test(statements[1]) ? 'unique index' : 'index';
    const refuse = (detail: string): never => {
        throw Error(`Only the approved ${table} schema (table and ${indexKind}) may be applied: ${detail}; no schema statements applied.`);
    };
    const optIn = (value: string | undefined): boolean => {
        if (value === undefined || value === '' || value === 'false') return false;
        if (value === 'true') return true;
        throw Error(`Invalid ${table} schema opt-in; use true or false. No mutation attempted.`);
    };
    return {
        table, index, statements, liveTriggerSql, optIn,
        owns: statement => forms.includes(normalizedDdl(statement)),
        plan(pending) {
            if (!pending.length) return [];
            let from = 0;
            const at = pending.map(statement => {
                const found = forms.indexOf(normalizedDdl(statement), from);
                if (found < 0) refuse('the plan contains a statement that is not the approved definition');
                from = found + 1;
                return found;
            });
            return at.map(position => statements[position]!);
        },
        async assertLive(db, required) {
            const objects = (await db.prepare(liveSchemaSql).all<{type: string; name: string; tbl_name: string; sql: string | null}>()).results;
            const liveTable = objects.find(row => row.type === 'table' && row.name === table);
            const liveIndex = objects.find(row => row.type === 'index' && row.name === index);
            if (liveTable && normalizedDdl(liveTable.sql ?? '') !== forms[0]) refuse(`the existing ${table} table differs from the approved definition`);
            if (liveIndex && (liveIndex.tbl_name !== table || normalizedDdl(liveIndex.sql ?? '') !== forms[1]))
                refuse(`the existing ${index} index differs from the approved definition`);
            // SQLite's own primary-key index has no SQL; any other index or trigger on the table changes its behaviour.
            if (objects.some(row => row.type === 'index' && row.tbl_name === table && row.name !== index && row.sql !== null))
                refuse(`the existing ${table} table has an unapproved index`);
            if (liveTable && (await db.prepare(liveTriggerSql).all<{name: string}>()).results.length)
                refuse(`the existing ${table} table has a trigger`);
            if (required && (!liveTable || !liveIndex)) refuse('the approved table and index are not both present');
        },
        validateRelease(input) {
            if (!optIn(input.enabled)) return false;
            if (input.eventName !== 'workflow_dispatch' || input.ref !== 'refs/heads/main' ||
                input.configurationScope !== 'production' || input.firstPartyOAuth !== 'false')
                throw Error(`The ${table} schema opt-in requires a manual main dispatch into protected production, without the OAuth bootstrap.`);
            return true;
        }
    };
}

/** The statements the enabled changes apply, in plan order. Every pending statement must belong to one enabled change and
 * each change's own statements must be in its approved order; otherwise the first enabled change's refusal stops the
 * release (or a generic one when none is enabled). */
export function approvedPlan(pending: readonly string[], enabled: readonly ApprovedSchemaChange[]): string[] {
    if (!pending.length) return [];
    const owner = pending.map(statement => enabled.find(change => change.owns(statement)));
    if (owner.some(change => change === undefined)) {
        if (!enabled.length) throw Error('Restricted deployment requires an empty schema plan; no schema changes are permitted.');
        // Let the first enabled change name the refusal: the plan holds a statement none of them approved.
        enabled[0]!.plan(pending);
    }
    for (const change of enabled) change.plan(pending.filter((_, at) => owner[at] === change));
    return pending.map((statement, at) => owner[at]!.plan([statement])[0]!);
}

/** Command-line workflow gate shared by each change's script: reads the release environment, prints the outcome and
 * sets a failing exit code without any cloud change. */
export function runReleaseGate(change: ApprovedSchemaChange, variable: string): void {
    try {
        if (process.argv.length !== 2) throw Error('Unexpected release gate argument.');
        const enabled = change.validateRelease({
            eventName: process.env.GITHUB_EVENT_NAME ?? '',
            ref: process.env.GITHUB_REF ?? '',
            configurationScope: process.env.WEAVE_RELEASE_CONFIGURATION_SCOPE ?? '',
            firstPartyOAuth: process.env.WEAVE_RELEASE_FIRST_PARTY_OAUTH ?? '',
            enabled: process.env[variable] ?? ''
        });
        console.log(enabled ? `${change.table} schema opt-in accepted for this run; the deploy applies only the approved statements.` :
            `${change.table} schema opt-in off for this release.`);
    } catch (error) {
        console.error((error as Error).message + ' No cloud changes attempted.');
        process.exitCode = 1;
    }
}
