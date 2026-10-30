import { randomBytes } from 'node:crypto';
import { exactInt64, utcTicks } from '../../src/time.ts';
import { validParameters } from '../../src/credentials.ts';

const emptyTimes={created_at:null,updated_at:null};
const timestamp=value=>value===null||value===undefined?null:utcTicks(value);
const integer=value=>exactInt64(typeof value==='number'&&Number.isSafeInteger(value)?String(value):value);
const milliseconds=value=>exactInt64(integer(value)*10000n);
const bool=value=>value===true||value===1?1:value===false||value===0?0:(()=>{throw Error('Invalid boolean.');})();
const json=value=>JSON.stringify(value);
export function parseLegacy(text) {
    return JSON.parse(text,(key,value,context)=>['generation','expectedGeneration','contactGeneration'].includes(key)&&typeof value==='number'?exactInt64(context.source):value);
}
export function payloadBytes(payload) {
    if(payload===null)return null;
    if(typeof payload?.bytes!=='string')throw Error('Missing legacy payload bytes.');
    const text=payload.bytes.replace(/[\t\r\n ]/g,'');
    if(text.length>87384||!/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(text))throw Error('Invalid legacy base64.');
    const bytes=Buffer.from(text,'base64');
    if(bytes.length>65536)throw Error('Legacy payload exceeds 64 KiB.');
    return bytes;
}

// This is a one-time data conversion, not a second runtime representation.
export function convertLegacyRecords(rows,{now,highWater,contacts:existingContacts=[],recordCounter=null}) {
    const result=Object.fromEntries(['accounts','account_addresses','contact_cards','contact_requests','contacts','mailbox_records','message_delivery','legacy_channels','rate_limits','stream_leases','stream_cursors','counters','maintenance_state'].map(name=>[name,[]]));
    const parsed=rows.map(row=>{const seq=integer(row.seq);try{return {...row,seq,value:parseLegacy(row.data)};}catch{throw Error('Invalid legacy JSON at sequence '+seq+'.');}});
    const accounts=new Set(parsed.filter(r=>r.kind==='account').map(r=>r.id));
    const cards=new Map(parsed.filter(r=>r.kind==='card').map(r=>[r.id,r.value]));
    const account=id=>{if(!accounts.has(id))throw Error('Legacy reference has no account.');return id;};
    const fail=row=>{throw Error('Invalid legacy '+row.kind+' at sequence '+row.seq+'.');};
    for(const row of parsed) {
        const {kind,owner,id,seq,value:v}=row;
        try {
            switch(kind) {
            case 'account':
                if(owner!==''||v.accountId!==id||!/^[0-9a-f]{64}$/.test(v.controlHash)||v.controlHashScheme!=='sha256-utf8-v1'||!validParameters(v.controlHashParameters))fail(row);
                result.accounts.push({account_id:id,control_hash:v.controlHash,control_hash_scheme:v.controlHashScheme,control_hash_parameters:json(v.controlHashParameters),display_name:v.displayName??null,description:v.description??null,...emptyTimes});break;
            case 'card': {
                if(owner!==''||v.cardId!==id)fail(row);
                const common={seq,created_at:utcTicks(v.createdAt),expires_at:timestamp(v.expiresAt),revoked_at:timestamp(v.revokedAt),public_seq:v._publicSequence==null?null:integer(v._publicSequence),updated_at:null};
                if(v._address) {
                    const a=v._address,method=v.methods.find(m=>m.methodId==='relay'&&m.transport==='relay');
                    if(!method||v._deleted&&!v.revokedAt)fail(row);
                    result.account_addresses.push({address_id:id,account_id:account(v.ownerMailboxId),...common,label:a.label??null,is_public:bool(a.isPublic),effective_from:timestamp(a.effectiveFrom),effective_to:timestamp(a.effectiveTo),direct_uri:a.directUri??null,legacy_relay_endpoint:method.endpoint});
                } else result.contact_cards.push({card_id:id,owner_account_id:account(v.ownerMailboxId),...common,visibility:v.visibility,audience_hint:v.audienceHint??null,methods:json(v.methods)});
                break;
            }
            case 'request': {
                const s=v.summary,card=cards.get(s.cardId);
                if(s.requesterMailboxId!==owner||s.requestId!==id||!card||card.ownerMailboxId!==s.recipientMailboxId)fail(row);
                if(v.expiresTicks!==undefined&&integer(v.expiresTicks)!==utcTicks(s.expiresAt))fail(row);
                result.contact_requests.push({requester_account_id:account(owner),request_id:id,seq,recipient_account_id:account(s.recipientMailboxId),request_version:1,
                    requester_address_id:null,recipient_address_id:card._address?s.cardId:null,target_card_id:card._address?null:s.cardId,method_id:s.methodId,legacy_generation:integer(s.generation),
                    status:s.status,created_at:utcTicks(s.createdAt),expires_at:utcTicks(s.expiresAt),request_message_id:s.requestMessageId,reply_message_id:s.replyMessageId??null,
                    fingerprint:v.fingerprint,decision_fingerprint:v.decisionFingerprint??null,updated_at:utcTicks(v.updatedAt),terminal_at:timestamp(v.terminalAt)});break;
            }
            case 'channel':
                if(owner!==''||id!==JSON.stringify([v.low,v.high]))fail(row);
                result.legacy_channels.push({low_account_id:account(v.low),high_account_id:account(v.high),generation:integer(v.generation),blocked_low:bool(v.blockedLow),blocked_high:bool(v.blockedHigh),
                    current_requester_account_id:v.currentRequest?.requesterMailboxId??null,current_request_id:v.currentRequest?.requestId??null,updated_at:utcTicks(v.updatedAt),created_at:null});break;
            case 'message': {
                const r=v.receipt,bytes=payloadBytes(v.payload);
                if(r.senderMailboxId!==owner||r.messageId!==id||(bytes?.length??0)!==v.size||v.version!==1)fail(row);
                if(v.expiresTicks!==undefined&&integer(v.expiresTicks)!==utcTicks(r.expiresAt))fail(row);
                if(v.payload!==null&&(v.payload.messageId!==id||utcTicks(v.payload.expiresAt)!==utcTicks(r.expiresAt)))fail(row);
                result.mailbox_records.push({seq,message_id:id,sender_account_id:account(owner),recipient_account_id:account(r.recipientMailboxId),payload:bytes,payload_size:v.size,
                    payload_encoding:v.payload?.payloadEncoding??null,created_at:timestamp(v.payload?.createdAt),expires_at:utcTicks(r.expiresAt),updated_at:null});
                result.message_delivery.push({message_seq:seq,state:r.state,terminal_at:timestamp(r.terminalAt),protocol_version:v.version,purpose:v.purpose,legacy_generation:integer(v.generation),connection_token:null,
                    requester_account_id:v.request?.requesterMailboxId??null,request_id:v.request?.requestId??null,fingerprint:v.fingerprint,fingerprint_scheme:'legacy-envelope-v1',...emptyTimes});break;
            }
            case 'registration-rate':case 'public-rate':
                if(owner!=='')fail(row);
                result.rate_limits.push({seq,policy:kind==='registration-rate'?'registration':'public-read',bucket_key:id,window_start:kind==='registration-rate'?exactInt64(integer(v.hour)*36000000000n):milliseconds(v.at),count:v.count,...emptyTimes});break;
            case 'stream':
                result.stream_leases.push({account_id:account(owner),lease_id:id,expires_at:milliseconds(v.expires),...emptyTimes});break;
            case 'stream-progress':
                if(id!=='cursor')fail(row);
                result.stream_cursors.push({account_id:account(owner),cursor:v.cursor??null,version:v.version,...emptyTimes});break;
            case 'counter':
                if(owner!==''||id!=='public-cards')fail(row);
                result.counters.push({name:id,value:integer(v.value),...emptyTimes});break;
            case 'maintenance':
                if(owner!==''||id!=='sweep')fail(row);
                result.maintenance_state.push({name:id,last_run_at:milliseconds(v.at),...emptyTimes});break;
            default:fail(row);
            }
        } catch { fail(row); }
    }
    if(parsed.some(r=>r.seq>highWater))throw Error('Legacy sequence high-water is inconsistent.');
    result.counters.push(recordCounter??{name:'record-seq',value:highWater,created_at:now,updated_at:now});
    if(recordCounter&&recordCounter.value!==highWater)throw Error('Converted sequence high-water changed while paused.');
    for(const c of result.legacy_channels) {
        const r=result.contact_requests.find(r=>r.requester_account_id===c.current_requester_account_id&&r.request_id===c.current_request_id);
        if(c.current_request_id!==null&&(!r||r.requester_account_id===r.recipient_account_id||![c.low_account_id,c.high_account_id].includes(r.requester_account_id)||![c.low_account_id,c.high_account_id].includes(r.recipient_account_id)))throw Error('Legacy channel participants disagree.');
        if(c.blocked_low||c.blocked_high||!r||r.status!=='accepted'||r.legacy_generation!==c.generation)continue;
        const previous=existingContacts.filter(x=>[c.low_account_id,c.high_account_id].includes(x.owner_account_id)&&[c.low_account_id,c.high_account_id].includes(x.to_account_id));
        if(previous.length!==0&&(previous.length!==2||previous[0].connection_token!==previous[1].connection_token))throw Error('Incomplete contact conversion.');
        const token=previous[0]?.connection_token??randomBytes(32).toString('hex');
        for(const [owner,to] of [[c.low_account_id,c.high_account_id],[c.high_account_id,c.low_account_id]])result.contacts.push({owner_account_id:owner,to_account_id:to,connection_token:token,...emptyTimes});
    }
    return result;
}
