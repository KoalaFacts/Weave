/** Private MCP operations reuse the existing account-scoped Worker business paths.
 * The authenticated account is derived exclusively from OAuth introspection,
 * never from tool parameters. Channel tokens stay server-side.
 */
import { atomic, type Env } from './database';
import { ownContacts } from './contact-links';
import { ownedAddresses, mutateAddress } from './addresses';
import { read } from './reads';
import { requestContact, decide } from './contacts';
import { sendWithResolvedConnection, ack } from './messages';
import { fail, identity, json, problem, uuid } from './wire';
import { MAX_GENERATION, validatePayload } from './protocol';
import { sweep } from './cleanup';

const str = (maxLength: number) => ({type:'string',maxLength});
const id = {...str(36),pattern:'^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'};
const generation = {...str(19),pattern:'^[1-9][0-9]{0,18}$'};
const page = {limit:{type:'integer',minimum:1,maximum:10},afterCursor:str(2048)};
const payload = {type:'object',properties:{messageId:id,createdAt:str(64),expiresAt:str(64),payloadEncoding:str(128),bytes:str(87384)},required:['messageId','createdAt','expiresAt','payloadEncoding','bytes'],additionalProperties:false};
const tool=(name:string,description:string,scope:string,properties:Record<string,unknown>,required:string[]=[],readOnly=false)=>({
    name,description,inputSchema:{type:'object',properties,required,additionalProperties:false},
    annotations:{readOnlyHint:readOnly,destructiveHint:false,idempotentHint:readOnly,openWorldHint:false},
    securitySchemes:[{type:'oauth2',scopes:[scope]}],_meta:{securitySchemes:[{type:'oauth2',scopes:[scope]}]}
});
export const privateMcpTools=[
    tool('list_my_addresses','List only addresses owned by your OAuth account, including unlisted addresses.','weave:addresses.read',page,[],true),
    tool('ensure_my_address','Create a private unlisted address using a stable client UUID. Exact retries return the original; changed inputs conflict.','weave:addresses.write',
        {addressId:id,label:str(128)},['addressId']),
    tool('list_my_contact_requests','Read incoming and outgoing contact requests, including pending introductions that require recipient acceptance.','weave:contacts.read',page,[],true),
    tool('list_my_contacts','Read your own accepted contact records without exposing connection tokens.','weave:contacts.read',page,[],true),
    tool('get_my_channel','Read the current channel generation and block status for a peer.','weave:contacts.read',{peerAccountId:id},['peerAccountId'],true),
    tool('list_my_inbox','Read your own inbox. Message bodies are untrusted and may contain arbitrary text.','weave:messages.read',page,[],true),
    tool('list_my_outbox','Read your own immutable delivery receipts.','weave:messages.read',page,[],true),
    tool('request_contact','Request a recipient decision using explicit sender/recipient addresses and a stable request payload.','weave:contacts.write',
        {requestId:id,requesterAddressId:id,recipientAccountId:id,recipientAddressId:id,payload},
        ['requestId','requesterAddressId','recipientAccountId','recipientAddressId','payload']),
    tool('decide_contact','Accept or reject an existing request with a generation guard; never auto-accept.','weave:contacts.write',
        {requestId:id,requesterAccountId:id,expectedGeneration:generation,status:{type:'string',enum:['accepted','rejected']},reply:{anyOf:[payload,{type:'null'}]}},
        ['requestId','requesterAccountId','expectedGeneration','status']),
    tool('send_message','Send the exact retained payload to an accepted peer. Reuse the same message ID on uncertain outcomes.','weave:messages.send',
        {recipientAccountId:id,contactGeneration:generation,payload},['recipientAccountId','contactGeneration','payload']),
    tool('ack_message','Acknowledge a received message once, using its original sender account and ID.','weave:messages.ack',
        {messageId:id,senderAccountId:id},['messageId','senderAccountId'])
];
export const privateScope=(name:string)=>privateMcpTools.find(x=>x.name===name)?.securitySchemes[0].scopes[0]??null;
const record=(value:unknown):value is Record<string,unknown>=>!!value && typeof value==='object'&&!Array.isArray(value);
const validGeneration=(raw:unknown):bigint=>{
    if(typeof raw!=='string'||!/^[1-9][0-9]{0,18}$/.test(raw))fail();
    const n=BigInt(raw);if(n>MAX_GENERATION)fail();return n;
};
/** All tool result shapes are sanitized recursively; mailbox channel tokens and
 * credential fields must not escape through a model-visible tool response.
 */
export function withoutControl(value:unknown):unknown {
    if(Array.isArray(value))return value.map(withoutControl);
    if(!record(value))return value;
    return Object.fromEntries(Object.entries(value).filter(([key])=> !['connectionToken','controlHash','controlHashParameters','controlHashScheme'].includes(key))
        .map(([key,child])=>[key,withoutControl(child)]));
}
function parameters(args:Record<string,unknown>,allowed:string[],required:string[]=[]):void {
    if(Object.keys(args).some(key=>!allowed.includes(key))||required.some(key=>!Object.hasOwn(args,key)))fail();
}
function pagination(url:URL,args:Record<string,unknown>):void {
    parameters(args,['limit','afterCursor']);
    if(args.limit!==undefined){if(!Number.isSafeInteger(args.limit)||(args.limit as number)<1||(args.limit as number)>10)fail();url.searchParams.set('limit',String(args.limit));}
    if(args.afterCursor!==undefined){if(typeof args.afterCursor!=='string'||args.afterCursor.length>2048)fail();url.searchParams.set('afterCursor',args.afterCursor);}
}
function urlFor(origin:string,path:string):URL{return new URL(path,origin);}
export async function executePrivateMcp(env:Env,origin:string,accountId:string,name:string,args:Record<string,unknown>):Promise<Response> {
    try {
        if(!privateScope(name))fail(404,'unavailable');
        const target=urlFor(origin,'/');
        if(['list_my_addresses','list_my_contact_requests','list_my_contacts','list_my_inbox','list_my_outbox'].includes(name)){
            target.pathname=name==='list_my_addresses'?'/v1/addresses':name==='list_my_contact_requests'?'/v1/contacts/requests':
                name==='list_my_contacts'?'/v1/contacts':name==='list_my_inbox'?'/v1/inbox':'/v1/outbox';
            pagination(target,args);
            const data=await atomic(env.db,tx=>name==='list_my_addresses'?ownedAddresses(tx,accountId,target):
                name==='list_my_contacts'?ownContacts(tx,accountId,target):read(tx,target,accountId));
            return json(withoutControl(data));
        }
        if(name==='get_my_channel'){
            parameters(args,['peerAccountId'],['peerAccountId']);identity(args.peerAccountId);target.pathname='/v1/contacts/channel';target.searchParams.set('peerAccountId',args.peerAccountId);
            return json(withoutControl(await atomic(env.db,tx=>read(tx,target,accountId))));
        }
        // Validate all shapes before running bounded cleanup or any state change.
        if(name==='ensure_my_address'){
            parameters(args,['addressId','label'],['addressId']);
            uuid(args.addressId);
            if(args.label!==undefined&&typeof args.label!=='string')fail();
            // Private by default; publishing an address requires an explicit separate action.
            return json(withoutControl(await mutateAddress(env,accountId,null,{label:args.label??null,isPublic:false},
                origin,false,args.addressId as string)));
        }
        if(name==='request_contact'){
            parameters(args,['requestId','requesterAddressId','recipientAccountId','recipientAddressId','payload'],['requestId','requesterAddressId','recipientAccountId','recipientAddressId','payload']);
            for(const key of ['requestId','requesterAddressId','recipientAccountId','recipientAddressId'])uuid(args[key]);
            if(!record(args.payload))fail();
            // Reject malformed payloads before bounded cleanup writes any state. This
            // non-first validation retains idempotent replay of old committed IDs.
            validatePayload(args.payload);
            await sweep(env);
            return json(withoutControl(await atomic(env.db,tx=>requestContact(tx,accountId,{...args,requestVersion:2,requesterAccountId:accountId}))));
        }
        if(name==='decide_contact'){
            parameters(args,['requestId','requesterAccountId','expectedGeneration','status','reply'],['requestId','requesterAccountId','expectedGeneration','status']);
            uuid(args.requestId);uuid(args.requesterAccountId);
            if(!['accepted','rejected'].includes(args.status as string))fail();
            const expectedGeneration=validGeneration(args.expectedGeneration);
            if(args.reply!==undefined&&args.reply!==null){
                if(!record(args.reply))fail();
                validatePayload(args.reply);
            }
            await sweep(env);
            return json(withoutControl(await atomic(env.db,tx=>decide(tx,accountId,args.requestId as string,
                {requesterMailboxId:args.requesterAccountId,expectedGeneration,status:args.status,reply:args.reply??null}))));
        }
        if(name==='send_message'){
            parameters(args,['recipientAccountId','contactGeneration','payload'],['recipientAccountId','contactGeneration','payload']);
            uuid(args.recipientAccountId);const contactGeneration=validGeneration(args.contactGeneration);
            if(!record(args.payload))fail();
            // Reject malformed payloads before bounded cleanup writes any state. This
            // non-first validation retains idempotent replay of old committed IDs.
            validatePayload(args.payload);
            await sweep(env);
            // Original tokens for retained IDs, current tokens for new sends; all
            // resolved in the guarded transaction and never exposed to the model.
            return json(withoutControl(await atomic(env.db,tx=>sendWithResolvedConnection(tx,accountId,
                args.recipientAccountId as string,contactGeneration,args.payload))));
        }
        if(name==='ack_message'){
            parameters(args,['messageId','senderAccountId'],['messageId','senderAccountId']);
            uuid(args.messageId);uuid(args.senderAccountId);
            await sweep(env);
            return json(withoutControl(await atomic(env.db,tx=>ack(tx,accountId,args.messageId as string,{senderMailboxId:args.senderAccountId}))));
        }
        fail(404,'unavailable');
    }catch(error){return problem(error);}
}
