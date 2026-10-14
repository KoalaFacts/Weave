import { atomic, Transaction, type Env } from './database';
import { publishSequence } from './cards';
import { enc, fail, limit, object, parse, query, uuid } from './wire';
import { ticks } from './protocol';

function profile(input: any): any {
    object(input,[],['label','isPublic','effectiveFrom','effectiveTo','directUri']);
    if (input.isPublic !== undefined && typeof input.isPublic !== 'boolean') fail();
    if (input.label !== undefined && input.label !== null && (typeof input.label !== 'string' || input.label.length > 128 || /[\u0000-\u001f\u007f-\u009f]/u.test(input.label))) fail();
    for (const key of ['effectiveFrom','effectiveTo']) if (input[key] !== undefined && input[key] !== null) {
        ticks(input[key]); if (!input[key].endsWith('Z')) fail();
    }
    if (input.effectiveFrom != null && input.effectiveTo != null && ticks(input.effectiveTo) <= ticks(input.effectiveFrom)) fail();
    if (input.directUri !== undefined && input.directUri !== null) {
        if (typeof input.directUri !== 'string' || enc.encode(input.directUri).length > 2048 || /[\u0000-\u0020\u007f-\u009f]/u.test(input.directUri)) fail();
        let uri: URL; try { uri = new URL(input.directUri); } catch { fail(); }
        // Stored metadata only: no fetch, redirect, forwarding or credential admission.
        if (!['http:','https:'].includes(uri.protocol) || uri.username || uri.password) fail();
    }
    return {label:input.label ?? null,isPublic:input.isPublic ?? false,effectiveFrom:input.effectiveFrom ?? null,effectiveTo:input.effectiveTo ?? null,directUri:input.directUri ?? null};
}
function baseUrl(origin: string): string {
    const url = new URL(origin);
    if (url.username || url.password || url.search || url.hash || url.pathname !== '/' || (url.protocol !== 'https:' && !(url.protocol === 'http:' && ['localhost','127.0.0.1','[::1]'].includes(url.hostname)))) fail(503,'storage');
    return url.origin;
}
export const addressView = (c: any) => ({addressId:c.cardId,accountId:c.ownerMailboxId,...c._address});
export async function mutateAddress(env: Env,owner: string,id: string|null,input: any,origin: string,remove = false): Promise<any> {
    if (id !== null) uuid(id);
    const chosen = remove ? null : profile(input);
    return atomic(env.db,async tx => {
        const previous = id === null ? null : await tx.get('card','',id);
        if (id !== null && (!previous || !previous._address || previous._deleted || previous.ownerMailboxId !== owner)) fail(404,'unavailable');
        if (remove) {
            tx.put('card','',id!,{...previous,_deleted:true,revokedAt:new Date().toISOString(),visibility:'unlisted',_address:{...previous._address,isPublic:false}});
            return {};
        }
        if (!previous) {
            const count = await tx.db.prepare("SELECT count(*) n FROM mailbox_records WHERE kind='card'").first<number>('n');
            if (count === null || count >= 1000) fail(429,'capacity');
        }
        const cardId = id ?? crypto.randomUUID();
        const c = {...(previous ?? {cardId,ownerMailboxId:owner,createdAt:new Date().toISOString(),expiresAt:null,revokedAt:null,audienceHint:null,
            methods:[{methodId:'relay',version:1,transport:'relay',endpoint:baseUrl(origin),instructions:'Contact request requires recipient acceptance'}]}),
            visibility:chosen.isPublic ? 'public' : 'unlisted',_address:chosen,_deleted:false,
            _effectiveFromTicks:chosen.effectiveFrom === null ? null : ticks(chosen.effectiveFrom).toString(),
            _effectiveToTicks:chosen.effectiveTo === null ? null : ticks(chosen.effectiveTo).toString(),
            _publicSequence:previous?._publicSequence ?? (chosen.isPublic ? await publishSequence(tx) : null)};
        tx.put('card','',cardId,c); return addressView(c);
    });
}
export async function ownedAddresses(tx: Transaction,owner: string,url: URL): Promise<any> {
    const match = url.pathname.match(/^\/v1\/addresses\/([^/]+)$/);
    if (match) {
        uuid(match[1]); const card = await tx.get('card','',match[1]);
        if (!card || !card._address || card._deleted || card.ownerMailboxId !== owner) fail(404,'unavailable'); return addressView(card);
    }
    const n=limit(url),after=query(url,'afterCursor');
    const prefix='addresses:'+btoa(owner)+':';
    if (after !== null && (!after.startsWith(prefix) || !/^[0-9a-f-]{36}$/.test(after.slice(prefix.length)))) fail();
    const rows=await tx.rows('card',"json_extract(data,'$.ownerMailboxId')=? AND json_extract(data,'$._address') IS NOT NULL AND json_extract(data,'$._deleted')=0 AND id>?",[owner,after?.slice(prefix.length) ?? ''],`ORDER BY id LIMIT ${n+1}`);
    return {items:rows.slice(0,n).map(r=>addressView(parse(r.data))),nextCursor:rows.length>n ? prefix+rows[n-1].id : null};
}
export async function directory(tx: Transaction,url: URL): Promise<any> {
    const now=(BigInt(Date.now())*10000n).toString();
    const filter=`kind='card' AND json_extract(data,'$._address') IS NOT NULL AND json_extract(data,'$._deleted')=0 AND json_extract(data,'$.visibility')='public' AND json_extract(data,'$.revokedAt') IS NULL
        AND (json_extract(data,'$._effectiveFromTicks') IS NULL OR CAST(json_extract(data,'$._effectiveFromTicks') AS INTEGER)<=CAST(? AS INTEGER))
        AND (json_extract(data,'$._effectiveToTicks') IS NULL OR CAST(json_extract(data,'$._effectiveToTicks') AS INTEGER)>CAST(? AS INTEGER))`;
    if (url.pathname.endsWith('/address')) {
        const id=query(url,'addressId',true); uuid(id);
        const row=await tx.db.prepare(`SELECT data FROM mailbox_records WHERE ${filter} AND id=?`).bind(now,now,id).first<{data:string}>();
        if (!row) fail(404,'unavailable'); return addressView(parse(row.data));
    }
    const n=limit(url),search=query(url,'q') ?? '',after=query(url,'afterCursor'); if(search.length>128) fail();
    // Version and query bind the opaque cursor to this ordering; old ID-only cursors must restart.
    const prefix='directory-created-v1:'+btoa(unescape(encodeURIComponent(search)))+':';
    let boundary: [string,string] | null = null;
    if(after !== null){
        if(!after.startsWith(prefix))fail();
        const value=after.slice(prefix.length),created=value.slice(0,24),id=value.slice(25);
        if(value[24]!=='/'||!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/.test(created)||!Number.isFinite(Date.parse(created))||new Date(created).toISOString()!==created)fail();
        uuid(id);boundary=[created,id];
    }
    const rows=(await tx.db.prepare(`SELECT id,data,json_extract(data,'$.createdAt') AS createdAt FROM mailbox_records WHERE ${filter}
        ${boundary ? "AND (json_extract(data,'$.createdAt'),id)<(?,?)" : ''}
        AND (instr(lower(COALESCE(json_extract(data,'$._address.label'),'')),lower(?))>0 OR instr(id,?)>0)
        ORDER BY json_extract(data,'$.createdAt') DESC,id DESC LIMIT ?`)
        .bind(now,now,...(boundary ?? []),search,search,n+1).all<{id:string;data:string;createdAt:string}>()).results;
    return {items:rows.slice(0,n).map(r=>addressView(parse(r.data))),nextCursor:rows.length>n ? prefix+rows[n-1].createdAt+'/'+rows[n-1].id : null};
}
