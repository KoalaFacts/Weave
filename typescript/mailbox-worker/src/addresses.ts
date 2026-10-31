import { atomic, Transaction, type Env } from './database';
import { publishSequence } from './cards';
import { enc, fail, limit, object, parse, query, uuid } from './wire';
import { ticks, nowText } from './protocol';
import { accountAddresses } from './schema';
import { addressSnapshot } from './wire-snapshots';
import { isoUtc } from './time';
import { agentLimits, agentTier } from './agent-limits.ts';
import { agentCards, removeAgentCard, withAgentCards } from './agent-cards.ts';

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
export async function mutateAddress(env: Env,owner: string,id: string|null,input: any,origin: string,remove = false,creationId: string|null = null): Promise<any> {
    if (id !== null) uuid(id);
    if(creationId!==null){
        uuid(creationId);
        if(id!==null||remove)fail();
    }
    const chosen = remove ? null : profile(input);
    return atomic(env.db,async tx => {
        const selectedId=id??creationId;
        const previous = selectedId === null ? null : await tx.get('card','',selectedId);
        if (id !== null && (!previous || !previous._address || previous._deleted || previous.ownerMailboxId !== owner)) fail(404,'unavailable');
        if (creationId !== null && previous && (!previous._address || previous._deleted || previous.ownerMailboxId !== owner)) fail(404,'unavailable');
        if (remove) {
            tx.put('card','',id!,{...previous,_deleted:true,revokedAt:nowText(tx),visibility:'unlisted',_address:{...previous._address,isPublic:false}});
            removeAgentCard(tx,id!);
            return {};
        }
        const unchanged=previous&&Object.entries(chosen).every(([key,value])=>['effectiveFrom','effectiveTo'].includes(key)?(value===null?previous._address[key]===null:previous._address[key]!==null&&ticks(value)===ticks(previous._address[key])):value===previous._address[key]);
        // A retained client-supplied creation ID can only return the original result,
        // never silently edit another already-created (or revoked) address.
        if(creationId!==null&&previous&&!unchanged)fail(409,'conflict');
        if(unchanged)return addressView(previous);
        if (!previous) {
            const count = await tx.db.prepare('SELECT (SELECT count(*) FROM contact_cards)+(SELECT count(*) FROM account_addresses) n').first<number>('n');
            if (count === null || count >= 1000) fail(429,'capacity');
        }
        // Per-agent limits apply to a new live address and to an address becoming public; counted in this transaction.
        if (!previous || chosen.isPublic && !previous._address.isPublic) {
            const limits = agentLimits[await agentTier(tx,owner)];
            const live = await tx.db.prepare('SELECT count(*) n,coalesce(sum(is_public),0) public FROM account_addresses WHERE account_id=? AND revoked_at IS NULL')
                .bind(owner).first<{n:number;public:number}>();
            if (!live) fail(503,'storage');
            if (!previous && live.n >= limits.liveAddresses || chosen.isPublic && live.public >= limits.publicAddresses) fail(429,'capacity');
        }
        const cardId = selectedId ?? crypto.randomUUID();
        if(selectedId===null&&await tx.get('card','',cardId))fail(409,'conflict');
        const c = {...(previous ?? {cardId,ownerMailboxId:owner,createdAt:nowText(tx),expiresAt:null,revokedAt:null,audienceHint:null,
            methods:[{methodId:'relay',version:1,transport:'relay',endpoint:baseUrl(origin),instructions:'Contact request requires recipient acceptance'}]}),
            visibility:chosen.isPublic ? 'public' : 'unlisted',_address:chosen,_deleted:false,
            _effectiveFromTicks:chosen.effectiveFrom === null ? null : ticks(chosen.effectiveFrom).toString(),
            _effectiveToTicks:chosen.effectiveTo === null ? null : ticks(chosen.effectiveTo).toString(),
            _publicSequence:previous?._publicSequence ?? (chosen.isPublic ? await publishSequence(tx) : null)};
        tx.put('card','',cardId,c);
        // A card exists only while its address is public; republishing never brings an old card back.
        if(previous?._address.isPublic&&!chosen.isPublic)removeAgentCard(tx,cardId);
        return addressView(c);
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
const publicFilter=`is_public=1 AND revoked_at IS NULL
        AND (effective_from IS NULL OR effective_from<=CAST(? AS INTEGER))
        AND (effective_to IS NULL OR effective_to>CAST(? AS INTEGER))
        AND (expires_at IS NULL OR expires_at>CAST(? AS INTEGER))`;
/** The stored Agent Card of a live, currently effective public address, filtered at read as well as deleted on leaving that state. */
export async function directoryAgentCard(tx: Transaction,addressId: string): Promise<unknown> {
    uuid(addressId);
    const now=(BigInt(Date.now())*10000n).toString();
    const rows=await tx.store.rows(accountAddresses,publicFilter+' AND address_id=?',[now,now,now,addressId],'LIMIT 1');
    const card=rows.length?(await agentCards(tx,[addressId])).get(addressId):undefined;
    if (card===undefined) fail(404,'unavailable');
    return card;
}
export async function directory(tx: Transaction,url: URL): Promise<any> {
    const now=(BigInt(Date.now())*10000n).toString();
    const filter=publicFilter;
    if (url.pathname.endsWith('/address')) {
        const id=query(url,'addressId',true); uuid(id);
        const rows=await tx.store.rows(accountAddresses,filter+' AND address_id=?',[now,now,now,id],'LIMIT 1');
        if (!rows.length) fail(404,'unavailable'); return (await withAgentCards(tx,[addressView(addressSnapshot(rows[0]))]))[0];
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
    return {items:await withAgentCards(tx,rows.slice(0,n).map(r=>addressView(addressSnapshot(r)))),nextCursor:rows.length>n ? prefix+isoUtc(rows[n-1].created_at)+'/'+rows[n-1].address_id : null};
}
