import { fail, parse, stringify } from './wire';

export interface Statement {
    bind(...values: (string | number | null)[]): Statement;
    all<T>(): Promise<{ results: T[] }>;
    first<T>(column?: string): Promise<T | null>;
    run(): Promise<unknown>;
}
export interface Database { prepare(sql: string): Statement; batch(statements: Statement[]): Promise<unknown[]>; }
export interface Env {
    db: Database;
    WEAVE_HOST?: string;
    CLEANUP_MAX_MESSAGES?: string;
    CLEANUP_TIME_BUDGET_MS?: string;
    REGISTRATION_ENABLED: string;
    REGISTRATION_MAX_ACCOUNTS: string;
    REGISTRATION_PER_IP_PER_HOUR: string;
    REGISTRATION_GLOBAL_PER_HOUR: string;
}
export interface RecordRow { seq: string; kind: string; owner: string; id: string; data: string; }
export class Transaction {
    writes: Statement[] = [];
    invalidatedPair: string[] | null = null;
    private cache = new Map<string,any>();
    constructor(public db: Database) {}
    async rows(kind: string, condition = '1=1', args: (string | number | null)[] = [], suffix = 'ORDER BY seq'): Promise<RecordRow[]> {
        return (await this.db.prepare(`SELECT CAST(seq AS TEXT) seq,kind,owner,id,data FROM mailbox_records WHERE kind=? AND (${condition}) ${suffix}`).bind(kind,...args).all<RecordRow>()).results;
    }
    async get(kind: string, owner: string, id: string): Promise<any | null> {
        const key = stringify([kind,owner,id]); if (this.cache.has(key)) return this.cache.get(key);
        const row = await this.db.prepare('SELECT data FROM mailbox_records WHERE kind=? AND owner=? AND id=?').bind(kind,owner,id).first<{data:string}>();
        const value = row ? parse(row.data) : null; this.cache.set(key,value); return value;
    }
    put(kind: string, owner: string, id: string, value: any): void {
        this.cache.set(stringify([kind,owner,id]),value);
        this.writes.push(this.db.prepare(`INSERT INTO mailbox_records(kind,owner,id,data) VALUES(?,?,?,?) ON CONFLICT(kind,owner,id) DO UPDATE SET data=excluded.data`).bind(kind,owner,id,stringify(value)));
    }
    delete(kind: string, owner: string, id: string): void {
        this.cache.set(stringify([kind,owner,id]),null);
        this.writes.push(this.db.prepare('DELETE FROM mailbox_records WHERE kind=? AND owner=? AND id=?').bind(kind,owner,id));
    }
}
export async function atomic<T>(db: Database, operation: (tx: Transaction) => Promise<T>): Promise<T> {
    for (let attempt = 0; attempt < 4; attempt++) {
        const revision = await db.prepare('SELECT token FROM mailbox_revision WHERE singleton=1').first<string>('token');
        if (revision === null) fail(503,'storage');
        const tx = new Transaction(db);
        // Even rejected reads need a stable revision before their outcome can be returned.
        let result: T | undefined; let error: unknown;
        try { result = await operation(tx); } catch (e) { error = e; }
        if (error || !tx.writes.length) {
            const current = await db.prepare('SELECT token FROM mailbox_revision WHERE singleton=1').first<string>('token');
            if (current !== revision) continue;
            if (error) throw error;
            return result as T;
        }
        try {
            await db.batch([
                db.prepare('UPDATE mailbox_revision SET token=? WHERE singleton=1 AND token=?').bind(crypto.randomUUID(),revision),
                db.prepare('INSERT INTO mailbox_guard(ok) VALUES(changes())'),
                ...tx.writes,
                db.prepare('DELETE FROM mailbox_guard')
            ]);
            return result as T;
        } catch (e) {
            if (!(e instanceof Error) || !e.message.includes('txn_revision')) throw e;
        }
    }
    fail(503,'storage');
}
