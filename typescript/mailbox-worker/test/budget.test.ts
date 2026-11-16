import assert from 'node:assert/strict';
import { test } from 'node:test';
import { build } from 'esbuild';
import { runInNewContext } from 'node:vm';

// Every request, cron sweep and stream runs on budgetDatabase(env.db): at most 48 D1 statements,
// with each batch charged per statement. Exhaustion is 503 storage before the statement reaches D1.
type Problem = { status: number; code: string };
type Statement = { bind(...values: unknown[]): Statement; all(): Promise<unknown>; first(column?: string): Promise<unknown>; run(): Promise<unknown> };
type Database = { prepare(sql: string): Statement; batch(statements: Statement[]): Promise<unknown[]> };

async function budgetDatabase(): Promise<(db: Database, maximum?: number) => Database> {
    const bundled = await build({ stdin: { contents: "export {budgetDatabase} from './src/budget.ts';", resolveDir: process.cwd() }, bundle: true, write: false, format: 'cjs', platform: 'node' });
    const module = { exports: {} as { budgetDatabase: (db: Database, maximum?: number) => Database } };
    runInNewContext(bundled.outputFiles[0].text, { module, exports: module.exports, TextEncoder });
    return module.exports.budgetDatabase;
}

// Records what actually reached the underlying database.
function recordingDatabase() {
    const sent: string[] = [];
    const statement = (sql: string, bound: unknown[] = []): Statement => ({
        bind: (...values) => statement(sql, values),
        all: async () => { sent.push(`all ${sql} ${bound.join(',')}`); return { results: [] }; },
        first: async (column?: string) => { sent.push(`first ${sql} ${bound.join(',')} ${column ?? ''}`.trimEnd()); return null; },
        run: async () => { sent.push(`run ${sql} ${bound.join(',')}`); return {}; }
    });
    const db: Database = { prepare: sql => statement(sql), batch: async statements => { sent.push(`batch ${statements.length}`); return statements.map(() => ({})); } };
    return { db, sent };
}

const storage = (error: unknown) => (error as Problem).status === 503 && (error as Problem).code === 'storage';

test('budgetDatabase_FortyEighthStatement_SentAndFortyNinthRefusedBeforeD1', async () => {
    const budget = await budgetDatabase(), { db, sent } = recordingDatabase(), limited = budget(db);
    const kinds = ['all', 'first', 'run'] as const;
    for (let i = 0; i < 48; i++) await limited.prepare(`SELECT ${i}`)[kinds[i % 3]]();
    assert.equal(sent.length, 48, 'all, first and run are each charged one statement');
    for (const kind of kinds) await assert.rejects(limited.prepare('SELECT 48')[kind](), storage, kind);
    assert.equal(sent.length, 48, 'a refused statement never reaches D1');
});

test('budgetDatabase_BoundStatement_StillChargedAndSentWithItsValues', async () => {
    const budget = await budgetDatabase(), { db, sent } = recordingDatabase(), limited = budget(db, 2);
    await limited.prepare('SELECT ?').bind('a').bind('b').run();
    await limited.prepare('SELECT ? AS n').bind('c').first('n');
    assert.deepEqual(sent, ['run SELECT ? b', 'first SELECT ? AS n c n'], 'values and the column pass through unchanged');
    await assert.rejects(limited.prepare('SELECT ?').bind('d').first(), storage, 'binding does not escape the budget');
    assert.equal(sent.length, 2);
});

test('budgetDatabase_BatchFillingTheBudget_SentAndOneMoreRefusedWithNothingWritten', async () => {
    const budget = await budgetDatabase(), { db, sent } = recordingDatabase(), limited = budget(db);
    for (let i = 0; i < 46; i++) await limited.prepare('SELECT 1').run();
    await limited.batch([limited.prepare('INSERT 1'), limited.prepare('INSERT 2')]);
    assert.equal(sent.at(-1), 'batch 2', 'a batch that ends exactly at 48 is sent');
    await assert.rejects(limited.batch([limited.prepare('INSERT 3')]), storage);
    await assert.rejects(limited.prepare('SELECT 1').run(), storage);
    assert.equal(sent.length, 47);
});

test('budgetDatabase_RefusedBatch_ChargesNothingSoASmallerOneStillFits', async () => {
    const budget = await budgetDatabase(), { db, sent } = recordingDatabase(), limited = budget(db);
    for (let i = 0; i < 47; i++) await limited.prepare('SELECT 1').run();
    // 47 + 2 > 48: the whole batch is refused, none of its statements is written, and the budget is not spent.
    await assert.rejects(limited.batch([limited.prepare('INSERT 1'), limited.prepare('INSERT 2')]), storage);
    assert.equal(sent.length, 47, 'no partial batch is sent');
    await limited.batch([limited.prepare('INSERT 1')]);
    assert.equal(sent.at(-1), 'batch 1', 'the refused batch charged nothing');
    await assert.rejects(limited.prepare('SELECT 1').first(), storage);
});

test('budgetDatabase_BatchLargerThanTheWholeBudget_RefusedFromAFreshBudget', async () => {
    const budget = await budgetDatabase(), { db, sent } = recordingDatabase(), limited = budget(db);
    await assert.rejects(limited.batch(Array.from({ length: 49 }, (_, i) => limited.prepare(`INSERT ${i}`))), storage);
    await limited.batch(Array.from({ length: 48 }, (_, i) => limited.prepare(`INSERT ${i}`)));
    assert.deepEqual(sent, ['batch 48']);
});

test('budgetDatabase_SeparateWrappers_EachHaveTheirOwnBudget', async () => {
    const budget = await budgetDatabase(), { db, sent } = recordingDatabase();
    const first = budget(db, 2), second = budget(db, 2);
    await first.prepare('SELECT 1').run(); await first.prepare('SELECT 2').run();
    await assert.rejects(first.prepare('SELECT 3').run(), storage);
    await second.prepare('SELECT 4').run();
    assert.equal(sent.length, 3, 'one request exhausting its budget does not spend another request\'s');
});
