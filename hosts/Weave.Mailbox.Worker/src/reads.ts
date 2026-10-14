import { Transaction } from './database';
import { cardView, eligible } from './cards';
import { effective } from './messages';
import { fail, identity, limit, parse, query, uuid } from './wire';
import { channelView, effectiveRequest, readChannel } from './protocol';

export function pageStart(url: URL,kind: string,owner: string): {n:number;after:string;prefix:string} {
    const n = limit(url), cursor = query(url,'afterCursor');
    const prefix = kind + ':' + btoa(unescape(encodeURIComponent(owner))) + ':';
    if (cursor !== null && (!cursor.startsWith(prefix) || !/^\d+$/.test(cursor.slice(prefix.length)) || BigInt(cursor.slice(prefix.length)) > 9223372036854775807n)) fail();
    return {n,after:cursor?.slice(prefix.length) ?? '0',prefix};
}
export async function read(tx: Transaction,url: URL,owner: string | null): Promise<any> {
    const path = url.pathname;
    if (path === '/v1/contacts/card') {
        const id = query(url,'cardId',true); identity(id); const c = await tx.get('card','',id);
        if (!eligible(c) || c.visibility !== 'public' && c.ownerMailboxId !== owner) fail(404,'unavailable'); return cardView(c);
    }
    if (path === '/v1/contacts/channel') {
        const peer = query(url,'peerMailboxId',true); identity(peer);
        const c = await readChannel(tx,owner!,peer); if (!c) fail(404,'unavailable'); return channelView(c,owner!);
    }
    const request = path.match(/^\/v1\/contacts\/requests\/([^/]+)$/);
    if (request) {
        uuid(request[1]); const requester = query(url,'requesterMailboxId',true); identity(requester);
        const r = await tx.get('request',requester,request[1]);
        if (!r || ![r.summary.requesterMailboxId,r.summary.recipientMailboxId].includes(owner)) fail(404,'unavailable'); return effectiveRequest(r.summary);
    }
    const receipt = path.match(/^\/v1\/outbox\/([^/]+)$/);
    if (receipt) { uuid(receipt[1]); const m = await tx.get('message',owner!,receipt[1]); if (!m) fail(404,'unavailable'); return effective(tx,m); }
    const kinds: Record<string,string> = {'/v1/inbox':'inbox','/v1/outbox':'receipts','/v1/contacts/requests':'contacts','/v1/contacts/cards':'public-cards','/v1/contacts/owned-cards':'owned-cards'};
    const kind = kinds[path]; if (!kind) fail(404,'unavailable');
    const {n,after,prefix} = pageStart(url,kind,kind === 'public-cards' ? 'public' : owner!);
    let sql: string, args: (string|number)[];
    if (kind.endsWith('cards')) {
        const publicList = kind === 'public-cards', sequence = publicList ? "CAST(json_extract(data,'$._publicSequence') AS INTEGER)" : 'seq';
        sql = `SELECT CAST(${sequence} AS TEXT) seq,data FROM mailbox_records WHERE kind='card' AND ${sequence}>CAST(? AS INTEGER)
            AND json_extract(data,'$.revokedAt') IS NULL AND (json_extract(data,'$._effectiveFromTicks') IS NULL OR CAST(json_extract(data,'$._effectiveFromTicks') AS INTEGER)<=CAST(? AS INTEGER)) AND (json_extract(data,'$._effectiveToTicks') IS NULL OR CAST(json_extract(data,'$._effectiveToTicks') AS INTEGER)>CAST(? AS INTEGER)) AND (json_extract(data,'$.expiresAt') IS NULL OR CAST(json_extract(data,'$._expiresTicks') AS INTEGER)>CAST(? AS INTEGER)) AND
            ${publicList ? "json_extract(data,'$.visibility')='public'" : "json_extract(data,'$.ownerMailboxId')=?"} ORDER BY ${sequence} LIMIT ?`;
        args = [after,...Array(3).fill((BigInt(Date.now())*10000n).toString()),...(publicList ? [] : [owner!]),n+1];
    } else if (kind === 'inbox') {
        sql = `SELECT CAST(m.seq AS TEXT) seq,m.data FROM mailbox_records m JOIN mailbox_records c ON c.kind='channel'
            AND ((json_extract(c.data,'$.low')=m.owner AND json_extract(c.data,'$.high')=?) OR (json_extract(c.data,'$.high')=m.owner AND json_extract(c.data,'$.low')=?))
            LEFT JOIN mailbox_records r ON r.kind='request' AND r.owner=json_extract(c.data,'$.currentRequest.requesterMailboxId') AND r.id=json_extract(c.data,'$.currentRequest.requestId')
            WHERE m.kind='message' AND m.seq>CAST(? AS INTEGER) AND json_extract(m.data,'$.receipt.recipientMailboxId')=? AND json_extract(m.data,'$.receipt.state')='pending'
            AND CAST(json_extract(m.data,'$.expiresTicks') AS INTEGER)>CAST(? AS INTEGER) AND json_extract(c.data,'$.blockedLow')=0 AND json_extract(c.data,'$.blockedHigh')=0
            AND json_extract(m.data,'$.generation')=json_extract(c.data,'$.generation')
            AND ((json_extract(m.data,'$.purpose') IN (1,2) AND json_extract(m.data,'$.request.requesterMailboxId')=json_extract(c.data,'$.currentRequest.requesterMailboxId') AND json_extract(m.data,'$.request.requestId')=json_extract(c.data,'$.currentRequest.requestId'))
                OR (json_extract(m.data,'$.purpose')=0 AND json_extract(r.data,'$.summary.status')='accepted' AND json_extract(r.data,'$.summary.generation')=json_extract(c.data,'$.generation')))
            ORDER BY m.seq LIMIT ?`;
        args = [owner!,owner!,after,owner!,(BigInt(Date.now())*10000n).toString(),n+1];
    } else {
        sql = `SELECT CAST(seq AS TEXT) seq,data FROM mailbox_records WHERE kind=? AND seq>CAST(? AS INTEGER) AND ${kind === 'contacts' ? "(owner=? OR json_extract(data,'$.summary.recipientMailboxId')=?)" : 'owner=?'} ORDER BY mailbox_records.seq LIMIT ?`;
        args = [kind === 'contacts' ? 'request' : 'message',after,owner!,...(kind === 'contacts' ? [owner!] : []),n+1];
    }
    const rows = (await tx.db.prepare(sql).bind(...args).all<{seq:string;data:string}>()).results;
    const items = [];
    for (const row of rows.slice(0,n)) {
        const value = parse(row.data);
        items.push(kind.endsWith('cards') ? cardView(value) : kind === 'contacts' ? effectiveRequest(value.summary) : kind === 'receipts' ? await effective(tx,value) :
            {senderMailboxId:value.receipt.senderMailboxId,envelope:{version:value.version,recipientMailboxId:value.receipt.recipientMailboxId,contactGeneration:value.generation,payload:value.payload}});
    }
    return {items,nextCursor:rows.length > n ? prefix + rows[n-1].seq : null};
}
