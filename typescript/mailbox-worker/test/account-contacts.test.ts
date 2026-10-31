import assert from 'node:assert/strict';
import { test } from 'node:test';
import { start,payload} from './harness.ts';
import { controlHarness, secret } from './control.ts';

async function request(h,a,b){
    const intro=payload('introduction');
    const input={requestVersion:2,requestId:intro.messageId,requesterAccountId:a.accountId,requesterAddressId:a.cardId,recipientAccountId:b.accountId,recipientAddressId:b.cardId,payload:intro};
    return {input,state:await h.ok('/v1/contacts/requests',a,'POST',input)};
}
async function accept(h,a,b,r){
    const decision={requesterAccountId:a.accountId,expectedGeneration:r.state.generation,status:'accepted',reply:null};
    const result=await h.ok('/v1/contacts/requests/'+r.input.requestId+'/decision',b,'POST',decision);
    const own=await h.ok('/v1/contacts',a);
    return {decision,result,token:own.items.find(x=>x.accountId===b.accountId).connectionToken};
}

test('modern requests bind both addresses, acceptance creates reciprocal contacts, deletion invalidates old rounds',async()=>{
    const h=await start();
    try{
        const a=await h.participant(),b=await h.participant(),stranger=await h.participant();
        const bad=payload('forged address');
        assert.equal((await h.request('/v1/contacts/requests',a,'POST',{requestVersion:2,requestId:bad.messageId,requesterAccountId:a.accountId,requesterAddressId:stranger.cardId,recipientAccountId:b.accountId,recipientAddressId:b.cardId,payload:bad})).status,404);
        assert.equal(await h.db.prepare('SELECT count(*) n FROM contact_requests').first('n'),0);
        const r=await request(h,a,b);
        assert.equal(r.state.request.requesterAccountId,a.accountId);assert.equal(r.state.request.requesterAddressId,a.cardId);
        assert.equal((await h.ok('/v1/inbox',b)).items.length,1,'introduction is deliverable before contacts exist');
        const accepted=await accept(h,a,b,r);
        const rows=(await h.db.prepare('SELECT * FROM contacts').all()).results;
        assert.equal(rows.length,2);assert.equal(new Set(rows.map(x=>x.connection_token)).size,1);assert.match(accepted.token,/^[0-9a-f]{64}$/);
        const envelope={version:2,recipientAccountId:b.accountId,contactGeneration:r.state.generation,connectionToken:accepted.token,payload:payload('ordinary')};
        await h.ok('/v1/messages',a,'POST',envelope);
        assert.equal((await h.ok('/v1/inbox',b)).items.length,2);
        await h.ok('/v1/contacts/accounts/'+b.accountId,a,'DELETE');
        assert.deepEqual((await h.db.prepare('SELECT owner_account_id,to_account_id FROM contacts').all()).results,[{owner_account_id:b.accountId,to_account_id:a.accountId}]);
        assert.equal((await h.ok('/v1/inbox',b)).items.length,0);
        assert.equal((await h.request('/v1/contacts/requests/'+r.input.requestId+'/decision',b,'POST',accepted.decision)).status,409);
        assert.equal(await h.db.prepare('SELECT count(*) n FROM contacts').first('n'),1);
        const again=await request(h,a,b),next=await accept(h,a,b,again);
        assert.notEqual(next.token,accepted.token);
        assert.equal((await h.request('/v1/messages',a,'POST',{...envelope,contactGeneration:again.state.generation,payload:payload('stale token')})).status,409);
        await h.ok('/v1/messages',a,'POST',{...envelope,contactGeneration:again.state.generation,connectionToken:next.token,payload:payload('new round')});
        assert.equal(await h.db.prepare('SELECT count(*) n FROM contact_requests').first('n'),2);
    }finally{await h.close();}
});

test('opaque payload boundaries preserve binary and empty BLOBs, oversize has no partial writes',async()=>{
    const h=await start();
    try{
        const a=await h.participant(),b=await h.participant(),r=await request(h,a,b),accepted=await accept(h,a,b,r);
        const envelope=bytes=>({version:2,recipientAccountId:b.accountId,contactGeneration:r.state.generation,connectionToken:accepted.token,payload:payload(bytes)});
        const bytes=Buffer.alloc(65536);for(let i=0;i<bytes.length;i++)bytes[i]=i%256;
        const full=envelope(bytes);await h.ok('/v1/messages',a,'POST',full);
        const stored=await h.db.prepare('SELECT typeof(payload) type,payload,payload_size FROM mailbox_records WHERE message_id=?').bind(full.payload.messageId).first();
        assert.equal(stored.type,'blob');assert.equal(stored.payload_size,65536);assert.deepEqual(Buffer.from(stored.payload),bytes);
        const empty=envelope(Buffer.alloc(0));await h.ok('/v1/messages',a,'POST',empty);
        assert.equal(await h.db.prepare('SELECT typeof(payload) type FROM mailbox_records WHERE message_id=?').bind(empty.payload.messageId).first('type'),'blob');
        const before=await h.db.prepare('SELECT count(*) n FROM mailbox_records').first('n');
        const large=await h.request('/v1/messages',a,'POST',envelope(Buffer.alloc(65537)));
        assert.equal(large.status,413);assert.equal((await large.json()).code,'tooLarge');
        assert.equal(await h.db.prepare('SELECT count(*) n FROM mailbox_records').first('n'),before);
        await h.ok('/v1/inbox/'+empty.payload.messageId+'/ack',b,'POST',{senderAccountId:a.accountId});
        assert.equal(await h.db.prepare('SELECT typeof(payload) type FROM mailbox_records WHERE message_id=?').bind(empty.payload.messageId).first('type'),'null');
    }finally{await h.close();}
});

// Control-specific; deleted with the control.
test('canonical credentials and aliases authenticate the same account and reject conflicting identities',async()=>{
    const h=controlHarness(await start());
    try{
        const control=secret();
        const register=await h.mf.dispatchFetch('http://localhost/v1/accounts',{method:'POST',headers:{'X-Weave-Account-Control':control,'Content-Type':'application/json'},body:'{}'});
        assert.equal(register.status,200);const account=await register.json();assert.equal(account.accountId,account.agentId);
        assert.equal((await h.request('/v1/addresses',control)).status,200);
        const conflict=await h.mf.dispatchFetch('http://localhost/v1/addresses',{headers:{'X-Weave-Account-Control':control,'X-Weave-Mailbox-Control':secret()}});
        assert.equal(conflict.status,401);
        assert.equal((await h.request('/v1/contacts/blocks',control,'PUT',{peerAccountId:'a',peerMailboxId:'b',expectedGeneration:1})).status,400);
    }finally{await h.close();}
});

test('oversize JSON and decoded payload are rejected before cleanup or any database write',async()=>{
    const h=await start();
    try{
        // Issue the token first: issuance is itself a write and is not under test.
        const a=await h.register();await h.token(a);
        await h.db.prepare("DELETE FROM maintenance_state WHERE name='sweep'").run();
        const revision=await h.db.prepare('SELECT token FROM mailbox_revision').first('token');
        const envelope={version:2,recipientAccountId:crypto.randomUUID(),contactGeneration:1,connectionToken:'a'.repeat(64),payload:payload(Buffer.alloc(65537))};
        assert.equal((await h.request('/v1/messages',a,'POST',envelope)).status,413);
        assert.equal(await h.db.prepare('SELECT token FROM mailbox_revision').first('token'),revision,'Oversize must not trigger maintenance writes.');
        assert.equal(await h.db.prepare('SELECT count(*) n FROM maintenance_state').first('n'),0);
        assert.equal((await h.request('/v1/messages',a,'POST','{"padding":"'+'x'.repeat(131072)+'"}')).status,413);
        assert.equal(await h.db.prepare('SELECT token FROM mailbox_revision').first('token'),revision);
        assert.equal(await h.db.prepare('SELECT count(*) n FROM mailbox_records').first('n'),0);
    }finally{await h.close();}
});

test('new server rows share one operation time and unchanged address/card writes preserve timestamps and counters',async()=>{
    const h=await start({bindings:{CLEANUP_MAX_MESSAGES:'0',CLEANUP_TIME_BUDGET_MS:'0'}});
    try{
        const a=await h.participant(),b=await h.participant(),r=await request(h,a,b);
        for(const table of ['accounts','account_addresses','legacy_channels'])assert.equal(await h.db.prepare(`SELECT count(*) n FROM ${table} WHERE created_at IS NULL OR updated_at IS NULL OR created_at<>updated_at`).first('n'),0,table+' new server timestamps');
        await accept(h,a,b,r);assert.equal(await h.db.prepare('SELECT count(*) n FROM contacts WHERE created_at<>updated_at OR created_at IS NULL').first('n'),0);
        await h.db.prepare('UPDATE account_addresses SET updated_at=123 WHERE address_id=?').bind(a.cardId).run();
        const before=await h.db.prepare("SELECT CAST(updated_at AS TEXT) at,CAST(value AS TEXT) value FROM counters WHERE name='record-seq'").first();
        await h.ok('/v1/addresses/'+a.cardId,a,'PUT',{});
        assert.equal(await h.db.prepare('SELECT CAST(updated_at AS TEXT) at FROM account_addresses WHERE address_id=?').bind(a.cardId).first('at'),'123');
        assert.deepEqual(await h.db.prepare("SELECT CAST(updated_at AS TEXT) at,CAST(value AS TEXT) value FROM counters WHERE name='record-seq'").first(),before);
        const card={cardId:'ordinary-noop',visibility:'unlisted',createdAt:new Date().toISOString(),expiresAt:null,revokedAt:null,audienceHint:null,methods:[{methodId:'relay',version:1,transport:'relay',endpoint:'https://example.invalid',instructions:''}]};
        await h.ok('/v1/contacts/cards',a,'PUT',card);await h.db.prepare("UPDATE contact_cards SET updated_at=456 WHERE card_id='ordinary-noop'").run();
        await h.ok('/v1/contacts/cards',a,'PUT',card);assert.equal(await h.db.prepare("SELECT CAST(updated_at AS TEXT) at FROM contact_cards WHERE card_id='ordinary-noop'").first('at'),'456');
    }finally{await h.close();}
});

test('concurrent send, old acceptance replay and owner deletion cannot restore delivery or the deleted contact',async()=>{
    const h=await start({bindings:{CLEANUP_MAX_MESSAGES:'0',CLEANUP_TIME_BUDGET_MS:'0'}});
    try{
        const a=await h.participant(),b=await h.participant(),r=await request(h,a,b),accepted=await accept(h,a,b,r),p=payload('concurrent send');
        const [remove,send,replay]=await Promise.all([
            h.request('/v1/contacts/accounts/'+b.accountId,a,'DELETE'),
            h.request('/v1/messages',b,'POST',{version:2,recipientAccountId:a.accountId,contactGeneration:r.state.generation,connectionToken:accepted.token,payload:p}),
            h.request('/v1/contacts/requests/'+r.input.requestId+'/decision',b,'POST',accepted.decision)
        ]);
        assert.equal(remove.status,200);assert.ok([200,404,409].includes(send.status));assert.ok([200,409].includes(replay.status));
        assert.equal(await h.db.prepare('SELECT count(*) n FROM contacts WHERE owner_account_id=?').bind(a.accountId).first('n'),0);
        assert.equal(await h.db.prepare('SELECT count(*) n FROM contacts WHERE owner_account_id=?').bind(b.accountId).first('n'),1);
        assert.equal((await h.ok('/v1/inbox',a)).items.length,0);
        if(send.status===200){assert.equal((await h.ok('/v1/outbox/'+p.messageId,b)).state,'blocked');assert.equal(await h.db.prepare('SELECT payload FROM mailbox_records WHERE message_id=?').bind(p.messageId).first('payload'),null);}
        assert.equal(await h.db.prepare('SELECT count(*) n FROM contact_requests').first('n'),1);
    }finally{await h.close();}
});

test('sequence exhaustion cannot round, wrap or partially register an address',async()=>{
    const h=await start();
    try{
        const a=await h.register();await h.token(a);await h.db.prepare("UPDATE counters SET value='9223372036854775807' WHERE name='record-seq'").run();
        const revision=await h.db.prepare('SELECT token FROM mailbox_revision').first('token');
        assert.equal((await h.request('/v1/addresses',a,'POST',{})).status,503);
        assert.equal(await h.db.prepare('SELECT count(*) n FROM account_addresses').first('n'),0);
        assert.equal(await h.db.prepare("SELECT CAST(value AS TEXT) value FROM counters WHERE name='record-seq'").first('value'),'9223372036854775807');
        assert.equal(await h.db.prepare('SELECT token FROM mailbox_revision').first('token'),revision);
    }finally{await h.close();}
});

// Control-specific; deleted with the control.
test('original control header preserves strict v1 summaries and HTTP/SSE envelopes while canonical reads retain modern metadata',async()=>{
    const h=controlHarness(await start());let reader;
    const legacy=async(path,control,method='GET',value)=>{
        const response=await h.mf.dispatchFetch('http://localhost'+path,{method,headers:{'X-Weave-Mailbox-Control':control,'Content-Type':'application/json'},...(value===undefined?{}:{body:JSON.stringify(value)})});
        assert.equal(response.status,200,await response.clone().text());return response.json();
    };
    try{
        const a=await h.participant(),b=await h.participant(),r=await request(h,a,b),accepted=await accept(h,a,b,r);
        const old=await legacy('/v1/contacts/requests/'+r.input.requestId+'?requesterMailboxId='+a.accountId,b.control);
        assert.deepEqual(Object.keys(old).sort(),['requestId','cardId','requesterMailboxId','recipientMailboxId','methodId','generation','status','createdAt','expiresAt','requestMessageId','replyMessageId'].sort());
        assert.equal(old.requesterMailboxId,a.accountId);assert.equal(old.status,'accepted');
        await h.ok('/v1/inbox/'+r.input.payload.messageId+'/ack',b.control,'POST',{senderAccountId:a.accountId});
        const envelope={version:2,recipientAccountId:b.accountId,contactGeneration:r.state.generation,connectionToken:accepted.token,payload:payload('mixed client bytes')};
        await h.ok('/v1/messages',a.control,'POST',envelope);
        const original={senderMailboxId:a.accountId,envelope:{version:1,recipientMailboxId:b.accountId,contactGeneration:r.state.generation,payload:envelope.payload}};
        assert.deepEqual((await legacy('/v1/inbox',b.control)).items,[original]);
        const canonical=(await h.ok('/v1/inbox',b.control)).items[0];
        assert.equal(canonical.senderAccountId,a.accountId);assert.equal(canonical.envelope.version,2);assert.equal(canonical.envelope.connectionToken,accepted.token);
        const response=await h.mf.dispatchFetch('http://localhost/v1/inbox/events',{headers:{'X-Weave-Mailbox-Control':b.control}});
        assert.equal(response.status,200);reader=response.body.getReader();let text='',match;
        for(let i=0;i<10&&!match;i++){const part=await reader.read();assert.equal(part.done,false);text+=new TextDecoder().decode(part.value);match=/data: (.+)\n\n/.exec(text);}
        assert.ok(match);assert.deepEqual(JSON.parse(match[1]),original);await reader.cancel();reader=null;
        assert.equal((await legacy('/v1/inbox/'+envelope.payload.messageId+'/ack',b.control,'POST',{senderMailboxId:a.accountId})).state,'acknowledged');
        assert.equal(await h.db.prepare('SELECT protocol_version FROM message_delivery JOIN mailbox_records ON message_seq=seq WHERE message_id=?').bind(envelope.payload.messageId).first('protocol_version'),2);
    }finally{await reader?.cancel();await h.close();}
});
