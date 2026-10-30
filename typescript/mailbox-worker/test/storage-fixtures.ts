import { createHash } from 'node:crypto';
import { utcTicks, isoUtc } from '../src/time.ts';
const at=value=>value==null?null:utcTicks(value).toString();
const iso=value=>value==null?null:isoUtc(value).replace(/(\.\d{3})0000Z$/,'$1Z');
const parameters=JSON.stringify({algorithm:'SHA-256',inputEncoding:'utf-8',digestEncoding:'hex-lower',normalization:'none',salt:null,rounds:1});
async function account(db,id){
    await db.prepare("INSERT OR IGNORE INTO accounts(account_id,control_hash,control_hash_scheme,control_hash_parameters) VALUES(?,?,'sha256-utf8-v1',?)")
        .bind(id,createHash('sha256').update('synthetic fixture '+id).digest('hex'),parameters).run();
}
const next=db=>db.prepare("UPDATE counters SET value=value+1 WHERE name='record-seq'");
const seq="(SELECT value FROM counters WHERE name='record-seq')";
export async function seedMessage(db,record){
    const r=record.receipt,p=record.payload;
    await account(db,r.senderMailboxId);await account(db,r.recipientMailboxId);
    const bytes=p===null?null:Uint8Array.from(Buffer.from(p.bytes,'base64')).buffer;
    await db.batch([
        next(db),db.prepare(`INSERT INTO mailbox_records(seq,message_id,sender_account_id,recipient_account_id,payload,payload_size,payload_encoding,created_at,expires_at) VALUES(${seq},?,?,?,?,?,?,?,?)`)
            .bind(r.messageId,r.senderMailboxId,r.recipientMailboxId,bytes,bytes?.byteLength??0,p?.payloadEncoding??null,at(p?.createdAt),at(r.expiresAt)),
        db.prepare(`INSERT INTO message_delivery(message_seq,state,terminal_at,protocol_version,purpose,legacy_generation,fingerprint,fingerprint_scheme,requester_account_id,request_id)
            VALUES(${seq},?,?,?,?,?,?,'legacy-envelope-v1',?,?)`).bind(r.state,at(r.terminalAt),record.version??1,record.purpose??0,String(record.generation??1),record.fingerprint??'synthetic-message',record.request?.requesterMailboxId??null,record.request?.requestId??null)
    ]);
}
export async function seedRequest(db,record){
    const s=record.summary;await account(db,s.requesterMailboxId);await account(db,s.recipientMailboxId);
    const address=await db.prepare('SELECT 1 found FROM account_addresses WHERE address_id=?').bind(s.cardId).first();
    if(!address&&!await db.prepare('SELECT 1 found FROM contact_cards WHERE card_id=?').bind(s.cardId).first())await db.batch([
        next(db),db.prepare(`INSERT INTO contact_cards(card_id,owner_account_id,seq,visibility,created_at,methods) VALUES(?,?,${seq},'unlisted',?,?)`)
            .bind(s.cardId,s.recipientMailboxId,at(s.createdAt),JSON.stringify([{methodId:'relay',version:1,transport:'relay',endpoint:'https://example.invalid',instructions:'synthetic fixture'}]))
    ]);
    await db.batch([next(db),db.prepare(`INSERT INTO contact_requests(requester_account_id,request_id,seq,recipient_account_id,request_version,requester_address_id,recipient_address_id,target_card_id,method_id,legacy_generation,status,created_at,expires_at,request_message_id,reply_message_id,fingerprint,decision_fingerprint,updated_at,terminal_at)
        VALUES(?,?,${seq},?,1,NULL,?,?,?,?,?,?,?,?,?,?,?,?,?)`).bind(s.requesterMailboxId,s.requestId,s.recipientMailboxId,address?s.cardId:null,address?null:s.cardId,s.methodId,String(s.generation),s.status,at(s.createdAt),at(s.expiresAt),s.requestMessageId,s.replyMessageId,record.fingerprint??'synthetic-history',record.decisionFingerprint??null,at(record.updatedAt??s.createdAt),at(record.terminalAt))]);
}
export async function seedChannel(db,c){
    await db.prepare(`INSERT INTO legacy_channels(low_account_id,high_account_id,generation,blocked_low,blocked_high,current_requester_account_id,current_request_id,updated_at) VALUES(?,?,?,?,?,?,?,?)`)
        .bind(c.low,c.high,String(c.generation),c.blockedLow?1:0,c.blockedHigh?1:0,c.currentRequest?.requesterMailboxId??null,c.currentRequest?.requestId??null,at(c.updatedAt)).run();
}
export async function messageRow(db,id){
    const r=await db.prepare(`SELECT m.message_id,m.sender_account_id,m.recipient_account_id,m.payload,m.payload_size,m.payload_encoding,
        CAST(m.created_at AS TEXT) created_at,CAST(m.expires_at AS TEXT) expires_at,d.state,CAST(d.terminal_at AS TEXT) terminal_at
        FROM mailbox_records m JOIN message_delivery d ON d.message_seq=m.seq WHERE m.message_id=?`).bind(id).first();
    return r&&{payload:r.payload===null?null:{messageId:r.message_id,createdAt:iso(r.created_at),expiresAt:iso(r.expires_at),payloadEncoding:r.payload_encoding,bytes:Buffer.from(r.payload).toString('base64')},size:r.payload_size,
        receipt:{state:r.state,terminalAt:iso(r.terminal_at)}};
}
export async function relationSnapshot(db){return {requests:(await db.prepare('SELECT * FROM contact_requests ORDER BY seq').all()).results,channels:(await db.prepare('SELECT * FROM legacy_channels ORDER BY low_account_id,high_account_id').all()).results};}
export async function messageSnapshot(db){return (await db.prepare('SELECT m.*,d.* FROM mailbox_records m JOIN message_delivery d ON d.message_seq=m.seq ORDER BY m.seq').all()).results;}
