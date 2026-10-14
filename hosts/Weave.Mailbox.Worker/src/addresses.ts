import { atomic, Transaction, type Env } from './database';
import { publishSequence } from './cards';
import { enc, fail, limit, object, parse, query, uuid } from './wire';
import { ticks, nowText } from './protocol';
import { accountAddresses } from './schema';
import { addressSnapshot } from './wire-snapshots';
import { isoUtc } from './time';

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
            tx.put('card','',id!,{...previous,_deleted:true,revokedAt:nowText(tx),visibility:'unlisted',_address:{...previous._address,isPublic:false}});
            return {};
        }
        if(previous&&Object.entries(chosen).every(([key,value])=>['effectiveFrom','effectiveTo'].includes(key)?(value===null?previous._address[key]===null:previous._address[key]!==null&&ticks(value)===ticks(previous._address[key])):value===previous._address[key]))return addressView(previous);
        if (!previous) {
            const count = await tx.db.prepare('SELECT (SELECT count(*) FROM contact_cards)+(SELECT count(*) FROM account_addresses) n').first<number>('n');
            if (count === null || count >= 1000) fail(429,'capacity');
        }
        const cardId = id ?? crypto.randomUUID();
        if(id===null&&await tx.get('card','',cardId))fail(409,'conflict');
        const c = {...(previous ?? {cardId,ownerMailboxId:owner,createdAt:nowText(tx),expiresAt:null,revokedAt:null,audienceHint:null,
            methods:[{methodId:'relay',version:1,transport:'relay',endpoint:baseUrl(origin),instructions:'Contact request requires recipient acceptance'}]}),
            visibility:chosen.isPublic ? 'public' : 'unlisted',_address:chosen,_deleted:false,
            _effectiveFromTicks:chosen.effectiveFrom === null ? null : ticks(chosen.effectiveFrom).toString(),
            _effectiveToTicks:chosen.effectiveTo === null ? null : ticks(chosen.effectiveTo).toString(),
            _publicSequence:previous?._publicSequence ?? (chosen.isPublic ? await publishSequence(tx) : null)};
        tx.put('card','',cardId,c); return addressView(c);
    });
}
export async function ownedAddresses(tx: Transaction,owner: string,url: URL): Promise<any> {
    const historical=query(url,'includeRevoked');if(historical!==null&&!['true','false'].includes(historical))fail();
    const includeRevoked=historical==='true',view=(c:any)=>includeRevoked?{...addressView(c),createdAt:c.createdAt,expiresAt:c.expiresAt,revokedAt:c.revokedAt}:addressView(c);
    const match = url.pathname.match(/^\/v1\/addresses\/([^/]+)$/);
    if (match) {
        uuid(match[1]); const card = await tx.get('card','',match[1]);
        if (!card || !card._address || !includeRevoked&&card._deleted || card.ownerMailboxId !== owner) fail(404,'unavailable'); return view(card);
    }
    const n=limit(url),after=query(url,'afterCursor');
    const prefix='addresses:'+btoa(owner)+':';
    if (after !== null && (!after.startsWith(prefix) || !/^[0-9a-f-]{36}$/.test(after.slice(prefix.length)))) fail();
    const rows=await tx.store.rows(accountAddresses,`account_id=? ${includeRevoked?'':'AND revoked_at IS NULL'} AND address_id>?`,[owner,after?.slice(prefix.length) ?? ''],`ORDER BY address_id LIMIT ${n+1}`);
    return {items:rows.slice(0,n).map(r=>view(addressSnapshot(r))),nextCursor:rows.length>n ? prefix+rows[n-1].address_id : null};
}
export async function directory(tx: Transaction,url: URL): Promise<any> {
    const now=(BigInt(Date.now())*10000n).toString();
    const filter=`is_public=1 AND revoked_at IS NULL
        AND (effective_from IS NULL OR effective_from<=CAST(? AS INTEGER))
        AND (effective_to IS NULL OR effective_to>CAST(? AS INTEGER))
        AND (expires_at IS NULL OR expires_at>CAST(? AS INTEGER))`;
    if (url.pathname.endsWith('/address')) {
        const id=query(url,'addressId',true); uuid(id);
        const rows=await tx.store.rows(accountAddresses,filter+' AND address_id=?',[now,now,now,id],'LIMIT 1');
        if (!rows.length) fail(404,'unavailable'); return addressView(addressSnapshot(rows[0]));
    }
    const n=limit(url),search=query(url,'q') ?? '',after=query(url,'afterCursor'); if(search.length>128) fail();
    // Version and query bind the opaque cursor to this ordering; old ID-only cursors must restart.
    const prefix='directory-created-v1:'+btoa(unescape(encodeURIComponent(search)))+':';
    let boundary: [string,string] | null = null;
    if(after !== null){
        if(!after.startsWith(prefix))fail();
        const value=after.slice(prefix.length),separator=value.lastIndexOf('/'),created=value.slice(0,separator),id=value.slice(separator+1);
        if(separator<0||!created.endsWith('Z'))fail();
        uuid(id);boundary=[ticks(created).toString(),id];
    }
    const rows=await tx.store.rows(accountAddresses,`${filter}
        ${boundary ? 'AND (created_at,address_id)<(CAST(? AS INTEGER),?)' : ''}
        AND (instr(lower(COALESCE(label,'')),lower(?))>0 OR instr(address_id,?)>0)`,
        [now,now,now,...(boundary??[]),search,search,n+1],'ORDER BY account_addresses.created_at DESC,address_id DESC LIMIT ?');
    return {items:rows.slice(0,n).map(r=>addressView(addressSnapshot(r))),nextCursor:rows.length>n ? prefix+isoUtc(rows[n-1].created_at)+'/'+rows[n-1].address_id : null};
}
