import { atomic, type Database } from '../src/database.ts';
import { limits } from '../src/limits.ts';
import { hash } from '../src/wire.ts';

/** Fills self-registration capacity with ownerless placeholder accounts, leaving `free` places. */
export async function fillAccounts(db: Database, free = 0): Promise<void> {
    const existing = await db.prepare('SELECT count(*) n FROM accounts WHERE human_id IS NULL').first<number>('n') ?? 0;
    const missing = limits.registrationMaxAccounts - free - existing;
    if (missing <= 0) return;
    await db.prepare(`WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<?)
        INSERT INTO accounts(account_id,human_id,display_name,description,created_at,updated_at)
        SELECT printf('00000000-0000-4000-8000-%012d',i),NULL,NULL,NULL,0,0 FROM n`).bind(missing).run();
}

/** Sets this hour's registration counter to `count`: for one source IP, or across every source when none is given. */
export async function setRegistrationHour(db: Database, count: number, sourceIp?: string): Promise<void> {
    await atomic(db, async tx => {
        const hour = Number(tx.store.now / 36000000000n);
        const id = sourceIp === undefined ? 'global' : await hash('registration:' + hour + ':' + sourceIp);
        tx.put('registration-rate', '', id, { hour, count });
    });
}

/** Gives a human placeholder agents up to the per-human limit, leaving `free` places. */
export async function fillOwnedAgents(db: Database, humanId: string, free = 0): Promise<void> {
    const existing = await db.prepare('SELECT count(*) n FROM accounts WHERE human_id=?').bind(humanId).first<number>('n') ?? 0;
    const missing = limits.ownedAgentsPerHuman - free - existing;
    if (missing <= 0) return;
    await db.prepare(`WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<?)
        INSERT INTO accounts(account_id,human_id,display_name,description,created_at,updated_at)
        SELECT lower(hex(randomblob(4)))||'-0000-4000-8000-'||printf('%012d',i),?,'placeholder '||i,NULL,0,0 FROM n`).bind(missing, humanId).run();
}
