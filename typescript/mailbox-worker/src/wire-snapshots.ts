import { exactInt64, isoUtc } from './time.ts';
const time=(value:string|null)=>value===null?null:isoUtc(value).replace(/(\.\d{3})0000Z$/,'$1Z');
const base64=(bytes:number[])=>{let value='';for(let i=0;i<bytes.length;i+=8192)value+=String.fromCharCode(...bytes.slice(i,i+8192));return btoa(value);};
export const addressSnapshot=(a:any)=>({cardId:a.address_id,ownerMailboxId:a.account_id,visibility:a.is_public?'public':'unlisted',createdAt:time(a.created_at),expiresAt:time(a.expires_at),revokedAt:time(a.revoked_at),audienceHint:null,
                methods:[{methodId:'relay',version:1,transport:'relay',endpoint:a.legacy_relay_endpoint,instructions:'Contact request requires recipient acceptance'}],
                _address:{label:a.label,isPublic:!!a.is_public,effectiveFrom:time(a.effective_from),effectiveTo:time(a.effective_to),directUri:a.direct_uri},_deleted:a.revoked_at!==null,
                _publicSequence:a.public_seq,_expiresTicks:a.expires_at,_effectiveFromTicks:a.effective_from,_effectiveToTicks:a.effective_to});

export const cardSnapshot=(c:any)=>({cardId:c.card_id,ownerMailboxId:c.owner_account_id,visibility:c.visibility,createdAt:time(c.created_at),expiresAt:time(c.expires_at),revokedAt:time(c.revoked_at),audienceHint:c.audience_hint,methods:JSON.parse(c.methods),_publicSequence:c.public_seq,_expiresTicks:c.expires_at});

export const requestSnapshot=(r:any)=>({summary:{requestId:r.request_id,cardId:r.recipient_address_id??r.target_card_id,requesterMailboxId:r.requester_account_id,recipientMailboxId:r.recipient_account_id,methodId:r.method_id,generation:exactInt64(r.legacy_generation),status:r.status,
                createdAt:time(r.created_at),expiresAt:time(r.expires_at),requestMessageId:r.request_message_id,replyMessageId:r.reply_message_id,
                requestVersion:r.request_version,requesterAddressId:r.requester_address_id,recipientAddressId:r.recipient_address_id},
                fingerprint:r.fingerprint,decisionFingerprint:r.decision_fingerprint,updatedAt:time(r.updated_at),terminalAt:time(r.terminal_at),expiresTicks:r.expires_at,
                requestVersion:r.request_version,requesterAddressId:r.requester_address_id,recipientAddressId:r.recipient_address_id,targetCardId:r.target_card_id});

export const messageSnapshot=(r:any,d:any=r)=>({receipt:{messageId:r.message_id,senderMailboxId:r.sender_account_id,recipientMailboxId:r.recipient_account_id,state:d.state,expiresAt:time(r.expires_at),terminalAt:time(d.terminal_at)},
                payload:r.payload===null?null:{messageId:r.message_id,createdAt:time(r.created_at),expiresAt:time(r.expires_at),payloadEncoding:r.payload_encoding,bytes:base64(r.payload)},size:r.payload_size,
                generation:d.legacy_generation===null?null:exactInt64(d.legacy_generation),version:d.protocol_version,expiresTicks:r.expires_at,fingerprint:d.fingerprint,fingerprintScheme:d.fingerprint_scheme,purpose:d.purpose,
                connectionToken:d.connection_token,request:d.request_id===null?null:{requesterMailboxId:d.requester_account_id,requestId:d.request_id}});
