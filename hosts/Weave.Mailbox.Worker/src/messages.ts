import { Transaction } from './database';
import { fail, identity, object, parse, uuid } from './wire';
import { blocked, current, fingerprint, nowText, readChannel, ticks, validateEnvelope, validatePayload } from './protocol';

export async function effective(tx: Transaction,m: any): Promise<any> {
    const r = m.receipt;
    if (r.state !== 'pending') return r;
    if (ticks(r.expiresAt) <= BigInt(Date.now())*10000n) return {...r,state:'expired',terminalAt:r.expiresAt};
    const c = await readChannel(tx,r.senderMailboxId,r.recipientMailboxId);
    if (!c || blocked(c) || c.generation !== m.generation) return {...r,state:'blocked',terminalAt:c?.updatedAt ?? nowText()};
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
    const quota = await tx.db.prepare(`WITH live AS (SELECT data FROM mailbox_records WHERE kind='message'
        AND json_extract(data,'$.receipt.state')='pending' AND CAST(json_extract(data,'$.expiresTicks') AS INTEGER)>CAST(? AS INTEGER)
        AND COALESCE((owner=? AND json_extract(data,'$.receipt.recipientMailboxId')=?) OR (owner=? AND json_extract(data,'$.receipt.recipientMailboxId')=?),0)=0)
        SELECT (SELECT count(*) FROM mailbox_records WHERE kind='message') total,
        (SELECT count(*) FROM live WHERE json_extract(data,'$.receipt.recipientMailboxId')=?) pending,
        (SELECT sum(json_extract(data,'$.size')) FROM live) bytes`)
        .bind(now,low,high,high,low,e.recipientMailboxId).first<{total:number;pending:number;bytes:number}>();
    const size = atob(e.payload.bytes).length;
    if (!quota || quota.total >= 8000 || quota.pending >= 64 || (quota.bytes ?? 0) + size > 33554432) fail(429,'capacity');
    const receipt = { messageId:e.payload.messageId,senderMailboxId:sender,recipientMailboxId:e.recipientMailboxId,state:'pending',expiresAt:e.payload.expiresAt,terminalAt:null };
    tx.put('message',sender,e.payload.messageId,{receipt,generation:e.contactGeneration,version:1,payload:e.payload,size,
        expiresTicks:ticks(e.payload.expiresAt).toString(),fingerprint:digest,purpose,request});
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
    return admit(tx,owner,e,0,null);
}
export async function ack(tx: Transaction,owner: string,id: string,input: any): Promise<any> {
    uuid(id); object(input,['senderMailboxId']); identity(input.senderMailboxId);
    const m = await tx.get('message',input.senderMailboxId,id);
    if (!m || m.receipt.recipientMailboxId !== owner) fail(404,'unavailable');
    let receipt = await effective(tx,m);
    if (receipt.state === 'pending') receipt = {...receipt,state:'acknowledged',terminalAt:nowText()};
    if (receipt.state !== m.receipt.state) tx.put('message',input.senderMailboxId,id,{...m,receipt,payload:null,size:0});
    return receipt;
}
export async function invalidate(tx: Transaction,c: any,now: string): Promise<void> {
    tx.invalidatedPair = [c.low,c.high];
    // One bounded SQL update avoids a batch statement per pending message.
    tx.writes.push(tx.db.prepare(`UPDATE mailbox_records SET data=json_set(data,
        '$.receipt.state',CASE WHEN CAST(json_extract(data,'$.expiresTicks') AS INTEGER)<=CAST(? AS INTEGER) THEN 'expired' ELSE 'blocked' END,
        '$.receipt.terminalAt',CASE WHEN CAST(json_extract(data,'$.expiresTicks') AS INTEGER)<=CAST(? AS INTEGER) THEN json_extract(data,'$.receipt.expiresAt') ELSE ? END,
        '$.payload',NULL,'$.size',0)
        WHERE kind='message' AND json_extract(data,'$.receipt.state')='pending' AND
        ((owner=? AND json_extract(data,'$.receipt.recipientMailboxId')=?) OR (owner=? AND json_extract(data,'$.receipt.recipientMailboxId')=?))`)
        .bind(ticks(now).toString(),ticks(now).toString(),now,c.low,c.high,c.high,c.low));
    if (c.currentRequest) {
        const r = await tx.get('request',c.currentRequest.requesterMailboxId,c.currentRequest.requestId);
        if (r && !r.terminalAt) tx.put('request',r.summary.requesterMailboxId,r.summary.requestId,{...r,terminalAt:now});
    }
}
