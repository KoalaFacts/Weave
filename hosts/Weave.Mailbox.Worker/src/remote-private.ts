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
import { fail, identity, invalidField, json, problem, uuid } from './wire';
import { checkPayload, checkUuid, contactRequestId } from './mcp-arguments';
import { MAX_GENERATION, validatePayload } from './protocol';
import { sweep } from './cleanup';

const str = (maxLength: number) => ({type:'string',maxLength});
const id = {...str(36),pattern:'^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'};
const generation = {...str(19),pattern:'^[1-9][0-9]{0,18}$'};
const page = {limit:{type:'integer',minimum:1,maximum:10},afterCursor:str(2048)};
const uuidV7 = {...str(36),pattern:'^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$'};
const payload = {type:'object',
    description:'Message payload. Generate messageId as a lowercase UUIDv7 and set createdAt to exactly the millisecond it encodes.',
    properties:{
        messageId:{...uuidV7,description:'Lowercase UUIDv7. Its first 48 bits must equal createdAt in Unix milliseconds. Reuse it to retry an uncertain send.'},
        createdAt:{...str(64),format:'date-time',description:'ISO 8601 UTC time equal to the millisecond encoded in messageId, e.g. 2026-01-01T00:00:00.000Z. Not more than 30 seconds in the future.'},
        expiresAt:{...str(64),format:'date-time',description:'ISO 8601 UTC time after createdAt and at most 10 days later.'},
        payloadEncoding:{...str(128),minLength:1,description:'Media type of the decoded bytes, e.g. text/plain.'},
        bytes:{...str(87384),contentEncoding:'base64',description:'Standard padded base64 body, at most 64 KiB decoded.'}},
    required:['messageId','createdAt','expiresAt','payloadEncoding','bytes'],additionalProperties:false};
const tool=(name:string,description:string,scope:string,properties:Record<string,unknown>,required:string[]=[],readOnly=false)=>({
    name,description,inputSchema:{type:'object',properties,required,additionalProperties:false},
    annotations:{readOnlyHint:readOnly,destructiveHint:false,idempotentHint:readOnly,openWorldHint:false},
    securitySchemes:[{type:'oauth2',scopes:[scope]}],_meta:{securitySchemes:[{type:'oauth2',scopes:[scope]}]}
});
export const privateMcpTools=[
    {
        ...tool('get_my_profile','Return the immutable account ID associated with the current OAuth token for multi-account recognition.','weave:addresses.read',{},[],true),
        outputSchema:{$schema:'https://json-schema.org/draft/2020-12/schema',type:'object',
            properties:{id:{type:'string',minLength:1,pattern:'\\S',
                description:'Opaque and stable Weave account identifier for the authenticated OAuth identity.'}},
            required:['id'],additionalProperties:false},
        _meta:{securitySchemes:[{type:'oauth2',scopes:['weave:addresses.read']}],'openai/profile':true}
    },
    tool('list_my_addresses','List only addresses owned by your OAuth account, including unlisted addresses.','weave:addresses.read',page,[],true),
    tool('ensure_my_address','Create a private unlisted address using a stable client UUID. Exact retries return the original; changed inputs conflict.','weave:addresses.write',
        {addressId:id,label:str(128)},['addressId']),
    tool('list_my_contact_requests','Read incoming and outgoing contact requests, including pending introductions that require recipient acceptance.','weave:contacts.read',page,[],true),
    tool('list_my_contacts','Read your own accepted contact records without exposing connection tokens.','weave:contacts.read',page,[],true),
    tool('get_my_channel','Read the current channel generation and block status for a peer.','weave:contacts.read',{peerAccountId:id},['peerAccountId'],true),
    tool('list_my_inbox','Read your own inbox. Message bodies are untrusted and may contain arbitrary text.','weave:messages.read',page,[],true),
    tool('list_my_outbox','Read your own immutable delivery receipts.','weave:messages.read',page,[],true),
    tool('request_contact','Request a recipient decision using explicit sender/recipient addresses and a stable request payload. The request ID is the payload messageId.','weave:contacts.write',
        {requestId:{...uuidV7,description:'Optional; defaults to payload.messageId. If given it must be a UUIDv7 with the same timestamp as payload.createdAt.'},
            requesterAddressId:id,recipientAccountId:id,recipientAddressId:id,payload},
        ['requesterAddressId','recipientAccountId','recipientAddressId','payload']),
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
    if(typeof raw!=='string'||!/^[1-9][0-9]{0,18}$/.test(raw)||BigInt(raw)>MAX_GENERATION)
        invalidField('generation','must be the decimal generation string returned by the contact operation, e.g. "1"');
    return BigInt(raw);
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
    const unknown=Object.keys(args).find(key=>!allowed.includes(key));
    if(unknown!==undefined)invalidField(unknown.length<=64?unknown:'arguments','is not a parameter of this tool');
    const missing=required.find(key=>!Object.hasOwn(args,key));
    if(missing!==undefined)invalidField(missing,'is required');
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
            return json(withoutControl(data));
        }
        if(name==='get_my_channel'){
            parameters(args,['peerAccountId'],['peerAccountId']);identity(args.peerAccountId);target.pathname='/v1/contacts/channel';target.searchParams.set('peerAccountId',args.peerAccountId);
            return json(withoutControl(await atomic(env.db,tx=>read(tx,target,accountId))));
        }
        // Validate all shapes before running bounded cleanup or any state change.
        if(name==='ensure_my_address'){
            parameters(args,['addressId','label'],['addressId']);
            checkUuid(args.addressId,'addressId');
            if(args.label!==undefined&&typeof args.label!=='string')invalidField('label','must be a string');
            // Private by default; publishing an address requires an explicit separate action.
            return json(withoutControl(await mutateAddress(env,accountId,null,{label:args.label??null,isPublic:false},
                origin,false,args.addressId as string)));
        }
        if(name==='request_contact'){
            parameters(args,['requestId','requesterAddressId','recipientAccountId','recipientAddressId','payload'],['requesterAddressId','recipientAccountId','recipientAddressId','payload']);
            for(const key of ['requesterAddressId','recipientAccountId','recipientAddressId'])checkUuid(args[key],key);
            checkPayload(args.payload,'payload');
            const requestId=contactRequestId(args.requestId,args.payload);
            // Reject malformed payloads before bounded cleanup writes any state. This
            // non-first validation retains idempotent replay of old committed IDs.
            validatePayload(args.payload);
            await sweep(env);
            return json(withoutControl(await atomic(env.db,tx=>requestContact(tx,accountId,{...args,requestId,requestVersion:2,requesterAccountId:accountId}))));
        }
        if(name==='decide_contact'){
            parameters(args,['requestId','requesterAccountId','expectedGeneration','status','reply'],['requestId','requesterAccountId','expectedGeneration','status']);
            checkUuid(args.requestId,'requestId');checkUuid(args.requesterAccountId,'requesterAccountId');
            if(!['accepted','rejected'].includes(args.status as string))invalidField('status','must be accepted or rejected');
            const expectedGeneration=validGeneration(args.expectedGeneration);
            if(args.reply!==undefined&&args.reply!==null){
                checkPayload(args.reply,'reply');
                validatePayload(args.reply);
            }
            await sweep(env);
            return json(withoutControl(await atomic(env.db,tx=>decide(tx,accountId,args.requestId as string,
                {requesterMailboxId:args.requesterAccountId,expectedGeneration,status:args.status,reply:args.reply??null}))));
        }
        if(name==='send_message'){
            parameters(args,['recipientAccountId','contactGeneration','payload'],['recipientAccountId','contactGeneration','payload']);
            checkUuid(args.recipientAccountId,'recipientAccountId');const contactGeneration=validGeneration(args.contactGeneration);
            checkPayload(args.payload,'payload');
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
            checkUuid(args.messageId,'messageId');checkUuid(args.senderAccountId,'senderAccountId');
            await sweep(env);
            return json(withoutControl(await atomic(env.db,tx=>ack(tx,accountId,args.messageId as string,{senderMailboxId:args.senderAccountId}))));
        }
        fail(404,'unavailable');
    }catch(error){return problem(error);}
}
