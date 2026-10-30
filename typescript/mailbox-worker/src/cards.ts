import { Transaction } from './database';
import { enc, fail, identity, object } from './wire';
import { fingerprint, ticks, utf8 } from './protocol';

export async function publishSequence(tx: Transaction): Promise<string> {
    const n = await tx.get('counter','','public-cards');
    const value = BigInt(n?.value ?? '0') + 1n;
    tx.put('counter','','public-cards',{value:value.toString()}); return value.toString();
}
export const cardView = (card: any) => { const {_publicSequence,_expiresTicks,_address,_deleted,_effectiveFromTicks,_effectiveToTicks,...view} = card; return view; };
export function eligible(card: any, now = BigInt(Date.now())*10000n): boolean { return card && !card._deleted && (card._effectiveFromTicks == null || BigInt(card._effectiveFromTicks) <= now) && (card._effectiveToTicks == null || BigInt(card._effectiveToTicks) > now) && card.revokedAt === null && (card.expiresAt === null || ticks(card.expiresAt) > now); }
export async function putCard(tx: Transaction,owner: string,input: any): Promise<any> {
    object(input,['cardId','visibility','createdAt','expiresAt','revokedAt','audienceHint','methods']); identity(input.cardId);
    const created = ticks(input.createdAt), now = BigInt(Date.now())*10000n;
    if (!['public','unlisted'].includes(input.visibility) || created > now || input.expiresAt !== null && ticks(input.expiresAt) <= created || input.revokedAt !== null && (ticks(input.revokedAt) < created || ticks(input.revokedAt) > now)) fail();
    if (input.audienceHint !== null && (typeof input.audienceHint !== 'string' || enc.encode(input.audienceHint).length > 1024)) fail();
    if (!Array.isArray(input.methods) || input.methods.length < 1 || input.methods.length > 16) fail();
    const ids = new Set();
    for (const m of input.methods) {
        object(m,['methodId','version','transport','endpoint','instructions']); identity(m.methodId);
        if (ids.has(m.methodId) || !Number.isInteger(m.version) || m.version < 1 || m.version > 2147483647 || !['relay','direct'].includes(m.transport)) fail();
        ids.add(m.methodId); utf8(m.endpoint,2048);
        if (typeof m.instructions !== 'string' || enc.encode(m.instructions).length > 65536) fail();
    }
    const existing = await tx.get('card','',input.cardId), card = {...input,ownerMailboxId:owner};
    if (existing) {
        if (existing.ownerMailboxId !== owner) fail(404,'unavailable');
        if (existing._address) fail(409,'conflict');
        if (await fingerprint({...cardView(existing),revokedAt:null}) !== await fingerprint({...card,revokedAt:null}) || existing.revokedAt !== null && ticks(existing.revokedAt) !== (input.revokedAt === null ? null : ticks(input.revokedAt))) fail(409,'conflict');
        if(existing.revokedAt===null&&input.revokedAt===null||existing.revokedAt!==null&&input.revokedAt!==null&&ticks(existing.revokedAt)===ticks(input.revokedAt))return cardView(existing);
    } else {
        const count = await tx.db.prepare('SELECT (SELECT count(*) FROM contact_cards)+(SELECT count(*) FROM account_addresses) n').first<number>('n');
        if (count === null || count >= 1000) fail(429,'capacity');
    }
    tx.put('card','',card.cardId,{...card,_expiresTicks:card.expiresAt === null ? null : ticks(card.expiresAt).toString(),_publicSequence:existing?._publicSequence ?? (card.visibility === 'public' ? await publishSequence(tx) : null)}); return card;
}
