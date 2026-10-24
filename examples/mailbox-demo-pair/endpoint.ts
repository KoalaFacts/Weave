import {existsSync,readdirSync} from 'node:fs';
import {join} from 'node:path';
import {createHash} from 'node:crypto';
import {PairError,ORIGIN,identity,object,parseGeneration,save,load} from './runtime.ts';
import {pages,type Client} from './mcp.ts';
import type {History,Generation} from './model.ts';

export type Peer = {accountId:string;addressId:string};
export type Message = {sequence:number;messageId:string;replyTo:string|null;text:string};
export type Evidence = {role:'A'|'B';runId:string;accountId:string;peerAccountId:string;recoveryVerified:boolean;privateAddress:boolean;sent:Message[];received:Message[];models:{threadId:string;sequence:number;inputTokens:number;outputTokens:number}[];acknowledged:string[]};
export type Infer = (sequence:number,history:History)=>Promise<Generation>;
function decode(payload:any):Record<string,any>{
    if(payload?.payloadEncoding!=='text/plain'||typeof payload.bytes!=='string'||payload.bytes.length>16384)throw new PairError('invalid_payload');
    const bytes=Buffer.from(payload.bytes,'base64');if(bytes.toString('base64')!==payload.bytes)throw new PairError('invalid_payload');
    try{return object(JSON.parse(new TextDecoder('utf8',{fatal:true}).decode(bytes)));}catch{throw new PairError('invalid_payload');}
}
export class Endpoint {
    private client!:Client;
    private own!:Peer;
    private target!:Peer;
    private index=0;
    private introId?:string;
    private history:History=[];
    private sent:Message[]=[];
    private received:Message[]=[];
    private models:Evidence['models']=[];
    private recovered=false;
    readonly role:'A'|'B';readonly runId:string;private directory:string;private open:()=>Promise<Client>;private infer:Infer;
    constructor(role:'A'|'B',runId:string,directory:string,open:()=>Promise<Client>,infer:Infer){
        this.role=role;this.runId=identity(runId);this.directory=directory;this.open=open;this.infer=infer;
    }
    private snapshot():void{save(join(this.directory,'transcript.json'),{sent:this.sent,received:this.received,models:this.models});}
    private async write(name:string,args:Record<string,any>):Promise<any>{
        const {connectionToken,...nonsecret}=args;
        const checkpoint={operation:name,status:'pending',arguments:nonsecret,...(connectionToken?{connectionTokenDigest:createHash('sha256').update(connectionToken).digest('hex')}:{})};
        const path=join(this.directory,`intent-${++this.index}.json`);save(path,checkpoint);
        const result=await this.client.call(name,args);
        save(path,{...checkpoint,status:'response_received'});return result;
    }
    async setup():Promise<Peer>{
        const path=join(this.directory,'identity.json'),known=existsSync(path)?object(load(path)):null;
        this.client=await this.open();let accountId:string;
        if(known){if(known.origin!==ORIGIN)throw new PairError('identity_origin_changed');accountId=identity(known.accountId);await this.client.call('list_addresses',{limit:1});}
        else{accountId=identity((await this.write('register_account',{})).accountId);save(path,{origin:ORIGIN,accountId,recovery:'not_verified'});}
        // Drop the entire binding and resolve the original vault reference again.
        await this.client.close();this.client=await this.open();
        await this.client.call('list_addresses',{limit:1});
        const recovered=identity((await this.write('register_account',{})).accountId);
        if(recovered!==accountId){save(join(this.directory,'identity-change.json'),{origin:ORIGIN,oldAccountId:accountId,newAccountId:recovered});throw new PairError('account_changed');}
        this.recovered=true;save(path,{origin:ORIGIN,accountId,recovery:'verified'});
        const label=`Weave pair ${this.runId} ${this.role}`;
        const existing=(await pages(this.client,'list_addresses')).filter(a=>a.label===label&&a.accountId===accountId);
        if(existing.length>1)throw new PairError('ambiguous_test_address');
        const address=existing[0]??await this.write('create_address',{label,isPublic:false});
        if(address.accountId!==accountId||address.isPublic!==false||address.effectiveFrom!=null||address.effectiveTo!=null)throw new PairError('unexpected_test_address');
        this.own={accountId,addressId:identity(address.addressId)};save(path,{origin:ORIGIN,...this.own,recovery:'verified'});return this.own;
    }
    // Only the settled first turn of each agent is restorable. Later effects invalidate this barrier.
    private checkpoint(sequence:number):void{
        if(sequence!==(this.role==='A'?1:2))return;
        save(join(this.directory,'restart-checkpoint.json'),{
            version:1,origin:ORIGIN,role:this.role,runId:this.runId,completedSequence:sequence,
            own:this.own,target:this.target,introId:this.introId??null,index:this.index,
            sent:this.sent,received:this.received,models:this.models,history:this.history,recovered:this.recovered
        });
    }
    async restore():Promise<void>{
        if(this.own||this.target||this.client)throw new PairError('endpoint_already_initialized');
        const record=object(load(join(this.directory,'restart-checkpoint.json')));
        const saved=object(load(join(this.directory,'identity.json')));
        const expected=this.role==='A'?1:2;
        if(record.version!==1||record.origin!==ORIGIN||record.runId!==this.runId||record.role!==this.role||
            record.completedSequence!==expected||record.recovered!==true||saved.origin!==ORIGIN||saved.recovery!=='verified')
            throw new PairError('restart_checkpoint_mismatch');
        const own=object(record.own),target=object(record.target);
        const owner=identity(own.accountId),addressId=identity(own.addressId),peer=identity(target.accountId);
        identity(target.addressId);
        if(owner===peer||saved.accountId!==owner||saved.addressId!==addressId||
            (this.role==='A'?typeof record.introId!=='string':record.introId!==null))
            throw new PairError('restart_checkpoint_mismatch');
        if(this.role==='A')identity(record.introId);
        if(!Number.isSafeInteger(record.index)||record.index<1||
            readdirSync(this.directory).filter(name=>/^intent-[1-9][0-9]*\\.json$/.test(name)).length!==record.index)
            throw new PairError('restart_unresolved_write');
        for(let i=1;i<=record.index;i++){
            const intent=object(load(join(this.directory,`intent-${i}.json`)));
            if(intent.status!=='response_received')throw new PairError('restart_unresolved_write');
        }
        const transcript=object(load(join(this.directory,'transcript.json')));
        if(JSON.stringify({sent:record.sent,received:record.received,models:record.models})!==JSON.stringify(transcript))
            throw new PairError('restart_state_advanced');
        if(!Array.isArray(record.sent)||!Array.isArray(record.received)||!Array.isArray(record.models)||!Array.isArray(record.history)||
            record.sent.length!==1||record.received.length!==(expected-1)||record.models.length!==1||
            record.history.length!==expected)throw new PairError('restart_checkpoint_mismatch');
        const sent=object(record.sent[0]),model=object(record.models[0]),history=record.history.map(object);
        const received=record.received.map(object);
        if(sent.sequence!==expected||!identity(sent.messageId)||typeof sent.text!=='string'||!sent.text.trim()||
            Buffer.byteLength(sent.text)>2048||model.sequence!==expected||!identity(model.threadId)||
            !Number.isSafeInteger(model.outputTokens)||model.outputTokens<1||
            (expected===1?sent.replyTo!==null:
                received[0].sequence!==1||sent.replyTo!==identity(received[0].messageId))||
            (expected===1?false:received[0].replyTo!==null||typeof received[0].text!=='string')||
            history.some((h:any,i:number)=>h.sequence!==i+1||
                h.text!==(i===expected-1?sent.text:received[0].text)))
            throw new PairError('restart_checkpoint_mismatch');
        // A fresh trusted worker resolves its own vault control before these read-only checks.
        const client=await this.open();
        try{
            const addresses=(await pages(client,'list_addresses')).filter(a=>a.addressId===addressId);
            if(addresses.length!==1||addresses[0].accountId!==owner||addresses[0].isPublic!==false||
                addresses[0].effectiveFrom!=null||addresses[0].effectiveTo!=null)
                throw new PairError('restart_identity_mismatch');
            const contact=await client.call('get_contact',{accountId:peer});
            if(contact.canSend!==true)throw new PairError('restart_contact_missing');
            const receipts=(await pages(client,'read_outbox')).filter(r=>r.messageId===sent.messageId&&r.recipientAccountId===peer);
            if(receipts.length!==1||receipts[0].state!==(expected===1?'acknowledged':'pending'))
                throw new PairError('restart_receipt_mismatch');
        }catch(error){await client.close();throw error;}
        this.client=client;this.own={accountId:owner,addressId};this.target={accountId:peer,addressId:target.addressId};
        this.index=record.index;this.introId=this.role==='A'?record.introId:undefined;
        this.sent=record.sent;this.received=record.received;this.models=record.models;this.history=record.history;this.recovered=true;
    }
    // Reopen from the endpoint's own vault reference; never register while verifying a live binding.
    async reconnect():Promise<void>{
        if(!this.own||!this.target)throw new PairError('endpoint_not_ready');
        await this.client.close();
        this.client=await this.open();
        const matches=(await pages(this.client,'list_addresses')).filter(address=>address.addressId===this.own.addressId);
        if(matches.length!==1||matches[0].accountId!==this.own.accountId||matches[0].isPublic!==false)throw new PairError('reconnected_identity_mismatch');
    }
    peer(peer:Peer):void{
        if(!this.own||this.target)throw new PairError('peer_already_set_or_not_ready');
        if(identity(peer.accountId)===this.own.accountId)throw new PairError('accounts_not_distinct');
        this.target={accountId:identity(peer.accountId),addressId:identity(peer.addressId)};
    }
    async introduce():Promise<void>{
        if(this.role!=='A'||!this.target)throw new PairError('invalid_contact_role');
        const previous=(await pages(this.client,'list_contact_requests')).filter(r=>[r.requesterAccountId,r.recipientAccountId].includes(this.target.accountId));
        if(previous.length)throw new PairError('existing_contact_requires_review');
        const payload=await this.client.call('prepare_payload',{text:JSON.stringify({type:'weave-pair-contact',runId:this.runId,text:'Owner-authorized private two-agent test.'})});
        this.introId=identity(payload.messageId);
        await this.write('request_contact',{requesterAccountId:this.own.accountId,requesterAddressId:this.own.addressId,recipientAccountId:this.target.accountId,recipientAddressId:this.target.addressId,payload});
    }
    async accept():Promise<void>{
        if(this.role!=='B'||!this.target)throw new PairError('invalid_contact_role');
        const requests=(await pages(this.client,'list_contact_requests')).filter(r=>r.requesterAccountId===this.target.accountId&&r.recipientAccountId===this.own.accountId&&r.requesterAddressId===this.target.addressId&&r.recipientAddressId===this.own.addressId);
        if(requests.length!==1||requests[0].status!=='pending')throw new PairError('contact_not_pending');
        const request=requests[0],inbox=await pages(this.client,'read_inbox');
        const introductions=inbox.filter(m=>m.senderAccountId===this.target.accountId&&m.envelope?.recipientAccountId===this.own.accountId&&m.envelope?.payload?.messageId===request.requestMessageId);
        if(introductions.length!==1)throw new PairError('introduction_not_found');
        const intro=decode(introductions[0].envelope.payload);
        if(intro.type!=='weave-pair-contact'||intro.runId!==this.runId)throw new PairError('introduction_mismatch');
        save(join(this.directory,'introduction-review.json'),{requestId:identity(request.requestId),messageId:identity(request.requestMessageId),senderAccountId:this.target.accountId,runId:this.runId});
        const result=await this.write('decide_contact',{requestId:request.requestId,requesterAccountId:this.target.accountId,expectedGeneration:parseGeneration(request.generation),status:'accepted'});
        if(result.isConnected!==true)throw new PairError('contact_not_accepted');
        await this.ack(request.requestMessageId);
    }
    private async ack(messageId:string):Promise<void>{
        const response=await this.write('ack_message',{messageId:identity(messageId),senderAccountId:this.target.accountId});
        if(response.state!=='acknowledged')throw new PairError('ack_not_confirmed');
    }
    private async receive(sequence:number):Promise<Message>{
        const inbox=await pages(this.client,'read_inbox');const found:{item:any;body:any}[]=[];
        for(const item of inbox){
            if(item.senderAccountId!==this.target.accountId||item.envelope?.recipientAccountId!==this.own.accountId)continue;
            const body=decode(item.envelope.payload);
            if(body.type==='weave-pair-message'&&body.runId===this.runId&&body.sequence===sequence)found.push({item,body});
        }
        if(found.length!==1)throw new PairError('expected_message_not_found');
        const {item,body}=found[0],expectedReplyTo=this.sent.at(-1)?.messageId??null;
        if(body.replyTo!==expectedReplyTo||typeof body.text!=='string'||!body.text.trim()||Buffer.byteLength(body.text)>2048)throw new PairError('message_correlation_mismatch');
        const channel=await this.client.call('get_channel',{peerAccountId:this.target.accountId});
        if(parseGeneration(item.envelope.contactGeneration)!==parseGeneration(channel.generation))throw new PairError('message_generation_mismatch');
        const message={sequence,messageId:identity(item.envelope.payload.messageId),replyTo:body.replyTo,text:body.text};
        if(this.received.some(m=>m.messageId===message.messageId))throw new PairError('duplicate_processing');
        this.received.push(message);this.history.push({sequence,text:body.text});this.snapshot();return message;
    }
    async step(sequence:number):Promise<void>{
        if(!this.target||sequence!==this.sent.length*2+(this.role==='A'?1:2)||sequence>6)throw new PairError('unexpected_step');
        const incoming=sequence===1?null:await this.receive(sequence-1);
        const generated=await this.infer(sequence,structuredClone(this.history));
        identity(generated.threadId);
        if(!generated.text.trim()||Buffer.byteLength(generated.text)>2048)throw new PairError('invalid_generated_message');
        this.models.push({threadId:generated.threadId,sequence,inputTokens:generated.inputTokens,outputTokens:generated.outputTokens});
        save(join(this.directory,`generated-${sequence}.json`),{sequence,...generated});
        if(incoming)await this.ack(incoming.messageId);
        const channel=await this.client.call('get_channel',{peerAccountId:this.target.accountId});
        const contact=await this.client.call('get_contact',{accountId:this.target.accountId});
        if(contact.canSend!==true||typeof contact.connectionToken!=='string'||!/^[0-9a-f]{64}$/.test(contact.connectionToken))throw new PairError('channel_not_authorized');
        const body={type:'weave-pair-message',runId:this.runId,sequence,replyTo:incoming?.messageId??null,text:generated.text};
        const payload=await this.client.call('prepare_payload',{text:JSON.stringify(body)});
        const message={sequence,messageId:identity(payload.messageId),replyTo:body.replyTo,text:body.text};
        // Retain the exact nonsecret envelope before the write. Never auto-replay a timeout.
        this.sent.push(message);this.snapshot();
        const receipt=await this.write('send_message',{recipientAccountId:this.target.accountId,contactGeneration:parseGeneration(channel.generation),connectionToken:contact.connectionToken,payload});
        if(receipt.messageId!==payload.messageId||!['pending','acknowledged'].includes(receipt.state))throw new PairError('send_not_confirmed');
        this.history.push({sequence,text:generated.text});
        this.checkpoint(sequence);
    }
    async consumeFinal():Promise<void>{if(this.role!=='A'||this.sent.length!==3)throw new PairError('invalid_final_step');await this.ack((await this.receive(6)).messageId);}
    async evidence():Promise<Evidence>{
        if(!this.own||!this.target)throw new PairError('endpoint_not_ready');
        const receipts=await pages(this.client,'read_outbox'),expected=[...this.sent.map(m=>m.messageId),...(this.introId?[this.introId]:[])];
        const acknowledged:string[]=[];
        for(const id of expected){const matched=receipts.filter(r=>r.messageId===id&&r.recipientAccountId===this.target.accountId);if(matched.length!==1||matched[0].state!=='acknowledged')throw new PairError('receipt_not_acknowledged');acknowledged.push(id);}
        const result={role:this.role,runId:this.runId,accountId:this.own.accountId,peerAccountId:this.target.accountId,recoveryVerified:this.recovered,privateAddress:true,sent:this.sent,received:this.received,models:this.models,acknowledged};
        save(join(this.directory,'evidence.json'),result);return result;
    }
    async close():Promise<void>{await this.client?.close();}
}
export function verifyEvidence(a:Evidence,b:Evidence):{messages:(Message&{from:'A'|'B'})[]} {
    if(a.role!=='A'||b.role!=='B'||a.runId!==b.runId||a.accountId===b.accountId||a.peerAccountId!==b.accountId||b.peerAccountId!==a.accountId)throw new PairError('pair_identity_mismatch');
    identity(a.runId);identity(a.accountId);identity(b.accountId);
    for(const e of [a,b])if(!e.recoveryVerified||!e.privateAddress||e.sent.length!==3||e.received.length!==3||e.models.length!==3)throw new PairError('incomplete_pair_evidence');
    const messages=[...a.sent.map(m=>({...m,from:'A' as const})),...b.sent.map(m=>({...m,from:'B' as const}))].sort((a,b)=>a.sequence-b.sequence);
    const ids=new Set<string>(),threads=new Set<string>();
    for(let i=0;i<6;i++){
        const m=messages[i],sender=i%2?b:a,recipient=i%2?a:b;
        if(m.sequence!==i+1||m.from!==sender.role||m.replyTo!==(i===0?null:messages[i-1].messageId)||ids.has(m.messageId)||!sender.acknowledged.includes(m.messageId))throw new PairError('conversation_chain_mismatch');
        identity(m.messageId);ids.add(m.messageId);
        const received=recipient.received.filter(r=>r.messageId===m.messageId);
        if(received.length!==1||received[0].sequence!==m.sequence||received[0].text!==m.text||received[0].replyTo!==m.replyTo)throw new PairError('receiver_evidence_mismatch');
        const model=sender.models.filter(g=>g.sequence===m.sequence);
        if(model.length!==1||threads.has(model[0].threadId)||model[0].outputTokens<1)throw new PairError('model_evidence_mismatch');threads.add(identity(model[0].threadId));
    }
    return {messages};
}
