import assert from 'node:assert/strict';
import {test} from 'node:test';
import {randomUUID} from 'node:crypto';
import {start,payload,type AgentCredential} from './harness.ts';

type Harness=Awaited<ReturnType<typeof start>>;
type Participant=AgentCredential&{cardId:string};

/** Two agents with addresses; `b`'s is public. `connect` makes them accepted contacts over HTTP. */
async function pair(h:Harness){
    const a=await h.participant(),b=await h.register();
    const card=await h.address(b,{isPublic:true});
    return {a,b:{...b,cardId:card.addressId} as Participant};
}
async function connect(h:Harness,a:Participant,b:Participant){
    const intro=payload('introduction');
    const state=await h.ok('/v1/contacts/requests',a,'POST',{requestVersion:2,requestId:intro.messageId,requesterAccountId:a.accountId,
        requesterAddressId:a.cardId,recipientAccountId:b.accountId,recipientAddressId:b.cardId,payload:intro});
    await h.ok('/v1/contacts/requests/'+intro.messageId+'/decision',b,'POST',
        {requesterAccountId:a.accountId,expectedGeneration:state.generation,status:'accepted',reply:null});
    return state.generation as string;
}
const rpc=(message:Record<string,unknown>,extra:Record<string,unknown>={})=>({jsonrpc:'2.0',id:7,method:'SendMessage',params:{message},...extra});
const userMessage=(text:string,messageId:string=randomUUID(),more:Record<string,unknown>={})=>({messageId,role:'ROLE_USER',parts:[{text}],...more});
async function send(h:Harness,as:AgentCredential|null,addressId:string,value:unknown,headers:Record<string,string>={}){
    const response=await h.fetchAs(as,'http://localhost/a2a/'+addressId,{method:'POST',headers:{'Content-Type':'application/json',...headers},
        body:typeof value==='string'?value:JSON.stringify(value)});
    return {status:response.status,version:response.headers.get('A2A-Version'),body:await response.json() as any};
}
/** The recipient's inbox items whose sender is `from`, with each body decoded. */
async function inboxFrom(h:Harness,owner:AgentCredential,from:AgentCredential){
    const page=await h.ok('/v1/inbox',owner);
    return page.items.filter((item:any)=>item.senderAccountId===from.accountId&&item.envelope.payload.payloadEncoding==='application/json')
        .map((item:any)=>({envelope:item.envelope,body:JSON.parse(Buffer.from(item.envelope.payload.bytes,'base64').toString('utf8'))}));
}

test('a2aSendMessage_AcceptedContact_QueuedAsServerBuiltA2aMessage',async()=>{
    const h=await start();
    try{
        const {a,b}=await pair(h);await connect(h,a,b);
        const answer=await send(h,a,b.cardId,rpc(userMessage('hello over A2A',randomUUID(),{contextId:'ctx-1',parts:[{text:'hello over A2A'},{data:{n:1}}]})));
        assert.equal(answer.status,200);assert.equal(answer.version,'1.0');
        assert.equal(answer.body.jsonrpc,'2.0');assert.equal(answer.body.id,7);assert.equal(answer.body.error,undefined);
        const reply=answer.body.result.message;
        assert.equal(reply.role,'ROLE_AGENT');assert.equal(reply.contextId,'ctx-1');
        assert.match(reply.parts[0].text,/does not mean the recipient has read/);
        const receipt=reply.parts[1].data.weave.receipt;
        assert.equal(reply.parts[1].data.weave.status,'queued');assert.equal(receipt.state,'pending');
        assert.equal(answer.body.result.task,undefined,'Weave keeps no task');
        const items=await inboxFrom(h,b,a);
        assert.equal(items.length,1);
        // The stored Message is built by the server: its own UUIDv7 messageId, ROLE_USER, the caller's parts and contextId.
        assert.deepEqual(items[0].body,{messageId:items[0].envelope.payload.messageId,contextId:'ctx-1',role:'ROLE_USER',parts:[{text:'hello over A2A'},{data:{n:1}}]});
        assert.equal(receipt.messageId,items[0].envelope.payload.messageId);
        assert.equal(JSON.stringify(answer.body).includes('connectionToken'),false,'channel tokens never leave the server');
    }finally{await h.close();}
});

test('a2aSendMessage_RetrySameMessageId_SameReceiptOneDelivery_DifferentContentConflicts',async()=>{
    const h=await start();
    try{
        const {a,b}=await pair(h);await connect(h,a,b);
        const message=userMessage('once');
        const first=await send(h,a,b.cardId,rpc(message)),second=await send(h,a,b.cardId,rpc(message));
        assert.equal(second.body.result.message.parts[1].data.weave.receipt.messageId,first.body.result.message.parts[1].data.weave.receipt.messageId);
        assert.equal((await inboxFrom(h,b,a)).length,1);
        const changed=await send(h,a,b.cardId,rpc({...message,parts:[{text:'different'}]}));
        assert.equal(changed.body.error.code,-32000);assert.equal(changed.body.error.data.code,'idempotency-conflict');
        assert.equal((await inboxFrom(h,b,a)).length,1);
        // The same messageId and content to another contact's address is a conflict, never the receipt for b.
        const c=await h.register(),cCard=await h.address(c,{isPublic:true});
        await connect(h,a,{...c,cardId:cCard.addressId});
        const elsewhere=await send(h,a,cCard.addressId,rpc(message));
        assert.equal(elsewhere.body.error.data.code,'idempotency-conflict');assert.equal((await inboxFrom(h,c,a)).length,0);
        // A retry after b's address left the directory still gets its receipt, and nothing new is sent.
        await h.ok('/v1/addresses/'+b.cardId,b,'DELETE');
        const late=await send(h,a,b.cardId,rpc(message));
        assert.equal(late.body.result.message.parts[1].data.weave.receipt.messageId,first.body.result.message.parts[1].data.weave.receipt.messageId);
        assert.equal((await inboxFrom(h,b,a)).length,1);
        // A new message to the revoked address is refused like the directory.
        assert.equal((await send(h,a,b.cardId,rpc(userMessage('after revoke')))).body.error.data.code,'unavailable');
    }finally{await h.close();}
});

test('a2aSendMessage_RetryAfterBlockOrAck_ReportsTheReceiptAsItStands',async()=>{
    const h=await start();
    try{
        const {a,b}=await pair(h);const generation=await connect(h,a,b);
        const acked=userMessage('to be acknowledged'),blocked=userMessage('to be blocked');
        const first=await send(h,a,b.cardId,rpc(acked));
        const ackId=first.body.result.message.parts[1].data.weave.receipt.messageId;
        await h.ok('/v1/inbox/'+ackId+'/ack',b,'POST',{senderAccountId:a.accountId});
        const afterAck=(await send(h,a,b.cardId,rpc(acked))).body.result.message;
        assert.equal(afterAck.parts[1].data.weave.status,'acknowledged');assert.equal(afterAck.parts[1].data.weave.receipt.state,'acknowledged');
        assert.match(afterAck.parts[0].text,/acknowledged receiving/);
        await send(h,a,b.cardId,rpc(blocked));
        await h.ok('/v1/contacts/blocks',b,'PUT',{peerAccountId:a.accountId,expectedGeneration:generation});
        const afterBlock=(await send(h,a,b.cardId,rpc(blocked))).body.result.message;
        assert.equal(afterBlock.parts[1].data.weave.status,'delivery-stopped');assert.equal(afterBlock.parts[1].data.weave.receipt.state,'blocked');
        // Blocking stops delivery but does not prove the recipient never read it, so the answer says that is unknown.
        assert.match(afterBlock.parts[0].text,/stopped delivering this message \(blocked\)/);assert.match(afterBlock.parts[0].text,/unknown/);
        assert.doesNotMatch(afterBlock.parts[0].text,/Queued|was not delivered/);
    }finally{await h.close();}
});

test('a2aSendMessage_NotAContactOrBlockedOrNotPublic_RefusedAndNothingStored',async()=>{
    const h=await start();
    try{
        const {a,b}=await pair(h);
        const notConnected=await send(h,a,b.cardId,rpc(userMessage('hi')));
        assert.equal(notConnected.status,200);assert.equal(notConnected.body.error.code,-32000);assert.equal(notConnected.body.error.data.code,'not-connected');
        const generation=await connect(h,a,b);
        await h.ok('/v1/contacts/blocks',b,'PUT',{peerAccountId:a.accountId,expectedGeneration:generation});
        const blocked=await send(h,a,b.cardId,rpc(userMessage('hi')));
        assert.equal(blocked.body.error.code,-32000);assert.equal(blocked.body.error.data.code,'not-connected');assert.equal(blocked.body.result,undefined);
        // A private address and an unknown one answer like the directory: unavailable.
        assert.equal((await send(h,a,a.cardId,rpc(userMessage('x')))).body.error.data.code,'unavailable');
        assert.equal((await send(h,a,randomUUID(),rpc(userMessage('x')))).body.error.data.code,'unavailable');
        // A public address outside its effective window is unavailable too, even to an accepted contact.
        const later=await h.register(),laterCard=await h.address(later,{isPublic:true});
        await connect(h,a,{...later,cardId:laterCard.addressId});
        await h.ok('/v1/addresses/'+laterCard.addressId,later,'PUT',{label:null,isPublic:true,effectiveFrom:new Date(Date.now()+86400000).toISOString(),effectiveTo:null,directUri:null});
        assert.equal((await send(h,a,laterCard.addressId,rpc(userMessage('too early')))).body.error.data.code,'unavailable');
        // An agent cannot send to its own public address.
        const own=await h.address(a,{isPublic:true});
        const self=await send(h,a,own.addressId,rpc(userMessage('me')));
        assert.equal(self.body.error.code,-32602);assert.equal(self.body.error.data.field,'addressId');assert.equal((await inboxFrom(h,a,a)).length,0);
        assert.equal((await inboxFrom(h,b,a)).length,0);
    }finally{await h.close();}
});

test('a2aSendMessage_NoCredential_401AndNoJsonRpc',async()=>{
    const h=await start();
    try{
        const {b}=await pair(h);
        const answer=await send(h,null,b.cardId,rpc(userMessage('hi')));
        assert.equal(answer.status,401);assert.equal(answer.body.code,'unauthenticated');
        const get=await h.fetchAs(null,'http://localhost/a2a/'+b.cardId);
        assert.equal(get.status,401);
    }finally{await h.close();}
});

test('a2aSendMessage_MalformedRequests_JsonRpcErrorsNamingTheProblem',async()=>{
    const h=await start();
    try{
        const {a,b}=await pair(h);await connect(h,a,b);
        const error=async(value:unknown,headers:Record<string,string>={})=>(await send(h,a,b.cardId,value,headers)).body.error;
        assert.equal((await error('{not json')).code,-32700);
        assert.equal((await error([rpc(userMessage('x'))])).code,-32600,'no batches');
        assert.equal((await error({jsonrpc:'2.0',method:'SendMessage',params:{message:userMessage('x')}})).code,-32600,'no notifications');
        assert.equal((await error({...rpc(userMessage('x')),method:'message/send'})).code,-32601,'the v0.3 name is not served');
        assert.equal((await error({...rpc(userMessage('x')),method:'GetTask'})).code,-32601);
        assert.deepEqual(await error(rpc(userMessage('x')),{'A2A-Version':'0.3'}),
            {code:-32600,message:'Version not supported',data:{reason:'version-not-supported',supported:['1.0']}});
        for(const [message,field] of [
            [userMessage('x',undefined,{taskId:'t1'}),'params.message.taskId'],
            [userMessage('x',undefined,{role:'ROLE_AGENT'}),'params.message.role'],
            [userMessage('x','has space'),'params.message.messageId'],
            [userMessage('x',undefined,{metadata:{a:1}}),'params.message.metadata'],
            [userMessage('x',undefined,{parts:[{url:'https://example.test/x'}]}),'params.message.parts[0]'],
            [userMessage('x',undefined,{parts:[{data:[1,2]}]}),'params.message.parts[0].data'],
        ] as const){
            const e=await error(rpc(message));
            assert.equal(e.code,-32602,field);assert.equal(e.data.field,field);
        }
        assert.equal((await error({...rpc(userMessage('x')),params:{message:userMessage('x'),tenant:'other'}})).data.field,'params.tenant');
        assert.equal((await inboxFrom(h,b,a)).length,0,'no malformed request stored anything');
        // A version 1.0 header, a configuration and empty metadata/extensions are accepted.
        const accepted=await send(h,a,b.cardId,{...rpc(userMessage('ok',undefined,{metadata:{},extensions:[]})),
            params:{message:userMessage('ok',undefined,{metadata:{},extensions:[]}),configuration:{acceptedOutputModes:['text/plain']}}},{'A2A-Version':'1.0'});
        assert.equal(accepted.body.result.message.role,'ROLE_AGENT');
    }finally{await h.close();}
});

test('directory_PublicAddress_AdvertisesWeaveA2aInterfaceThatAcceptsSendMessage',async()=>{
    const h=await start();
    try{
        const {a,b}=await pair(h);await connect(h,a,b);
        const lookup=await h.fetchAs(null,'http://localhost/v1/directory/address?addressId='+b.cardId);
        assert.equal(lookup.status,200);
        const entry=await lookup.json() as any;
        assert.deepEqual(entry.a2aInterface,{url:'http://localhost/a2a/'+b.cardId,protocolBinding:'JSONRPC',protocolVersion:'1.0'});
        assert.equal(entry.agentCard,null,'the interface sits beside the agent-declared card, not inside it');
        const search=await h.fetchAs(null,'http://localhost/v1/directory?q='+b.cardId);
        const items=(await search.json() as any).items;
        assert.equal(items.length,1);assert.deepEqual(items[0].a2aInterface,entry.a2aInterface);
        // The advertised URL is the live endpoint: an admitted contact's SendMessage there is queued.
        const response=await h.fetchAs(a,entry.a2aInterface.url,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(rpc(userMessage('via the directory')))});
        const body=await response.json() as any;
        assert.equal(body.result.message.parts[1].data.weave.status,'queued');
        assert.equal((await inboxFrom(h,b,a)).length,1);
    }finally{await h.close();}
});
