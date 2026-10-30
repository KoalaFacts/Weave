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
import { checkPayload, checkUuid } from './mcp-arguments';
import { MAX_GENERATION, validatePayload } from './protocol';
import { sweep } from './cleanup';

export { privateMcpTools } from './private-mcp-tools';
import { privateMcpTools } from './private-mcp-tools';
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
            parameters(args,['requesterAddressId','recipientAccountId','recipientAddressId','payload'],['requesterAddressId','recipientAccountId','recipientAddressId','payload']);
            for(const key of ['requesterAddressId','recipientAccountId','recipientAddressId'])checkUuid(args[key],key);
            checkPayload(args.payload,'payload');
            // The request ID is always the payload messageId; there is no separate parameter.
            const requestId=args.payload.messageId as string;
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
