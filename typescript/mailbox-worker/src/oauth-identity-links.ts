/** Owner-approved RFC 7662 OAuth subject links. No provider claims or raw tokens are stored.
 * This is a separately gated future D1 schema: do not migrate a live database
 * merely because the source is present or the tests can initialize a new one.
 */
import type {Database} from './database';
import {atomic} from './database';
import type {VerifiedOAuthSubject} from './remote-oauth';
import {nowUtc} from './time';
import {fail,hash,uuid} from './wire';

export interface LinkedSubjectKey {issuer:string;resource:string;subjectDigest:string;}
const digestPattern=/^[0-9a-f]{64}$/;
function validateSubject(value:VerifiedOAuthSubject):void {
    if(typeof value.issuer!=='string'||typeof value.resource!=='string'||typeof value.subject!=='string'||
       value.issuer.length>512||value.resource.length>512||value.subject.length<1||value.subject.length>255||
       /[\x00-\x1f\x7f]/.test(value.subject))fail();
    let issuer:URL,resource:URL;
    try{issuer=new URL(value.issuer);resource=new URL(value.resource);}catch{fail();}
    if(issuer.protocol!=='https:'||issuer.username||issuer.password||issuer.search||issuer.hash||
       issuer.href.replace(/\/$/,'')!==value.issuer.replace(/\/$/,'')||
       resource.protocol!=='https:'||resource.username||resource.password||resource.search||resource.hash||
       !['/mcp','/access-trial/mcp'].includes(resource.pathname)||resource.href!==value.resource)fail();
}
function validateKey(key:LinkedSubjectKey):void {
    if(!key||typeof key!=='object'||typeof key.subjectDigest!=='string'||!digestPattern.test(key.subjectDigest))fail();
    validateSubject({issuer:key.issuer,resource:key.resource,subject:'validated-link-key'});
}
/** Hash the exact tuple with domain separation; never retain the external sub. */
export async function oauthSubjectDigest(value:VerifiedOAuthSubject):Promise<string> {
    validateSubject(value);
    return hash('weave-oauth-subject-link-v1:'+JSON.stringify([value.issuer,value.resource,value.subject]));
}
export async function resolveLinkedOAuthSubject(db:Database,subject:VerifiedOAuthSubject):Promise<string|null> {
    const digest=await oauthSubjectDigest(subject);
    return db.prepare('SELECT account_id FROM oauth_identity_links WHERE issuer=? AND resource=? AND subject_digest=? AND revoked_at IS NULL')
        .bind(subject.issuer,subject.resource,digest).first<string>('account_id');
}
/** This function only receives a subject already validated by the configured AS.
 * The HTTP boundary additionally requires the Weave owner's control secret.
 */
export async function linkOAuthSubject(db:Database,owner:string,subject:VerifiedOAuthSubject):Promise<LinkedSubjectKey> {
    uuid(owner);const digest=await oauthSubjectDigest(subject);
    const key={issuer:subject.issuer,resource:subject.resource,subjectDigest:digest};
    return atomic(db,async tx=>{
        const row=await tx.db.prepare('SELECT account_id,revoked_at FROM oauth_identity_links WHERE issuer=? AND resource=? AND subject_digest=?')
            .bind(subject.issuer,subject.resource,digest).first<{account_id:string;revoked_at:string|null}>();
        if(row&&row.account_id!==owner)fail(409,'conflict');
        if(row&&row.revoked_at===null)return key;
        const at=nowUtc().toString();
        if(row)tx.writes.push(tx.db.prepare(
            'UPDATE oauth_identity_links SET revoked_at=NULL,updated_at=? WHERE issuer=? AND resource=? AND subject_digest=? AND account_id=?')
            .bind(at,subject.issuer,subject.resource,digest,owner));
        else tx.writes.push(tx.db.prepare(
            'INSERT INTO oauth_identity_links(issuer,resource,subject_digest,account_id,created_at,updated_at,revoked_at) VALUES(?,?,?,?,?,?,NULL)')
            .bind(subject.issuer,subject.resource,digest,owner,at,at));
        return key;
    });
}
/** Cursor encodes an ordered key, not an authorization credential. Owner scope is
 * independently checked in every query, including caller-edited continuations. */
function encodeLinkCursor(key:LinkedSubjectKey):string {
    const bytes=new TextEncoder().encode(JSON.stringify(key));
    let binary='';for(const byte of bytes)binary+=String.fromCharCode(byte);
    return btoa(binary).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
}
function decodeLinkCursor(value:string):LinkedSubjectKey {
    if(!value||value.length>4096||!/^[A-Za-z0-9_-]+$/.test(value))fail();
    let parsed:unknown;
    try{
        const binary=atob(value.replace(/-/g,'+').replace(/_/g,'/'));
        const bytes=new Uint8Array(binary.length);
        for(let index=0;index<binary.length;index++)bytes[index]=binary.charCodeAt(index);
        parsed=JSON.parse(new TextDecoder('utf-8',{fatal:true}).decode(
            bytes));
    }catch{fail();}
    if(!parsed||typeof parsed!=='object'||Array.isArray(parsed)||
       Object.keys(parsed).sort().join(',')!=='issuer,resource,subjectDigest')fail();
    const key=parsed as LinkedSubjectKey;
    validateKey(key);
    if(encodeLinkCursor(key)!==value)fail();
    return key;
}
export async function listOAuthLinks(db:Database,owner:string,cursor:string|null=null):Promise<{
    items:Array<LinkedSubjectKey&{active:boolean}>;truncated:boolean;nextCursor:string|null
}> {
    uuid(owner);
    const after=cursor===null?null:decodeLinkCursor(cursor);
    const sql=after?
        'SELECT issuer,resource,subject_digest,revoked_at FROM oauth_identity_links WHERE account_id=? AND (issuer,resource,subject_digest)>(?,?,?) ORDER BY issuer,resource,subject_digest LIMIT 51':
        'SELECT issuer,resource,subject_digest,revoked_at FROM oauth_identity_links WHERE account_id=? ORDER BY issuer,resource,subject_digest LIMIT 51';
    const query=db.prepare(sql);
    const rows=(await (after?query.bind(owner,after.issuer,after.resource,after.subjectDigest):query.bind(owner))
        .all<{issuer:string;resource:string;subject_digest:string;revoked_at:string|null}>()).results;
    const visible=rows.slice(0,50);
    const truncated=rows.length>50;
    const last=visible.at(-1);
    return {items:visible.map(row=>({issuer:row.issuer,resource:row.resource,
        subjectDigest:row.subject_digest,active:row.revoked_at===null})),truncated,
        nextCursor:truncated&&last?encodeLinkCursor({issuer:last.issuer,resource:last.resource,
            subjectDigest:last.subject_digest}):null};
}
export async function revokeOAuthLink(db:Database,owner:string,key:LinkedSubjectKey):Promise<LinkedSubjectKey&{active:false}> {
    uuid(owner);validateKey(key);
    return atomic(db,async tx=>{
        const row=await tx.db.prepare('SELECT account_id,revoked_at FROM oauth_identity_links WHERE issuer=? AND resource=? AND subject_digest=?')
            .bind(key.issuer,key.resource,key.subjectDigest).first<{account_id:string;revoked_at:string|null}>();
        if(!row||row.account_id!==owner)fail(404,'unavailable');
        if(row.revoked_at===null){
            tx.writes.push(tx.db.prepare(
                'UPDATE oauth_identity_links SET revoked_at=?,updated_at=? WHERE issuer=? AND resource=? AND subject_digest=? AND account_id=? AND revoked_at IS NULL')
                .bind(nowUtc().toString(),nowUtc().toString(),key.issuer,key.resource,key.subjectDigest,owner));
        }
        return {...key,active:false};
    });
}
