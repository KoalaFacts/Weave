import { eligible } from './cards';
import { Transaction } from './database';
import { fail, generation, identity, object, uuid } from './wire';
import { admit, invalidate } from './messages';
import { establish } from './contact-links';
import { blocked, channelView, current, fingerprint, MAX_GENERATION, nowText, pair, readChannel, relation, ticks, timestampMatches, validatePayload, writeChannel } from './protocol';

export async function requestContact(tx: Transaction,owner: string,input: any): Promise<any> {
    const modern=input.requestVersion===2;
    if(modern){
        object(input,['requestVersion','requestId','requesterAccountId','requesterAddressId','recipientAccountId','recipientAddressId','payload']);
        for(const key of ['requesterAccountId','requesterAddressId','recipientAccountId','recipientAddressId'])uuid(input[key]);
        if(input.requesterAccountId!==owner||input.recipientAccountId===owner)fail(404,'unavailable');
    }else{object(input,['requestId','cardId','methodId','payload']);identity(input.cardId);identity(input.methodId);}
    validatePayload(input.payload); timestampMatches(input.requestId,input.payload.createdAt);
    const digest = await fingerprint(modern?input:{cardId:input.cardId,methodId:input.methodId,payload:input.payload});
    const previous = await tx.get('request',owner,input.requestId);
    if (previous) {
        if (previous.fingerprint !== digest) fail(409,'conflict');
        return relation(await readChannel(tx,owner,previous.summary.recipientMailboxId),previous);
    }
    validatePayload(input.payload,true);
    const cardId=modern?input.recipientAddressId:input.cardId,methodId=modern?'relay':input.methodId;
    const card = await tx.get('card','',cardId);
    if (!eligible(card)) fail(404,'unavailable');
    if(modern){
        const source=await tx.get('card','',input.requesterAddressId);
        if(!eligible(source)||!source._address||source.ownerMailboxId!==owner||!card._address||card.ownerMailboxId!==input.recipientAccountId)fail(404,'unavailable');
    }
    const recipient = card.ownerMailboxId;
    if (recipient === owner || card.methods.find((m: any) => m.methodId === methodId)?.transport !== 'relay') fail();
    const old = await readChannel(tx,owner,recipient);
    if (old && blocked(old)) fail(404,'unavailable');
    if (old?.generation === MAX_GENERATION) fail(429,'capacity');
    if (old?.currentRequest) {
        const r = await tx.get('request',old.currentRequest.requesterMailboxId,old.currentRequest.requestId);
        if (r && !r.terminalAt && (r.summary.status === 'accepted' || ticks(r.summary.expiresAt) > BigInt(Date.now())*10000n)) fail(409,'conflict');
    }
    const quota = await tx.db.prepare(`SELECT
        (SELECT count(*) FROM contact_requests) requests,
        (SELECT count(*) FROM legacy_channels) channels,
        (SELECT count(*) FROM contact_requests WHERE recipient_account_id=?
            AND status IN ('pending','needsAction') AND expires_at>CAST(? AS INTEGER) AND terminal_at IS NULL) pending`)
        .bind(recipient,(BigInt(Date.now())*10000n).toString()).first<{requests:number;channels:number;pending:number}>();
    if (!quota || quota.requests >= 1000 || !old && quota.channels >= 5000 || quota.pending >= 32) fail(429,'capacity');
    const now = nowText(tx); if (old) await invalidate(tx,old,now);
    const [low,high] = pair(owner,recipient);
    const c = {low,high,generation:(old?.generation ?? 0n)+1n,blockedLow:false,blockedHigh:false,currentRequest:{requesterMailboxId:owner,requestId:input.requestId},updatedAt:now};
    const summary = {requestId:input.requestId,cardId,requesterMailboxId:owner,recipientMailboxId:recipient,methodId,
        generation:c.generation,status:'pending',createdAt:input.payload.createdAt,expiresAt:input.payload.expiresAt,requestMessageId:input.payload.messageId,replyMessageId:null,
        requestVersion:modern?2:1,requesterAddressId:modern?input.requesterAddressId:null,recipientAddressId:card._address?cardId:null};
    const r = {summary,fingerprint:digest,decisionFingerprint:null,updatedAt:now,terminalAt:null,expiresTicks:ticks(summary.expiresAt).toString(),
        requestVersion:modern?2:1,requesterAddressId:modern?input.requesterAddressId:null,recipientAddressId:card._address?cardId:null,targetCardId:card._address?null:cardId};
    tx.put('request',owner,input.requestId,r);
    writeChannel(tx,c);
    await admit(tx,owner,{version:1,recipientMailboxId:recipient,contactGeneration:c.generation,payload:input.payload},1,c.currentRequest);
    return relation(c,r);
}
export async function decide(tx: Transaction,owner: string,id: string,input: any): Promise<any> {
    uuid(id); object(input,['requesterMailboxId','expectedGeneration','status','reply']); identity(input.requesterMailboxId); generation(input.expectedGeneration);
    if (!['pending','needsAction','accepted','rejected'].includes(input.status)) fail();
    const r = await tx.get('request',input.requesterMailboxId,id);
    if (!r || r.summary.recipientMailboxId !== owner) fail(404,'unavailable');
    const c = await readChannel(tx,owner,input.requesterMailboxId);
    if (blocked(c)) fail(404,'unavailable');
    if (c.generation !== input.expectedGeneration || !current(c,r.summary)) fail(409,'conflict');
    if (input.reply !== null) validatePayload(input.reply);
    const digest = await fingerprint({requestId:id,...input});
    if (digest === r.decisionFingerprint) return relation(c,r);
    if (!['pending','needsAction'].includes(r.summary.status)) fail(409,'conflict');
    if (ticks(r.summary.expiresAt) <= BigInt(Date.now())*10000n) fail(410,'expired');
    if (input.reply !== null) {
        validatePayload(input.reply,true);
        await admit(tx,owner,{version:1,recipientMailboxId:input.requesterMailboxId,contactGeneration:c.generation,payload:input.reply},2,c.currentRequest);
    }
    const now = nowText(tx);
    if(input.status==='accepted')establish(tx,owner,input.requesterMailboxId,now);
    const updated = {...r,summary:{...r.summary,status:input.status,replyMessageId:input.reply?.messageId ?? r.summary.replyMessageId},decisionFingerprint:digest,updatedAt:now,terminalAt:input.status === 'rejected' ? now : null};
    tx.put('request',input.requesterMailboxId,id,updated); writeChannel(tx,{...c,updatedAt:now}); return relation(c,updated);
}
export async function setBlocked(tx: Transaction,owner: string,input: any,value: boolean): Promise<any> {
    object(input,['peerMailboxId','expectedGeneration']); identity(input.peerMailboxId); generation(input.expectedGeneration);
    const c = await readChannel(tx,owner,input.peerMailboxId); if (!c) fail(404,'unavailable');
    if (c.generation !== input.expectedGeneration) fail(409,'conflict');
    const flag = c.low === owner ? 'blockedLow' : 'blockedHigh';
    if (c[flag] === value) return channelView(c,owner);
    if (c.generation === MAX_GENERATION) fail(429,'capacity');
    const now = nowText(tx); await invalidate(tx,c,now);
    const changed = {...c,[flag]:value,generation:c.generation+1n,currentRequest:null,updatedAt:now}; writeChannel(tx,changed); return channelView(changed,owner);
}
