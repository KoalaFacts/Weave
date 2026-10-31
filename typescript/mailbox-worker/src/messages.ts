import { Transaction } from './database';
import { fail, identity, object, uuid } from './wire';
import { blocked, current, fingerprint, nowText, readChannel, ticks, validateEnvelope, validatePayload } from './protocol';
import { connection } from './contact-links';
import { agentLimits, agentTier, requireHourly } from './agent-limits.ts';

export async function effective(tx: Transaction,m: any): Promise<any> {
    const r = m.receipt;
    if (r.state !== 'pending') return r;
    if (ticks(r.expiresAt) <= BigInt(Date.now())*10000n) return {...r,state:'expired',terminalAt:r.expiresAt};
    const c = await readChannel(tx,r.senderMailboxId,r.recipientMailboxId);
    if (!c || blocked(c) || c.generation !== m.generation) return {...r,state:'blocked',terminalAt:c?.updatedAt ?? nowText(tx)};
    if(m.purpose===0){
        const token=await connection(tx,r.senderMailboxId,r.recipientMailboxId);
        if(!token||m.connectionToken!==null&&m.connectionToken!==token)return {...r,state:'blocked',terminalAt:c.updatedAt};
    }
    return r;
}
export async function terminalize(tx: Transaction,m: any): Promise<any> {
    const receipt = await effective(tx,m);
    if (receipt.state !== m.receipt.state) tx.put('message',receipt.senderMailboxId,receipt.messageId,{...m,receipt,payload:null,size:0});
    return receipt;
}
export async function admit(tx: Transaction,sender: string,e: any,purpose: number,request: any): Promise<any> {
    const digest = await fingerprint({envelope:e,purpose,requestId:request?.requestId ?? null});
    const existing = await tx.get('message',sender,e.payload.messageId);
    if (existing) { if (existing.fingerprint !== digest) fail(409,'conflict'); return terminalize(tx,existing); }
    // Counts exclude expired pending bodies; physical cleanup is bounded separately.
    const [low,high] = tx.invalidatedPair ?? [null,null];
    const now = (BigInt(Date.now())*10000n).toString();
    const quota = await tx.db.prepare(`WITH live AS (SELECT m.* FROM mailbox_records m JOIN message_delivery d ON d.message_seq=m.seq
        WHERE d.state='pending' AND m.expires_at>CAST(? AS INTEGER)
        AND COALESCE((sender_account_id=? AND recipient_account_id=?) OR (sender_account_id=? AND recipient_account_id=?),0)=0)
        SELECT (SELECT count(*) FROM mailbox_records) total,
        (SELECT count(*) FROM live WHERE recipient_account_id=?) pending,
        (SELECT sum(payload_size) FROM live) bytes`)
        .bind(now,low,high,high,low,e.recipientMailboxId).first<{total:number;pending:number;bytes:number}>();
    const size = atob(e.payload.bytes).length;
    if (!quota || quota.total >= 8000 || quota.pending >= 64 || (quota.bytes ?? 0) + size > 33554432) fail(429,'capacity');
    const receipt = { messageId:e.payload.messageId,senderMailboxId:sender,recipientMailboxId:e.recipientMailboxId,state:'pending',expiresAt:e.payload.expiresAt,terminalAt:null };
    tx.put('message',sender,e.payload.messageId,{receipt,generation:e.contactGeneration,version:e.version,payload:e.payload,size,
        expiresTicks:ticks(e.payload.expiresAt).toString(),fingerprint:digest,purpose,request,connectionToken:purpose===0?await connection(tx,sender,e.recipientMailboxId):null,
        fingerprintScheme:e.version===2?'account-envelope-v2':'legacy-envelope-v1'});
    return receipt;
}
export async function send(tx: Transaction,owner: string,e: any): Promise<any> {
    validateEnvelope(e,owner);
    const existing = await tx.get('message',owner,e.payload.messageId);
    if (existing) {
        if (existing.fingerprint !== await fingerprint({envelope:e,purpose:0,requestId:null})) fail(409,'conflict');
        return terminalize(tx,existing);
    }
    validatePayload(e.payload,true);
    const c = await readChannel(tx,owner,e.recipientMailboxId);
    if (!c || blocked(c)) fail(404,'unavailable');
    if (c.generation !== e.contactGeneration) fail(409,'conflict');
    const r = c.currentRequest && await tx.get('request',c.currentRequest.requesterMailboxId,c.currentRequest.requestId);
    if (!r || r.summary.status !== 'accepted' || !current(c,r.summary)) fail(404,'unavailable');
    const token=await connection(tx,owner,e.recipientMailboxId);
    if(!token)fail(404,'unavailable');
    if(e.version===2&&e.connectionToken!==token)fail(409,'conflict');
    // Only new message IDs reach this point, so a retry returns its receipt without consuming the hourly limit.
    await requireHourly(tx,'agent-send',owner,agentLimits[await agentTier(tx,owner)].messagesPerHour);
    return admit(tx,owner,e,0,null);
}
/** Account-authorized send without disclosing channel tokens to the caller.
 * Retained IDs use their ORIGINAL token, even after contact removal/replacement.
 * Only the token is recovered: send() still checks the complete supplied envelope
 * fingerprint and returns its existing receipt without admitting another message.
 */
export async function sendWithResolvedConnection(tx: Transaction,owner: string,recipient: string,generation: bigint,payload: any): Promise<any> {
    validatePayload(payload);
    const existing=await tx.get('message',owner,payload.messageId);
    if(existing&&(existing.version!==2||existing.purpose!==0||!existing.connectionToken))fail(409,'conflict');
    const token=existing?existing.connectionToken:await connection(tx,owner,recipient);
    if(!token)fail(404,'unavailable');
    return send(tx,owner,{version:2,recipientMailboxId:recipient,contactGeneration:generation,connectionToken:token,payload});
}
export async function ack(tx: Transaction,owner: string,id: string,input: any): Promise<any> {
    uuid(id); object(input,['senderMailboxId']); identity(input.senderMailboxId);
    const m = await tx.get('message',input.senderMailboxId,id);
    if (!m || m.receipt.recipientMailboxId !== owner) fail(404,'unavailable');
    let receipt = await effective(tx,m);
    if (receipt.state === 'pending') receipt = {...receipt,state:'acknowledged',terminalAt:nowText(tx)};
    if (receipt.state !== m.receipt.state) tx.put('message',input.senderMailboxId,id,{...m,receipt,payload:null,size:0});
    return receipt;
}
export async function invalidate(tx: Transaction,c: any,now: string): Promise<void> {
    tx.invalidatedPair = [c.low,c.high];
    // One bounded SQL update avoids a batch statement per pending message.
    const condition='((sender_account_id=? AND recipient_account_id=?) OR (sender_account_id=? AND recipient_account_id=?))';
    const args=[c.low,c.high,c.high,c.low],at=ticks(now).toString();
    tx.writes.push(tx.db.prepare(`UPDATE mailbox_records_legacy SET data=json_set(data,'$.payload',NULL,'$.size',0),payload_cleared_at=?
        WHERE kind='message' AND json_extract(data,'$.payload') IS NOT NULL AND seq IN
        (SELECT m.seq FROM mailbox_records m JOIN message_delivery d ON d.message_seq=m.seq WHERE d.state='pending' AND ${condition})`).bind(at,...args));
    tx.writes.push(tx.db.prepare(`UPDATE mailbox_records SET payload=NULL,payload_size=0,updated_at=? WHERE ${condition}
        AND seq IN (SELECT message_seq FROM message_delivery WHERE state='pending')`).bind(at,...args));
    tx.writes.push(tx.db.prepare(`UPDATE message_delivery SET state=CASE WHEN (SELECT expires_at FROM mailbox_records WHERE seq=message_seq)<=CAST(? AS INTEGER) THEN 'expired' ELSE 'blocked' END,
        terminal_at=min(CAST(? AS INTEGER),(SELECT expires_at FROM mailbox_records WHERE seq=message_seq)),updated_at=?
        WHERE state='pending' AND message_seq IN (SELECT seq FROM mailbox_records WHERE ${condition})`).bind(at,at,at,...args));
    if (c.currentRequest) {
        const r = await tx.get('request',c.currentRequest.requesterMailboxId,c.currentRequest.requestId);
        if (r && !r.terminalAt) tx.put('request',r.summary.requesterMailboxId,r.summary.requestId,{...r,terminalAt:now});
    }
}
