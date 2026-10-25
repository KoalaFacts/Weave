import { fail, stringify } from './wire';
import type { OAuthServiceBinding } from './remote-oauth';
import { RecordStore } from './record-store';
import { nowUtc } from './time';

export interface Statement {
    bind(...values: (string | number | null | ArrayBuffer)[]): Statement;
    all<T>(): Promise<{ results: T[] }>;
    first<T>(column?: string): Promise<T | null>;
    run(): Promise<unknown>;
}
export interface Database { prepare(sql: string): Statement; batch(statements: Statement[]): Promise<unknown[]>; }
export interface Env {
    // Optional delegated OAuth resource-server configuration. Confidential values
    // are injected as Worker secrets, never committed to wrangler.jsonc.
    OAUTH_ISSUER?: string;
    OAUTH_RESOURCE?: string;
    OAUTH_INTROSPECTION_URL?: string;
    OAUTH_CLIENT_ID?: string;
    OAUTH_CLIENT_SECRET?: string;
    // Optional, exclusive private RPC alternative to HTTPS RFC7662 introspection.
    AUTH_SERVER?: OAuthServiceBinding;
    db: Database;
    MAINTENANCE_MODE?: string;
    RECOVERY_READ_ONLY?: string;
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
    readonly store:RecordStore;
    constructor(public db: Database) {this.store=new RecordStore(db,this.writes,nowUtc());}
    async get(kind: string, owner: string, id: string): Promise<any | null> {
        const key = stringify([kind,owner,id]); if (this.cache.has(key)) return this.cache.get(key);
        const value=await this.store.get(kind,owner,id);this.cache.set(key,value);return value;
    }
    put(kind: string, owner: string, id: string, value: any): void {
        this.cache.set(stringify([kind,owner,id]),value);
        this.store.put(kind,owner,id,value);
    }
    delete(kind: string, owner: string, id: string): void {
        this.cache.set(stringify([kind,owner,id]),null);
        if(kind!=='stream')throw Error('Only temporary stream leases may be deleted by record kind.');
        this.writes.push(this.db.prepare('DELETE FROM stream_leases WHERE account_id=? AND lease_id=?').bind(owner,id));
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
            const at=tx.store.now.toString();
            await db.batch([
                db.prepare('UPDATE mailbox_revision SET token=?,updated_at=? WHERE singleton=1 AND token=?').bind(crypto.randomUUID(),at,revision),
                db.prepare('INSERT INTO mailbox_guard(ok,created_at,updated_at) VALUES(changes(),?,?)').bind(at,at),
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
