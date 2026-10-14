import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtemp,rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { start,payload,secret } from './harness.mjs';
async function connect(h,a,b){
    const p=payload('recovery introduction'),input={requestVersion:2,requestId:p.messageId,requesterAccountId:a.accountId,requesterAddressId:a.cardId,recipientAccountId:b.accountId,recipientAddressId:b.cardId,payload:p};
    const request=await h.ok('/v1/contacts/requests',a.control,'POST',input),decision={requesterAccountId:a.accountId,expectedGeneration:request.generation,status:'accepted',reply:null};
    await h.ok('/v1/contacts/requests/'+p.messageId+'/decision',b.control,'POST',decision);
    const contact=await h.ok('/v1/contacts/accounts/'+b.accountId,a.control);
    return {request,input,decision,contact};
}
async function snapshot(db){
    const tables=(await db.prepare("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE '_cf_%' ORDER BY name").all()).results;
    const result={};
    for(const {name} of tables){
        const columns=(await db.prepare(`PRAGMA table_info("${name}")`).all()).results;
        const selection=columns.map(c=>c.type==='INTEGER'?`CAST("${c.name}" AS TEXT) "${c.name}"`:`"${c.name}"`).join(',');
        result[name]=(await db.prepare(`SELECT ${selection} FROM "${name}"`).all()).results;
    }
    return result;
}
test('retained recovery bundle reads post-cutover writes and performs no rate, cleanup, registration or business writes',async()=>{
    const persist=await mkdtemp(join(tmpdir(),'weave-current-recovery-'));let h;
    try{
        h=await start({persist});const a=await h.participant(),b=await h.participant(),first=await connect(h,a,b);
        const send=async text=>{const p=payload(text);await h.ok('/v1/messages',a.control,'POST',{version:2,recipientAccountId:b.accountId,contactGeneration:first.request.generation,connectionToken:first.contact.connectionToken,payload:p});return p;};
        const acked=await send('ack before recovery');await h.ok('/v1/inbox/'+acked.messageId+'/ack',b.control,'POST',{senderAccountId:a.accountId});
        const expired=await send('cleanup before recovery');
        // Advance only this synthetic record's lifetime so the real scheduled
        // cleanup path can run without changing the production clock.
        await h.db.prepare('UPDATE mailbox_records SET expires_at=? WHERE message_id=?').bind((BigInt(Date.now()-9*86400000)*10000n).toString(),expired.messageId).run();
        await h.db.prepare("DELETE FROM maintenance_state WHERE name='sweep'").run();
        assert.equal((await (await h.mf.getWorker()).scheduled({scheduledTime:Date.now(),cron:'*/5 * * * *'})).outcome,'ok');
        assert.equal(await h.db.prepare('SELECT count(*) n FROM mailbox_records WHERE message_id=?').bind(expired.messageId).first('n'),0);
        const c=await h.register({displayName:'Registered after cutover'});await h.address(c,{label:'New public address',isPublic:true});
        await h.ok('/v1/addresses/'+b.cardId,b.control,'DELETE');const replacement=await h.address(b);
        await h.ok('/v1/contacts/accounts/'+b.accountId,a.control,'DELETE');
        const next=await connect(h,a,{...b,cardId:replacement.addressId});assert.notEqual(next.contact.connectionToken,first.contact.connectionToken);
        await h.ok('/v1/contacts/accounts/'+b.accountId,a.control,'DELETE');
        assert.equal((await h.request('/v1/contacts/requests/'+next.input.requestId+'/decision',b.control,'POST',next.decision)).status,409);
        const before=await snapshot(h.db);await h.close();h=await start({persist,migrate:false,scriptPath:'dist/recovery.js'});
        assert.equal((await h.ok('/v1/accounts',c.control,'POST',{})).accountId,c.accountId);
        assert.equal((await h.ok('/v1/addresses?includeRevoked=true',b.control)).items.find(x=>x.addressId===b.cardId).revokedAt!==null,true);
        assert.equal((await h.ok('/v1/contacts',a.control)).items.length,0);
        assert.equal((await h.ok('/v1/contacts',b.control)).items[0].canSend,false);
        assert.equal((await h.ok('/v1/contacts/requests',a.control)).items.length,2);
        assert.equal((await h.ok('/v1/outbox/'+acked.messageId,a.control)).state,'acknowledged');
        assert.equal((await h.request('/v1/outbox/'+expired.messageId,a.control)).status,404);
        await h.ok('/v1/directory');await h.ok('/v1/contacts/cards');
        for(const [path,owner,method,body] of [['/v1/addresses',c,'POST',{}],['/v1/inbox/events',b,'GET'],['/v1/contacts/requests/'+next.input.requestId+'/decision',b,'POST',next.decision],['/v1/inbox/'+acked.messageId+'/ack',b,'POST',{senderAccountId:a.accountId}]])assert.equal((await h.request(path,owner.control,method,body)).status,503);
        assert.equal((await h.request('/v1/accounts',secret(),'POST',{})).status,401);
        assert.equal((await (await h.mf.getWorker()).scheduled({scheduledTime:Date.now(),cron:'*/5 * * * *'})).outcome,'ok');
        assert.deepEqual(await snapshot(h.db),before,'Recovery must neither restore frozen state nor write during read-only service.');
    }finally{try{await h?.close();}finally{await rm(persist,{recursive:true,force:true});}}
});
