import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtemp,rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { start,payload} from './harness.ts';
async function connect(h,a,b){
    const p=payload('recovery introduction'),input={requestVersion:2,requestId:p.messageId,requesterAccountId:a.accountId,requesterAddressId:a.cardId,recipientAccountId:b.accountId,recipientAddressId:b.cardId,payload:p};
    const request=await h.ok('/v1/contacts/requests',a,'POST',input),decision={requesterAccountId:a.accountId,expectedGeneration:request.generation,status:'accepted',reply:null};
    await h.ok('/v1/contacts/requests/'+p.messageId+'/decision',b,'POST',decision);
    const contact=await h.ok('/v1/contacts/accounts/'+b.accountId,a);
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
        const send=async text=>{const p=payload(text);await h.ok('/v1/messages',a,'POST',{version:2,recipientAccountId:b.accountId,contactGeneration:first.request.generation,connectionToken:first.contact.connectionToken,payload:p});return p;};
        const acked=await send('ack before recovery');await h.ok('/v1/inbox/'+acked.messageId+'/ack',b,'POST',{senderAccountId:a.accountId});
        const expired=await send('cleanup before recovery');
        // Advance only this synthetic record's lifetime so the real scheduled
        // cleanup path can run without changing the production clock.
        await h.db.prepare('UPDATE mailbox_records SET expires_at=? WHERE message_id=?').bind((BigInt(Date.now()-9*86400000)*10000n).toString(),expired.messageId).run();
        await h.db.prepare("DELETE FROM maintenance_state WHERE name='sweep'").run();
        assert.equal((await (await h.mf.getWorker()).scheduled({scheduledTime:Date.now(),cron:'*/5 * * * *'})).outcome,'ok');
        assert.equal(await h.db.prepare('SELECT count(*) n FROM mailbox_records WHERE message_id=?').bind(expired.messageId).first('n'),0);
        const c=await h.register({displayName:'Registered after cutover'});await h.address(c,{label:'New public address',isPublic:true});
        await h.ok('/v1/addresses/'+b.cardId,b,'DELETE');const replacement=await h.address(b);
        await h.ok('/v1/contacts/accounts/'+b.accountId,a,'DELETE');
        const next=await connect(h,a,{...b,cardId:replacement.addressId});assert.notEqual(next.contact.connectionToken,first.contact.connectionToken);
        await h.ok('/v1/contacts/accounts/'+b.accountId,a,'DELETE');
        assert.equal((await h.request('/v1/contacts/requests/'+next.input.requestId+'/decision',b,'POST',next.decision)).status,409);
        const before=await snapshot(h.db);await h.close();h=await start({persist,migrate:false,scriptPath:'dist/recovery.js'});
        assert.equal((await h.ok('/v1/accounts',c,'POST',{})).accountId,c.accountId);
        assert.equal((await h.ok('/v1/addresses?includeRevoked=true',b)).items.find(x=>x.addressId===b.cardId).revokedAt!==null,true);
        assert.equal((await h.ok('/v1/contacts',a)).items.length,0);
        assert.equal((await h.ok('/v1/contacts',b)).items[0].canSend,false);
        assert.equal((await h.ok('/v1/contacts/requests',a)).items.length,2);
        assert.equal((await h.ok('/v1/outbox/'+acked.messageId,a)).state,'acknowledged');
        assert.equal((await h.request('/v1/outbox/'+expired.messageId,a)).status,404);
        await h.ok('/v1/directory');await h.ok('/v1/contacts/cards');
        for(const [path,owner,method,body] of [['/v1/addresses',c,'POST',{}],['/v1/inbox/events',b,'GET'],['/v1/contacts/requests/'+next.input.requestId+'/decision',b,'POST',next.decision],['/v1/inbox/'+acked.messageId+'/ack',b,'POST',{senderAccountId:a.accountId}]])assert.equal((await h.request(path,owner,method,body)).status,503);
        assert.equal((await (await h.mf.getWorker()).scheduled({scheduledTime:Date.now(),cron:'*/5 * * * *'})).outcome,'ok');
        assert.deepEqual(await snapshot(h.db),before,'Recovery must neither restore frozen state nor write during read-only service.');
    }finally{try{await h?.close();}finally{await rm(persist,{recursive:true,force:true});}}
});
test('address history pages revoked and active addresses for the owner only',async()=>{
    const h=await start();
    try{
        const owner=await h.register(),peer=await h.register();
        const [revoked,active]=[await h.address(owner),await h.address(owner)].sort((a,b)=>a.addressId.localeCompare(b.addressId));
        const foreign=await h.address(peer);
        await h.ok('/v1/addresses/'+revoked.addressId,owner,'DELETE');
        await h.ok('/v1/addresses/'+foreign.addressId,peer,'DELETE');
        const rows=async()=>(await h.db.prepare('SELECT * FROM account_addresses ORDER BY address_id').all()).results,before=await rows();
        assert.deepEqual((await h.ok('/v1/addresses',owner)).items.map(x=>x.addressId),[active.addressId]);
        const first=await h.ok('/v1/addresses?includeRevoked=true&limit=1',owner);
        assert.equal(first.items[0].addressId,revoked.addressId);assert.ok(first.items[0].revokedAt);assert.ok(first.nextCursor);
        const second=await h.ok('/v1/addresses?includeRevoked=true&limit=1&afterCursor='+encodeURIComponent(first.nextCursor),owner);
        assert.deepEqual(second.items.map(x=>x.addressId),[active.addressId]);
        assert.equal(second.items[0].revokedAt,null);assert.equal(second.nextCursor,null);
        for(const item of [...first.items,...second.items])assert.equal(item.accountId,owner.accountId);
        assert.deepEqual(await rows(),before);
    }finally{await h.close();}
});
