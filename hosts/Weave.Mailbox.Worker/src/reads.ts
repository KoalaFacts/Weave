import { Transaction } from './database';
import { cardView, eligible } from './cards';
import { effective } from './messages';
import { fail, identity, identityQuery, limit, parse, query, uuid } from './wire';
import { channelView, effectiveRequest, readChannel } from './protocol';
import { accountAddresses, contactCards, contactRequests } from './schema';
import { addressSnapshot, cardSnapshot, requestSnapshot, messageSnapshot } from './wire-snapshots';

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
        const peer = identityQuery(url,'peerAccountId','peerMailboxId');
        const c = await readChannel(tx,owner!,peer); if (!c) fail(404,'unavailable'); return channelView(c,owner!);
    }
    const request = path.match(/^\/v1\/contacts\/requests\/([^/]+)$/);
    if (request) {
        uuid(request[1]); const requester = identityQuery(url,'requesterAccountId','requesterMailboxId');
        const r = await tx.get('request',requester,request[1]);
        if (!r || ![r.summary.requesterMailboxId,r.summary.recipientMailboxId].includes(owner)) fail(404,'unavailable'); return effectiveRequest(r.summary);
    }
    const receipt = path.match(/^\/v1\/outbox\/([^/]+)$/);
    if (receipt) { uuid(receipt[1]); const m = await tx.get('message',owner!,receipt[1]); if (!m) fail(404,'unavailable'); return effective(tx,m); }
    const kinds: Record<string,string> = {'/v1/inbox':'inbox','/v1/outbox':'receipts','/v1/contacts/requests':'contacts','/v1/contacts/cards':'public-cards','/v1/contacts/owned-cards':'owned-cards'};
    const kind = kinds[path]; if (!kind) fail(404,'unavailable');
    const {n,after,prefix} = pageStart(url,kind,kind === 'public-cards' ? 'public' : owner!);
    let rows:{seq:string;value:any}[];
    const now=(BigInt(Date.now())*10000n).toString();
    if(kind.endsWith('cards')){
        const publicList=kind==='public-cards',sequence=publicList?'public_seq':'seq';
        const addressRows=await tx.store.rows(accountAddresses,`${sequence}>CAST(? AS INTEGER) AND revoked_at IS NULL
            AND (effective_from IS NULL OR effective_from<=CAST(? AS INTEGER)) AND (effective_to IS NULL OR effective_to>CAST(? AS INTEGER))
            AND (expires_at IS NULL OR expires_at>CAST(? AS INTEGER)) AND ${publicList?'is_public=1':'account_id=?'}`,
            [after,now,now,now,...(publicList?[]:[owner!]),n+1],`ORDER BY account_addresses.${sequence} LIMIT ?`);
        const cardRows=await tx.store.rows(contactCards,`${sequence}>CAST(? AS INTEGER) AND revoked_at IS NULL AND (expires_at IS NULL OR expires_at>CAST(? AS INTEGER))
            AND ${publicList?"visibility='public'":'owner_account_id=?'}`,[after,now,...(publicList?[]:[owner!]),n+1],`ORDER BY contact_cards.${sequence} LIMIT ?`);
        rows=[...addressRows.map(row=>({seq:row[sequence],value:addressSnapshot(row)})),...cardRows.map(row=>({seq:row[sequence],value:cardSnapshot(row)}))]
            .sort((a,b)=>BigInt(a.seq)<BigInt(b.seq)?-1:BigInt(a.seq)>BigInt(b.seq)?1:0).slice(0,n+1);
    }else if(kind==='contacts'){
        rows=(await tx.store.rows(contactRequests,'seq>CAST(? AS INTEGER) AND (requester_account_id=? OR recipient_account_id=?)',[after,owner!,owner!,n+1],'ORDER BY contact_requests.seq LIMIT ?'))
            .map(row=>({seq:row.seq,value:requestSnapshot(row)}));
    }else{
        let query=tx.store.messageQuery(),args:(string|number)[];
        if(kind==='inbox'){
            query+=` JOIN legacy_channels c ON c.low_account_id=min(mailbox_records.sender_account_id,mailbox_records.recipient_account_id)
                AND c.high_account_id=max(mailbox_records.sender_account_id,mailbox_records.recipient_account_id)
                LEFT JOIN contact_requests r ON r.requester_account_id=c.current_requester_account_id AND r.request_id=c.current_request_id
                WHERE mailbox_records.seq>CAST(? AS INTEGER) AND mailbox_records.recipient_account_id=? AND message_delivery.state='pending'
                AND mailbox_records.expires_at>CAST(? AS INTEGER) AND c.blocked_low=0 AND c.blocked_high=0 AND message_delivery.legacy_generation=c.generation
                AND ((message_delivery.purpose IN (1,2) AND message_delivery.requester_account_id=c.current_requester_account_id AND message_delivery.request_id=c.current_request_id)
                    OR (message_delivery.purpose=0 AND r.status='accepted' AND r.legacy_generation=c.generation
                        AND EXISTS(SELECT 1 FROM contacts a JOIN contacts b ON b.owner_account_id=a.to_account_id AND b.to_account_id=a.owner_account_id AND b.connection_token=a.connection_token
                            WHERE a.owner_account_id=mailbox_records.sender_account_id AND a.to_account_id=mailbox_records.recipient_account_id
                            AND (message_delivery.connection_token IS NULL OR message_delivery.connection_token=a.connection_token))))`;
            args=[after,owner!,now,n+1];
        }else{query+=' WHERE mailbox_records.seq>CAST(? AS INTEGER) AND mailbox_records.sender_account_id=?';args=[after,owner!,n+1];}
        const selected=(await tx.db.prepare(query+' ORDER BY mailbox_records.seq LIMIT ?').bind(...args).all<any>()).results;
        rows=selected.map(row=>({seq:row.seq,value:messageSnapshot(row)}));
    }
    const items = [];
    for (const row of rows.slice(0,n)) {
        const value = row.value;
        items.push(kind.endsWith('cards') ? cardView(value) : kind === 'contacts' ? effectiveRequest(value.summary) : kind === 'receipts' ? await effective(tx,value) :
            {senderMailboxId:value.receipt.senderMailboxId,envelope:{version:value.version,recipientMailboxId:value.receipt.recipientMailboxId,contactGeneration:value.generation,
                ...(value.version===2?{connectionToken:value.connectionToken}:{}),payload:value.payload}});
    }
    return {items,nextCursor:rows.length > n ? prefix + rows[n-1].seq : null};
}
