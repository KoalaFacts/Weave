import assert from 'node:assert/strict';
import {test} from 'node:test';
import {build} from 'esbuild';
import {withTestGrant,start,payload,type AgentCredential} from './harness.ts';
import type {Env} from '../src/database.ts';
import {remoteMcp,type PrivateAccess} from '../src/remote-mcp.ts';
// Evaluate the bundled production modules, as the other private MCP tests do.
const bundle=async(entry:string)=>{
    const built=await build({entryPoints:[entry],bundle:true,write:false,platform:'node',format:'esm',logLevel:'silent'});
    return import('data:text/javascript;base64,'+Buffer.from(built.outputFiles[0].contents).toString('base64'));
};
const {executePrivateMcp:executeWithGrant}=await bundle('src/remote-private.ts') as typeof import('../src/remote-private.ts');
const executePrivateMcp=withTestGrant(executeWithGrant);
const {sweep}=await bundle('src/cleanup.ts') as typeof import('../src/cleanup.ts');

const origin='https://demo.test';
type Harness=Awaited<ReturnType<typeof start>>;
type Participant=AgentCredential&{cardId:string};
const environment=(h:Harness,cleanup='0'):Env=>({db:h.db,REGISTRATION_ENABLED:'true',REGISTRATION_MAX_ACCOUNTS:'100',
    REGISTRATION_PER_IP_PER_HOUR:'10',REGISTRATION_GLOBAL_PER_HOUR:'100',CLEANUP_MAX_MESSAGES:cleanup,CLEANUP_TIME_BUDGET_MS:'2000'});
const call=async(env:Env,owner:string,name:string,args:Record<string,unknown>)=>{
    const response=await executePrivateMcp(env,origin,owner,name,args);
    return {status:response.status,body:await response.json()};
};
const ok=async(env:Env,owner:string,name:string,args:Record<string,unknown>)=>{
    const answer=await call(env,owner,name,args);
    assert.equal(answer.status,200,name+': '+JSON.stringify(answer.body));
    return answer.body;
};
const text=(value:string)=>({parts:[{text:value}]});
const v7=/^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
const millis=(id:string)=>BigInt('0x'+id.replaceAll('-','').slice(0,12));
async function stored(h:Harness,messageId:string){
    const row=await h.db.prepare('SELECT hex(payload) body,payload_encoding,CAST(created_at AS TEXT) created,CAST(expires_at AS TEXT) expires FROM mailbox_records WHERE message_id=?')
        .bind(messageId).first<{body:string;payload_encoding:string;created:string;expires:string}>();
    assert.ok(row,'message '+messageId+' is stored');
    return {...row,message:JSON.parse(Buffer.from(row.body,'hex').toString('utf8'))};
}
const count=async(h:Harness,sql:string,...args:string[])=>Number(await h.db.prepare(sql).bind(...args).first('n'));
const sendCount=(h:Harness,owner:string)=>count(h,"SELECT coalesce(sum(count),0) n FROM rate_limits WHERE policy='agent-send' AND bucket_key=?",owner);
async function pair(h:Harness,env:Env):Promise<{a:Participant;b:Participant}> {
    const a=await h.participant(),b=await h.participant();
    const requested=await ok(env,a.accountId,'request_contact',{requesterAddressId:a.cardId,recipientAccountId:b.accountId,
        recipientAddressId:b.cardId,message:text('hello, may we talk?')});
    await ok(env,b.accountId,'decide_contact',{requestId:requested.request.requestId,requesterAccountId:a.accountId,
        expectedGeneration:String(requested.generation),status:'accepted'});
    return {a,b};
}

test('sendMessage_TextOnly_ServerBuildsA2aMessage',async()=>{
    const h=await start();
    try{
        const env=environment(h),{a,b}=await pair(h,env);
        const receipt=await ok(env,a.accountId,'send_message',{recipientAccountId:b.accountId,contactGeneration:'1',message:text('ping')});
        assert.match(receipt.messageId,v7);
        assert.equal(receipt.state,'pending');
        const row=await stored(h,receipt.messageId);
        assert.equal(row.payload_encoding,'application/json');
        assert.deepEqual(row.message,{messageId:receipt.messageId,role:'ROLE_USER',parts:[{text:'ping'}]});
        assert.equal(millis(receipt.messageId)*10000n,BigInt(row.created),'messageId encodes the envelope createdAt millisecond');
        assert.equal(BigInt(row.expires)-BigInt(row.created),864000000000n,'expiresAt is createdAt plus 24 hours');
    }finally{await h.close();}
});

test('sendMessage_DataPart_RoundTrips',async()=>{
    const h=await start();
    try{
        const env=environment(h),{a,b}=await pair(h,env);
        const message={contextId:'ctx-42',parts:[{data:{kind:'order',items:[1,2.5,'three',null,{nested:true}]},mediaType:'application/json'},{text:'see data'}]};
        const receipt=await ok(env,a.accountId,'send_message',{recipientAccountId:b.accountId,message});
        assert.deepEqual((await stored(h,receipt.messageId)).message,{messageId:receipt.messageId,role:'ROLE_USER',...message});
    }finally{await h.close();}
});

test('sendMessage_PartWithTextAndData_Invalid',async()=>{
    const h=await start();
    try{
        const env=environment(h),{a,b}=await pair(h,env),before=await count(h,'SELECT count(*) n FROM mailbox_records');
        const answer=await call(env,a.accountId,'send_message',{recipientAccountId:b.accountId,message:{parts:[{text:'x',data:{y:1}}]}});
        assert.equal(answer.status,400);
        assert.equal(answer.body.field,'message.parts[0]');
        assert.equal(await count(h,'SELECT count(*) n FROM mailbox_records'),before);
    }finally{await h.close();}
});

test('sendMessage_Over64KiB_Invalid',async()=>{
    const h=await start();
    try{
        const env=environment(h),{a,b}=await pair(h,env),before=await count(h,'SELECT count(*) n FROM mailbox_records');
        const answer=await call(env,a.accountId,'send_message',{recipientAccountId:b.accountId,message:text('a'.repeat(65536))});
        assert.equal(answer.status,400);
        assert.equal(answer.body.field,'message');
        assert.equal(await count(h,'SELECT count(*) n FROM mailbox_records'),before);
        assert.equal(await sendCount(h,a.accountId),0);
    }finally{await h.close();}
});

test('sendMessage_NoGeneration_UsesCurrent',async()=>{
    const h=await start();
    try{
        const env=environment(h),{a,b}=await pair(h,env);
        const receipt=await ok(env,a.accountId,'send_message',{recipientAccountId:b.accountId,message:text('no generation')});
        const inbox=await ok(env,b.accountId,'list_my_inbox',{});
        const item=inbox.items.find((x:any)=>x.envelope.payload.messageId===receipt.messageId);
        assert.equal(String(item.envelope.contactGeneration),'1');
    }finally{await h.close();}
});

test('sendMessage_NotConnected_ToolError',async()=>{
    const h=await start();
    try{
        const env=environment(h),a=await h.participant(),b=await h.participant();
        const answer=await call(env,a.accountId,'send_message',{recipientAccountId:b.accountId,message:text('stranger')});
        assert.equal(answer.status,409);
        assert.equal(answer.body.code,'not-connected');
        // A pending, not yet accepted request is not a connection either.
        await ok(env,a.accountId,'request_contact',{requesterAddressId:a.cardId,recipientAccountId:b.accountId,recipientAddressId:b.cardId,message:text('hi')});
        assert.equal((await call(env,a.accountId,'send_message',{recipientAccountId:b.accountId,message:text('too early')})).status,409);
        assert.equal(await count(h,'SELECT count(*) n FROM mailbox_records WHERE sender_account_id=?',a.accountId),1,'only the introduction is stored');
    }finally{await h.close();}
});

test('sendMessage_SameKeySameInput_OriginalReceiptNoLimitUse',async()=>{
    const h=await start();
    try{
        const env=environment(h),{a,b}=await pair(h,env);
        const input={recipientAccountId:b.accountId,message:text('exactly once'),idempotencyKey:'send-1'};
        const first=await ok(env,a.accountId,'send_message',input);
        const used=await sendCount(h,a.accountId);
        assert.equal(used,1);
        // Key order in the input does not change the request.
        const again=await ok(env,a.accountId,'send_message',{idempotencyKey:'send-1',message:text('exactly once'),recipientAccountId:b.accountId});
        assert.deepEqual(again,first);
        assert.equal(await sendCount(h,a.accountId),used,'a replay consumes no hourly send');
        assert.equal(await count(h,'SELECT count(*) n FROM mailbox_records m JOIN message_delivery d ON d.message_seq=m.seq WHERE m.sender_account_id=? AND d.purpose=0',a.accountId),1,'one ordinary message');
        // The same key on another account is that account's own key.
        const back=await ok(env,b.accountId,'send_message',{recipientAccountId:a.accountId,message:text('exactly once'),idempotencyKey:'send-1'});
        assert.notEqual(back.messageId,first.messageId);
        // Without a key a retry is a new message.
        const unkeyed={recipientAccountId:b.accountId,message:text('twice')};
        assert.notEqual((await ok(env,a.accountId,'send_message',unkeyed)).messageId,(await ok(env,a.accountId,'send_message',unkeyed)).messageId);
    }finally{await h.close();}
});

test('sendMessage_SameKeyDifferentText_409OneMessage',async()=>{
    const h=await start();
    try{
        const env=environment(h),{a,b}=await pair(h,env);
        const first=await ok(env,a.accountId,'send_message',{recipientAccountId:b.accountId,message:text('original'),idempotencyKey:'k:1'});
        for(const changed of [
            {recipientAccountId:b.accountId,message:text('changed'),idempotencyKey:'k:1'},
            {recipientAccountId:b.accountId,contactGeneration:'1',message:text('original'),idempotencyKey:'k:1'},
        ]){
            const answer=await call(env,a.accountId,'send_message',changed);
            assert.equal(answer.status,409,JSON.stringify(answer.body));
            assert.equal(answer.body.code,'idempotency-conflict');
        }
        // A different tool cannot reuse the key either.
        const other=await call(env,a.accountId,'request_contact',{requesterAddressId:a.cardId,recipientAccountId:b.accountId,
            recipientAddressId:b.cardId,message:text('original'),idempotencyKey:'k:1'});
        assert.equal(other.status,409);
        assert.equal(other.body.code,'idempotency-conflict');
        const rows=await h.db.prepare("SELECT message_id FROM mailbox_records WHERE sender_account_id=? AND payload_encoding='application/json' AND message_id<>?")
            .bind(a.accountId,(await h.db.prepare("SELECT request_id FROM contact_requests WHERE requester_account_id=?").bind(a.accountId).first<{request_id:string}>())!.request_id).all<{message_id:string}>();
        assert.deepEqual(rows.results.map(r=>r.message_id),[first.messageId],'exactly one message for the key');
        assert.deepEqual((await stored(h,first.messageId)).message.parts,[{text:'original'}],'the stored body is not overwritten');
        assert.equal(await sendCount(h,a.accountId),1);
        const invalid=await call(env,a.accountId,'send_message',{recipientAccountId:b.accountId,message:text('x'),idempotencyKey:'has space'});
        assert.equal(invalid.status,400);assert.equal(invalid.body.field,'idempotencyKey');
    }finally{await h.close();}
});

test('requestContact_TextIntroduction_Stored',async()=>{
    const h=await start();
    try{
        const env=environment(h),a=await h.participant(),b=await h.participant();
        const input={requesterAddressId:a.cardId,recipientAccountId:b.accountId,recipientAddressId:b.cardId,message:text('I am A'),idempotencyKey:'intro'};
        const requested=await ok(env,a.accountId,'request_contact',input);
        const requestId=requested.request.requestId;
        assert.match(requestId,v7);
        assert.equal(requested.request.status,'pending');
        assert.deepEqual((await stored(h,requestId)).message,{messageId:requestId,role:'ROLE_USER',parts:[{text:'I am A'}]});
        const contactLimit=await count(h,"SELECT coalesce(sum(count),0) n FROM rate_limits WHERE policy='agent-contact' AND bucket_key=?",a.accountId);
        assert.deepEqual(await ok(env,a.accountId,'request_contact',input),requested,'a keyed retry returns the original relation');
        assert.equal(await count(h,"SELECT coalesce(sum(count),0) n FROM rate_limits WHERE policy='agent-contact' AND bucket_key=?",a.accountId),contactLimit);
        assert.equal(await count(h,'SELECT count(*) n FROM contact_requests WHERE requester_account_id=?',a.accountId),1);
        const inbox=await ok(env,b.accountId,'list_my_inbox',{});
        assert.deepEqual(inbox.items.map((x:any)=>x.message?.parts),[[{text:'I am A'}]]);
    }finally{await h.close();}
});

test('decideContact_ReplyMessage',async()=>{
    const h=await start();
    try{
        const env=environment(h),a=await h.participant(),b=await h.participant();
        const requested=await ok(env,a.accountId,'request_contact',{requesterAddressId:a.cardId,recipientAccountId:b.accountId,
            recipientAddressId:b.cardId,message:text('hello')});
        const decision={requestId:requested.request.requestId,requesterAccountId:a.accountId,expectedGeneration:String(requested.generation),
            status:'accepted',reply:{parts:[{text:'welcome'}],contextId:'intro'},idempotencyKey:'decide-1'};
        const accepted=await ok(env,b.accountId,'decide_contact',decision);
        assert.equal(accepted.isConnected,true);
        assert.deepEqual(await ok(env,b.accountId,'decide_contact',decision),accepted,'a keyed retry returns the original relation');
        const inbox=await ok(env,a.accountId,'list_my_inbox',{});
        assert.equal(inbox.items.length,1);
        const item=inbox.items[0];
        assert.deepEqual({...item.message,messageId:undefined},{messageId:undefined,contextId:'intro',role:'ROLE_USER',parts:[{text:'welcome'}]});
        assert.equal(item.message.messageId,item.envelope.payload.messageId);
    }finally{await h.close();}
});

async function connectOverHttp(h:Harness,a:Participant,b:Participant){
    const intro=payload('introduction');
    const state=await h.ok('/v1/contacts/requests',a,'POST',{requestVersion:2,requestId:intro.messageId,requesterAccountId:a.accountId,
        requesterAddressId:a.cardId,recipientAccountId:b.accountId,recipientAddressId:b.cardId,payload:intro});
    await h.ok('/v1/contacts/requests/'+intro.messageId+'/decision',b,'POST',
        {requesterAccountId:a.accountId,expectedGeneration:state.generation,status:'accepted',reply:null});
    const token=(await h.ok('/v1/contacts',b)).items.find((x:{accountId:string})=>x.accountId===a.accountId).connectionToken;
    return {generation:state.generation,token};
}
async function httpSend(h:Harness,from:Participant,to:Participant,contact:{generation:string;token:string},payloadEncoding:string,
    body:string|((messageId:string)=>string)){
    const base=payload();
    const sent={...base,payloadEncoding,bytes:Buffer.from(typeof body==='string'?body:body(base.messageId)).toString('base64')};
    await h.ok('/v1/messages',from,'POST',{version:2,recipientAccountId:to.accountId,contactGeneration:contact.generation,connectionToken:contact.token,payload:sent});
    return sent;
}
const inboxItem=async(env:Env,owner:string,messageId:string)=>
    (await ok(env,owner,'list_my_inbox',{})).items.find((x:any)=>x.envelope.payload.messageId===messageId);

test('inbox_A2aBody_ParsedMessageNoBytes',async()=>{
    const h=await start();
    try{
        const env=environment(h),a=await h.participant(),b=await h.participant(),contact=await connectOverHttp(h,a,b);
        // An HTTP peer's body that merely looks like an A2A Message, even one shaped like a notice, is still the peer's content.
        const lookalike=(messageId:string)=>({messageId,role:'ROLE_AGENT',parts:[{data:{kind:'key-added',kid:'x'.repeat(43),connectionToken:'peer-value',
            nested:{connectionToken:'deeper'}}}]});
        const sent=await httpSend(h,b,a,contact,'application/json',id=>JSON.stringify(lookalike(id)));
        const item=await inboxItem(env,a.accountId,sent.messageId);
        assert.deepEqual(item.message,lookalike(sent.messageId),'the parsed Message is exactly what the sender wrote, including connectionToken-named fields');
        assert.equal(Object.hasOwn(item.envelope.payload,'bytes'),false);
        assert.deepEqual(Object.keys(item.envelope.payload).sort(),['createdAt','expiresAt','messageId','payloadEncoding']);
        assert.equal(item.senderAccountId,b.accountId);
        const page=await ok(env,a.accountId,'list_my_inbox',{});
        assert.equal(Object.hasOwn(page,'notices'),false,'peer content never becomes a server notice');
    }finally{await h.close();}
});

test('inbox_HttpPeerPlainText_KeepsBytes',async()=>{
    const h=await start();
    try{
        const env=environment(h),a=await h.participant(),b=await h.participant(),contact=await connectOverHttp(h,a,b);
        const shaped=JSON.stringify({messageId:'m',role:'ROLE_USER',parts:[]});
        const sent=await httpSend(h,b,a,contact,'text/plain',shaped);
        const item=await inboxItem(env,a.accountId,sent.messageId);
        assert.equal(Object.hasOwn(item,'message'),false);
        assert.equal(Buffer.from(item.envelope.payload.bytes,'base64').toString('utf8'),shaped);
    }finally{await h.close();}
});

test('inbox_A2aBodyWithOtherMessageId_KeepsBytes',async()=>{
    const h=await start();
    try{
        const env=environment(h),a=await h.participant(),b=await h.participant(),contact=await connectOverHttp(h,a,b);
        const body=JSON.stringify({messageId:'not-the-envelope-id',role:'ROLE_USER',parts:[{text:'hi'}]});
        const sent=await httpSend(h,b,a,contact,'application/json',body);
        const item=await inboxItem(env,a.accountId,sent.messageId);
        assert.equal(Object.hasOwn(item,'message'),false);
        assert.equal(Buffer.from(item.envelope.payload.bytes,'base64').toString('utf8'),body);
    }finally{await h.close();}
});

test('sendMessage_DataPartNamedConnectionToken_RoundTripsToInbox',async()=>{
    const h=await start();
    try{
        const env=environment(h),{a,b}=await pair(h,env);
        const parts=[{data:{connectionToken:'x',inner:[{connectionToken:'y'}]}}];
        const receipt=await ok(env,a.accountId,'send_message',{recipientAccountId:b.accountId,message:{parts}});
        assert.deepEqual((await inboxItem(env,b.accountId,receipt.messageId)).message.parts,parts);
    }finally{await h.close();}
});

test('sendMessage_ConcurrentSameKey_OneMessageSameResult',async()=>{
    const h=await start();
    try{
        const env=environment(h),{a,b}=await pair(h,env);
        const input={recipientAccountId:b.accountId,message:text('raced'),idempotencyKey:'race'};
        const [first,second]=await Promise.all([ok(env,a.accountId,'send_message',input),ok(env,a.accountId,'send_message',input)]);
        assert.deepEqual(second,first);
        assert.equal(await count(h,'SELECT count(*) n FROM mailbox_records m JOIN message_delivery d ON d.message_seq=m.seq WHERE m.sender_account_id=? AND d.purpose=0',a.accountId),1);
        assert.equal(await sendCount(h,a.accountId),1);
    }finally{await h.close();}
});

test('idempotency_ExpiredRowsPrunedByLaterWrite',async()=>{
    const h=await start();
    try{
        const env=environment(h),{a,b}=await pair(h,env);
        await ok(env,a.accountId,'send_message',{recipientAccountId:b.accountId,message:text('old'),idempotencyKey:'old'});
        const day=864000000000n,now=BigInt(Date.now())*10000n;
        await h.db.prepare("UPDATE mcp_idempotency SET created_at=CAST(? AS INTEGER) WHERE idempotency_key='old'").bind((now-day-10000n).toString()).run();
        // The sweep is disabled (CLEANUP_MAX_MESSAGES=0, as deployed); a later keyed write still prunes the expired row.
        await ok(env,b.accountId,'send_message',{recipientAccountId:a.accountId,message:text('later'),idempotencyKey:'later'});
        const keys=(await h.db.prepare('SELECT idempotency_key FROM mcp_idempotency ORDER BY idempotency_key').all<{idempotency_key:string}>()).results;
        assert.deepEqual(keys.map(r=>r.idempotency_key),['later']);
    }finally{await h.close();}
});

test('inbox_MalformedJson_KeepsBytes',async()=>{
    const h=await start();
    try{
        const env=environment(h),a=await h.participant(),b=await h.participant(),contact=await connectOverHttp(h,a,b);
        for(const body of ['{"messageId":"m","role":', JSON.stringify({messageId:'m',role:'ROLE_USER'}), JSON.stringify([1])]){
            const sent=await httpSend(h,b,a,contact,'application/json',body);
            const item=await inboxItem(env,a.accountId,sent.messageId);
            assert.equal(Object.hasOwn(item,'message'),false,body);
            assert.equal(Buffer.from(item.envelope.payload.bytes,'base64').toString('utf8'),body);
        }
    }finally{await h.close();}
});

test('idempotency_Prune24h',async()=>{
    const h=await start();
    try{
        const env=environment(h),{a,b}=await pair(h,env);
        const first=await ok(env,a.accountId,'send_message',{recipientAccountId:b.accountId,message:text('old'),idempotencyKey:'old'});
        await ok(env,a.accountId,'send_message',{recipientAccountId:b.accountId,message:text('fresh'),idempotencyKey:'fresh'});
        const day=864000000000n,now=BigInt(Date.now())*10000n;
        await h.db.prepare("UPDATE mcp_idempotency SET created_at=CAST(? AS INTEGER) WHERE idempotency_key='old'").bind((now-day-10000n).toString()).run();
        // An expired key is free again even before the prune runs.
        const reused=await ok(env,a.accountId,'send_message',{recipientAccountId:b.accountId,message:text('new body'),idempotencyKey:'old'});
        assert.notEqual(reused.messageId,first.messageId);
        await h.db.prepare("UPDATE mcp_idempotency SET created_at=CAST(? AS INTEGER) WHERE idempotency_key='old'").bind((now-day-10000n).toString()).run();
        await h.db.prepare("DELETE FROM maintenance_state WHERE name='sweep'").run();
        await sweep(environment(h,'128'));
        const keys=(await h.db.prepare('SELECT idempotency_key FROM mcp_idempotency WHERE account_id=? ORDER BY idempotency_key').bind(a.accountId).all<{idempotency_key:string}>()).results;
        assert.deepEqual(keys.map(r=>r.idempotency_key),['fresh']);
    }finally{await h.close();}
});

test('remoteMcp_ResolvableConflicts_NameTheCode',async()=>{
    for(const code of ['idempotency-conflict','not-connected']){
        const access:PrivateAccess={tools:[],scope:()=>'weave:messages.send',authorize:async()=>'11111111-2222-4333-8444-555555555555',
            invoke:async()=>Response.json({status:409,title:'x',code},{status:409})};
        const response=await remoteMcp(new Request(origin+'/mcp',{method:'POST',headers:{'Content-Type':'application/json',
            Accept:'application/json, text/event-stream',Authorization:'Bearer synthetic'},
            body:JSON.stringify({jsonrpc:'2.0',id:1,method:'tools/call',params:{name:'send_message',arguments:{}}})}),async()=>new Response('{}'),access);
        const frame=await response.json();
        assert.equal(frame.result.isError,true);
        assert.deepEqual(frame.result.structuredContent,{status:409,code});
        assert.match(frame.result.content[0].text,new RegExp(code+'.*Nothing was written'));
    }
});

const nested=(depth:number):unknown=>{let value:unknown=0;for(let i=0;i<depth;i++)value=[value];return value;};
const strangerIntroduction=async(h:Harness,from:Participant,to:Participant,body:(messageId:string)=>string)=>{
    const intro=payload();
    const sent={...intro,payloadEncoding:'application/json',bytes:Buffer.from(body(intro.messageId)).toString('base64')};
    await h.ok('/v1/contacts/requests',from,'POST',{requestVersion:2,requestId:intro.messageId,requesterAccountId:from.accountId,
        requesterAddressId:from.cardId,recipientAccountId:to.accountId,recipientAddressId:to.cardId,payload:sent});
    return sent;
};

test('inbox_StrangerDeeplyNestedIntroduction_200KeepsBytes',async()=>{
    const h=await start();
    try{
        const env=environment(h),attacker=await h.participant(),friend=await h.participant(),victim=await h.participant();
        // Built by hand over HTTP, so no sender-side MCP limit applies.
        const deep=await strangerIntroduction(h,attacker,victim,id=>JSON.stringify({messageId:id,role:'ROLE_USER',parts:[{data:{x:nested(3000)}}]}));
        const normal=await strangerIntroduction(h,friend,victim,id=>JSON.stringify({messageId:id,role:'ROLE_USER',parts:[{text:'hi'}]}));
        const answer=await call(env,victim.accountId,'list_my_inbox',{});
        assert.equal(answer.status,200,JSON.stringify(answer.body).slice(0,200));
        const item=(id:string)=>answer.body.items.find((x:any)=>x.envelope.payload.messageId===id);
        assert.equal(Object.hasOwn(item(deep.messageId),'message'),false);
        assert.equal(item(deep.messageId).envelope.payload.bytes,deep.bytes);
        assert.deepEqual(item(normal.messageId).message.parts,[{text:'hi'}]);
    }finally{await h.close();}
});

test('inbox_PeerDataWithAliasKeys_ReturnedExactlyAsSent',async()=>{
    const h=await start();
    try{
        const env=environment(h),a=await h.participant(),b=await h.participant(),contact=await connectOverHttp(h,a,b);
        const message=(messageId:string)=>({messageId,role:'ROLE_USER',parts:[{data:{agentId:'x',senderMailboxId:'y'}}]});
        const sent=await httpSend(h,b,a,contact,'application/json',id=>JSON.stringify(message(id)));
        const item=await inboxItem(env,a.accountId,sent.messageId);
        assert.deepEqual(item.message,message(sent.messageId));
        assert.deepEqual(Object.keys(item.message.parts[0].data),['agentId','senderMailboxId']);
        assert.equal(item.senderAccountId,b.accountId,'server data keeps its canonical names');
    }finally{await h.close();}
});

test('sendMessage_DataDeeperThan16_InvalidNothingWritten',async()=>{
    const h=await start();
    try{
        const env=environment(h),{a,b}=await pair(h,env),before=await count(h,'SELECT count(*) n FROM mailbox_records');
        // data itself is level 1; 15 nested arrays reach depth 16 and are accepted.
        await ok(env,a.accountId,'send_message',{recipientAccountId:b.accountId,message:{parts:[{data:{x:nested(15)}}]}});
        for(const depth of [16,3000]){
            const answer=await call(env,a.accountId,'send_message',{recipientAccountId:b.accountId,message:{parts:[{data:{x:nested(depth)}}]},idempotencyKey:'deep'+depth});
            assert.equal(answer.status,400,String(depth));
            assert.equal(answer.body.field,'message.parts[0].data');
        }
        assert.equal(await count(h,'SELECT count(*) n FROM mailbox_records'),before+1);
    }finally{await h.close();}
});

test('remoteMcpInbox_PeerGenerationAndLargeInteger_NotRewrittenInStructuredContent',async()=>{
    const h=await start();
    try{
        const env=environment(h),a=await h.participant(),b=await h.participant(),contact=await connectOverHttp(h,a,b);
        const sent=await httpSend(h,b,a,contact,'application/json',
            id=>`{"messageId":"${id}","role":"ROLE_USER","parts":[{"data":{"generation":7,"big":9007199254740993}}]}`);
        const access:PrivateAccess={tools:[],scope:name=>name==='list_my_inbox'?'weave:messages.read':null,authorize:async()=>a.accountId,
            invoke:(owner,name,args)=>executePrivateMcp(env,origin,owner,name,args)};
        const response=await remoteMcp(new Request(origin+'/mcp',{method:'POST',headers:{'content-type':'application/json',accept:'application/json, text/event-stream',
            authorization:'Bearer t'},body:JSON.stringify({jsonrpc:'2.0',id:1,method:'tools/call',params:{name:'list_my_inbox',arguments:{}}})}),
            async()=>{throw Error('no public read');},access);
        const result=(await response.json()).result;
        const item=result.structuredContent.items.find((x:any)=>x.envelope.payload.messageId===sent.messageId);
        const data=item.message.parts[0].data;
        assert.equal(data.generation,7,'a peer key named generation stays the number the peer sent');
        // structuredContent is parsed JSON: a peer integer beyond 2^53 is the nearest JS number there; the text content is exact.
        assert.equal(data.big,9007199254740992);
        assert.match(result.content[0].text,/"generation":7,"big":9007199254740993/);
        assert.equal(typeof item.envelope.contactGeneration,'string','server generations stay exact decimal strings');
    }finally{await h.close();}
});
