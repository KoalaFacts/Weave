import { Transaction } from './database';
import { fail, identity, limit, query } from './wire';
import { invalidate } from './messages';
import { MAX_GENERATION, nowText, readChannel, writeChannel } from './protocol';
import { ticks } from './protocol';

export async function connection(tx:Transaction,a:string,b:string):Promise<string|null>{
    return tx.db.prepare(`SELECT a.connection_token FROM contacts a JOIN contacts b
        ON b.owner_account_id=a.to_account_id AND b.to_account_id=a.owner_account_id AND b.connection_token=a.connection_token
        WHERE a.owner_account_id=? AND a.to_account_id=?`).bind(a,b).first<string>('connection_token');
}
export function establish(tx:Transaction,a:string,b:string,now:string):string {
    const token=Array.from(crypto.getRandomValues(new Uint8Array(32)),x=>x.toString(16).padStart(2,'0')).join('');
    const at=ticks(now).toString();
    tx.writes.push(tx.db.prepare(`INSERT INTO contacts(owner_account_id,to_account_id,connection_token,created_at,updated_at) VALUES(?,?,?,?,?),(?,?,?,?,?)
        ON CONFLICT(owner_account_id,to_account_id) DO UPDATE SET connection_token=excluded.connection_token,updated_at=excluded.updated_at`)
        .bind(a,b,token,at,at,b,a,token,at,at));
    return token;
}
export async function deleteContact(tx:Transaction,owner:string,peer:string):Promise<any>{
    identity(peer);if(peer===owner)fail();
    const existing=await tx.db.prepare('SELECT 1 found FROM contacts WHERE owner_account_id=? AND to_account_id=?').bind(owner,peer).first();
    if(!existing)return {};
    const c=await readChannel(tx,owner,peer);if(!c)fail(503,'storage');
    if(c.generation===MAX_GENERATION)fail(429,'capacity');
    const now=nowText(tx);await invalidate(tx,c,now);
    tx.writes.push(tx.db.prepare('DELETE FROM contacts WHERE owner_account_id=? AND to_account_id=?').bind(owner,peer));
    writeChannel(tx,{...c,generation:c.generation+1n,currentRequest:null,updatedAt:now});
    return {};
}
export async function ownContacts(tx:Transaction,owner:string,url:URL,peer?:string):Promise<any>{
    const n=limit(url),prefix='contacts-owned:'+btoa(owner)+':',cursor=query(url,'afterCursor');
    if(peer!==undefined)identity(peer);
    if(cursor!==null&&(!cursor.startsWith(prefix)||cursor.length>prefix.length+128))fail();
    const after=cursor?.slice(prefix.length)??'';
    // Tokens are available only to the authenticated row owner, never discovery.
    const rows=(await tx.db.prepare(`SELECT a.to_account_id accountId,a.connection_token connectionToken,
        CASE WHEN b.connection_token=a.connection_token AND c.blocked_low=0 AND c.blocked_high=0 AND r.status='accepted' AND r.legacy_generation=c.generation THEN 1 ELSE 0 END canSend
        FROM contacts a LEFT JOIN contacts b ON b.owner_account_id=a.to_account_id AND b.to_account_id=a.owner_account_id
        LEFT JOIN legacy_channels c ON c.low_account_id=min(a.owner_account_id,a.to_account_id) AND c.high_account_id=max(a.owner_account_id,a.to_account_id)
        LEFT JOIN contact_requests r ON r.requester_account_id=c.current_requester_account_id AND r.request_id=c.current_request_id
        WHERE a.owner_account_id=? AND ${peer===undefined?'a.to_account_id>?':'a.to_account_id=?'} ORDER BY a.to_account_id LIMIT ?`).bind(owner,peer??after,peer===undefined?n+1:1).all<{accountId:string;connectionToken:string;canSend:number}>()).results;
    if(peer!==undefined){if(!rows.length)fail(404,'unavailable');return {...rows[0],canSend:!!rows[0].canSend};}
    return {items:rows.slice(0,n).map(row=>({...row,canSend:!!row.canSend})),nextCursor:rows.length>n?prefix+rows[n-1].accountId:null};
}
