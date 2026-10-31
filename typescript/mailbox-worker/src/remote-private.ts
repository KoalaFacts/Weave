/** Private MCP operations reuse the existing account-scoped Worker business paths.
 * The authenticated account is derived exclusively from OAuth introspection,
 * never from tool parameters. Channel tokens stay server-side.
 */
import { atomic, type Env, type Transaction } from './database';
import { ownContacts } from './contact-links';
import { ownedAddresses, mutateAddress } from './addresses';
import { read } from './reads';
import { requestContact, decide } from './contacts';
import { sendWithResolvedConnection, ack, terminalize } from './messages';
import { fail, identity, invalidField, json, problem, uuid } from './wire';
import { checkUuid } from './mcp-arguments';
import { MAX_GENERATION, readChannel, relation } from './protocol';
import { sweep } from './cleanup';
import { checkMessage, inboxMessage, messagePayload } from './a2a-message.ts';
import { idempotencyClaim, recordResultId, replayedResultId, type IdempotencyClaim } from './mcp-idempotency.ts';

export { privateMcpTools } from './private-mcp-tools';
import { privateMcpTools } from './private-mcp-tools';
export const privateScope=(name:string)=>privateMcpTools.find(x=>x.name===name)?.securitySchemes[0].scopes[0]??null;
const record=(value:unknown):value is Record<string,unknown>=>!!value && typeof value==='object'&&!Array.isArray(value);
const validGeneration=(raw:unknown):bigint=>{
    if(typeof raw!=='string'||!/^[1-9][0-9]{0,18}$/.test(raw)||BigInt(raw)>MAX_GENERATION)
        invalidField('generation','must be the decimal generation string returned by the contact operation, e.g. "1"');
    return BigInt(raw);
};
/** All tool result shapes are sanitized recursively; mailbox channel tokens
 * must not escape through a model-visible tool response.
 */
export function withoutSecrets(value:unknown):unknown {
    if(Array.isArray(value))return value.map(withoutSecrets);
    if(!record(value))return value;
    return Object.fromEntries(Object.entries(value).filter(([key])=>key!=='connectionToken')
        .map(([key,child])=>[key,withoutSecrets(child)]));
}
function parameters(args:Record<string,unknown>,allowed:string[],required:string[]=[]):void {
    const unknown=Object.keys(args).find(key=>!allowed.includes(key));
    if(unknown!==undefined)invalidField(unknown.length<=64?unknown:'arguments','is not a parameter of this tool');
    const missing=required.find(key=>!Object.hasOwn(args,key));
    if(missing!==undefined)invalidField(missing,'is required');
}
function pagination(url:URL,args:Record<string,unknown>):void {
    parameters(args,['limit','afterCursor']);
    if(args.limit!==undefined){
        if(!Number.isSafeInteger(args.limit)||(args.limit as number)<1||(args.limit as number)>10)invalidField('limit','must be an integer from 1 to 10');
        url.searchParams.set('limit',String(args.limit));
    }
    if(args.afterCursor!==undefined){
        if(typeof args.afterCursor!=='string'||args.afterCursor.length>2048)invalidField('afterCursor','must be the nextCursor string from the previous page');
        url.searchParams.set('afterCursor',args.afterCursor);
    }
}
function urlFor(origin:string,path:string):URL{return new URL(path,origin);}
/** A keyed write replays its stored result ID; otherwise it writes and records the key in the same transaction. */
async function keyed<T>(tx:Transaction,accountId:string,claim:IdempotencyClaim|null,replay:(resultId:string)=>Promise<T>,
    write:()=>Promise<{result:T;resultId:string}>):Promise<T>{
    const replayed=claim&&await replayedResultId(tx,accountId,claim);
    if(replayed)return replay(replayed);
    const {result,resultId}=await write();
    if(claim)recordResultId(tx,accountId,claim,resultId);
    return result;
}
/** The current generation of an accepted, unblocked channel; anything else is not a connection. */
async function connectedGeneration(tx:Transaction,owner:string,peer:string):Promise<bigint>{
    const channel=await readChannel(tx,owner,peer);
    const request=channel?.currentRequest&&await tx.get('request',channel.currentRequest.requesterMailboxId,channel.currentRequest.requestId);
    if(!channel||!request||!relation(channel,request).isConnected)fail(409,'not-connected');
    return channel.generation;
}
/** Inbox bodies that parse as an A2A Message are shown parsed, without the raw bytes. They remain untrusted peer content. */
function withParsedMessages(page:{items:any[]}):unknown{
    return {...page,items:page.items.map(item=>{
        const message=inboxMessage(item.envelope?.payload);
        if(!message)return item;
        const {bytes:_bytes,...payload}=item.envelope.payload;
        return {...item,envelope:{...item.envelope,payload},message};
    })};
}
export async function executePrivateMcp(env:Env,origin:string,accountId:string,name:string,args:Record<string,unknown>):Promise<Response> {
    try {
        if(!privateScope(name))fail(404,'unavailable');
        const target=urlFor(origin,'/');
        if(name==='get_my_profile'){
            parameters(args,[]);
            uuid(accountId);
            return json({id:accountId});
        }
        if(['list_my_addresses','list_my_contact_requests','list_my_contacts','list_my_inbox','list_my_outbox'].includes(name)){
            target.pathname=name==='list_my_addresses'?'/v1/addresses':name==='list_my_contact_requests'?'/v1/contacts/requests':
                name==='list_my_contacts'?'/v1/contacts':name==='list_my_inbox'?'/v1/inbox':'/v1/outbox';
            pagination(target,args);
            const data=await atomic(env.db,tx=>name==='list_my_addresses'?ownedAddresses(tx,accountId,target):
                name==='list_my_contacts'?ownContacts(tx,accountId,target):read(tx,target,accountId));
            return json(withoutSecrets(name==='list_my_inbox'?withParsedMessages(data):data));
        }
        if(name==='get_my_channel'){
            parameters(args,['peerAccountId'],['peerAccountId']);identity(args.peerAccountId);target.pathname='/v1/contacts/channel';target.searchParams.set('peerAccountId',args.peerAccountId);
            return json(withoutSecrets(await atomic(env.db,tx=>read(tx,target,accountId))));
        }
        // Validate all shapes before running bounded cleanup or any state change.
        if(name==='ensure_my_address'){
            parameters(args,['addressId','label'],['addressId']);
            checkUuid(args.addressId,'addressId');
            if(args.label!==undefined&&typeof args.label!=='string')invalidField('label','must be a string');
            // Private by default; publishing an address requires an explicit separate action.
            return json(withoutSecrets(await mutateAddress(env,accountId,null,{label:args.label??null,isPublic:false},
                origin,false,args.addressId as string)));
        }
        if(name==='request_contact'){
            parameters(args,['requesterAddressId','recipientAccountId','recipientAddressId','message','idempotencyKey'],['requesterAddressId','recipientAccountId','recipientAddressId','message']);
            for(const key of ['requesterAddressId','recipientAccountId','recipientAddressId'])checkUuid(args[key],key);
            const message=checkMessage(args.message,'message');
            const {requesterAddressId,recipientAccountId,recipientAddressId}=args;
            const claim=await idempotencyClaim(name,args.idempotencyKey,{requesterAddressId,recipientAccountId,recipientAddressId,message});
            await sweep(env);
            return json(withoutSecrets(await atomic(env.db,tx=>keyed(tx,accountId,claim,async requestId=>{
                const request=await tx.get('request',accountId,requestId);
                if(!request)fail(404,'unavailable');
                return relation(await readChannel(tx,accountId,request.summary.recipientMailboxId),request);
            },async()=>{
                // The request ID is always the server-built introduction's messageId.
                const payload=messagePayload(message,tx.store.now);
                const result=await requestContact(tx,accountId,{requestVersion:2,requestId:payload.messageId,requesterAccountId:accountId,
                    requesterAddressId,recipientAccountId,recipientAddressId,payload});
                return {result,resultId:payload.messageId};
            }))));
        }
        if(name==='decide_contact'){
            parameters(args,['requestId','requesterAccountId','expectedGeneration','status','reply','idempotencyKey'],['requestId','requesterAccountId','expectedGeneration','status']);
            checkUuid(args.requestId,'requestId');checkUuid(args.requesterAccountId,'requesterAccountId');
            if(!['accepted','rejected'].includes(args.status as string))invalidField('status','must be accepted or rejected');
            const expectedGeneration=validGeneration(args.expectedGeneration);
            const reply=args.reply===undefined||args.reply===null?null:checkMessage(args.reply,'reply');
            const {requestId,requesterAccountId,status}=args;
            const claim=await idempotencyClaim(name,args.idempotencyKey,{requestId,requesterAccountId,expectedGeneration:args.expectedGeneration,status,reply});
            await sweep(env);
            return json(withoutSecrets(await atomic(env.db,tx=>keyed(tx,accountId,claim,async()=>{
                const request=await tx.get('request',requesterAccountId,requestId);
                if(!request||request.summary.recipientMailboxId!==accountId)fail(404,'unavailable');
                return relation(await readChannel(tx,accountId,requesterAccountId),request);
            },async()=>{
                const result=await decide(tx,accountId,requestId,{requesterMailboxId:requesterAccountId,expectedGeneration,status,
                    reply:reply&&messagePayload(reply,tx.store.now)});
                return {result,resultId:requestId};
            }))));
        }
        if(name==='send_message'){
            parameters(args,['recipientAccountId','contactGeneration','message','idempotencyKey'],['recipientAccountId','message']);
            checkUuid(args.recipientAccountId,'recipientAccountId');
            const contactGeneration=args.contactGeneration===undefined?null:validGeneration(args.contactGeneration);
            const message=checkMessage(args.message,'message');
            const recipientAccountId=args.recipientAccountId;
            const claim=await idempotencyClaim(name,args.idempotencyKey,{recipientAccountId,contactGeneration:args.contactGeneration??null,message});
            await sweep(env);
            // Channel tokens are resolved in the guarded transaction and never exposed to the model.
            return json(withoutSecrets(await atomic(env.db,tx=>keyed(tx,accountId,claim,async messageId=>{
                const stored=await tx.get('message',accountId,messageId);
                if(!stored)fail(404,'unavailable');
                return terminalize(tx,stored);
            },async()=>{
                const generation=contactGeneration??await connectedGeneration(tx,accountId,recipientAccountId);
                const payload=messagePayload(message,tx.store.now);
                return {result:await sendWithResolvedConnection(tx,accountId,recipientAccountId,generation,payload),resultId:payload.messageId};
            }))));
        }
        if(name==='ack_message'){
            parameters(args,['messageId','senderAccountId'],['messageId','senderAccountId']);
            checkUuid(args.messageId,'messageId');checkUuid(args.senderAccountId,'senderAccountId');
            await sweep(env);
            return json(withoutSecrets(await atomic(env.db,tx=>ack(tx,accountId,args.messageId as string,{senderMailboxId:args.senderAccountId}))));
        }
        fail(404,'unavailable');
    }catch(error){return problem(error);}
}
