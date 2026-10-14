import { Transaction } from './database';
import { enc, fail, generation, hash, identity, object, stringify, uuid } from './wire';

export const MAX_GENERATION = 9223372036854775807n;
export const nowText = () => new Date().toISOString();
export function ticks(value: unknown): bigint {
    if (typeof value !== 'string') fail();
    const match = /^(\d{4})-(\d\d)-(\d\d)T(\d\d):(\d\d):(\d\d)(?:\.(\d{1,7}))?(Z|[+-]\d\d:\d\d)$/.exec(value);
    if (!match) fail();
    const [year,month,day,hour,minute,second] = match.slice(1,7).map(Number);
    if (year < 1 || month < 1 || month > 12 || day < 1 || day > new Date(Date.UTC(year === 1 ? 2001 : year,month,0)).getUTCDate() || hour > 23 || minute > 59 || second > 59) fail();
    const zone = match[8]; if (zone !== 'Z' && (Number(zone.slice(1,3)) > 14 || Number(zone.slice(4)) > 59 || Number(zone.slice(1,3)) === 14 && Number(zone.slice(4)) !== 0)) fail();
    const milliseconds = Date.parse(value); if (!Number.isFinite(milliseconds)) fail();
    return BigInt(milliseconds) * 10000n + BigInt((match[7] ?? '').padEnd(7,'0').slice(3));
}
export function timestampMatches(id: string, created: string): void {
    uuid(id);
    if (id[14] !== '7' || !/[89ab]/.test(id[19]) || BigInt('0x'+id.replaceAll('-','').slice(0,12)) !== ticks(created) / 10000n) fail();
}
export function validatePayload(p: any, first = false): void {
    object(p,['messageId','createdAt','expiresAt','payloadEncoding','bytes']);
    timestampMatches(p.messageId,p.createdAt);
    const created = ticks(p.createdAt), expires = ticks(p.expiresAt), now = BigInt(Date.now()) * 10000n;
    if (expires <= created || expires-created > 864000000000n || created-now > 300000000n) fail();
    if (typeof p.payloadEncoding !== 'string' || !p.payloadEncoding.trim() || p.payloadEncoding.length > 128 || /[\u0000-\u001f\u007f-\u009f]/u.test(p.payloadEncoding)) fail();
    if (typeof p.bytes !== 'string') fail();
    const base64 = p.bytes.replace(/[\t\r\n ]/g,'');
    if (!/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(base64)) fail();
    let decoded; try { decoded = atob(base64); } catch { fail(); }
    if (decoded.length > 65536) fail(); p.bytes = btoa(decoded);
    if (first && (expires <= now || now-created > 3000000000n)) fail(410,'expired');
}
export async function fingerprint(value: any): Promise<string> {
    function canonical(v: any, key = ''): any {
        if (v !== null && ['createdAt','expiresAt','revokedAt'].includes(key)) return ticks(v).toString();
        if (Array.isArray(v)) return v.map(x => canonical(x));
        if (v && typeof v === 'object') return Object.fromEntries(Object.keys(v).sort().map(k => [k,canonical(v[k],k)]));
        return v;
    }
    return hash(stringify(canonical(value)));
}
export const pair = (a: string,b: string) => a < b ? [a,b] : [b,a];
export const channelKey = (a: string,b: string) => stringify(pair(a,b));
export const blocked = (c: any) => c.blockedLow || c.blockedHigh;
export const current = (c: any,r: any) => c.currentRequest?.requesterMailboxId === r.requesterMailboxId && c.currentRequest?.requestId === r.requestId && c.generation === r.generation;
export const effectiveRequest = (summary: any,now = BigInt(Date.now())*10000n) =>
    ['pending','needsAction'].includes(summary.status) && ticks(summary.expiresAt) <= now ? {...summary,status:'expired'} : summary;
export const relation = (c: any,r: any) => ({request:effectiveRequest(r.summary),generation:c.generation,isBlocked:blocked(c),isConnected:!blocked(c) && current(c,r.summary) && r.summary.status === 'accepted',updatedAt:ticks(c.updatedAt) > ticks(r.updatedAt) ? c.updatedAt : r.updatedAt});
export const channelView = (c: any,owner: string) => ({ mailboxId:owner,peerMailboxId:c.low === owner ? c.high : c.low,generation:c.generation,
    isBlockedByMailbox:c.low === owner ? c.blockedLow : c.blockedHigh,isBlockedByPeer:c.low === owner ? c.blockedHigh : c.blockedLow,currentRequest:c.currentRequest });
export const readChannel = (tx: Transaction,a: string,b: string) => tx.get('channel','',channelKey(a,b));
export function writeChannel(tx: Transaction,c: any): void { tx.put('channel','',channelKey(c.low,c.high),c); }
export function validateEnvelope(e: any,sender: string): void {
    object(e,['version','recipientMailboxId','contactGeneration','payload']); identity(e.recipientMailboxId); generation(e.contactGeneration);
    if (e.version !== 1 || e.recipientMailboxId === sender) fail(); validatePayload(e.payload);
}
export function utf8(value: unknown,max: number,nullable = false): void {
    if (nullable && value === null) return;
    if (typeof value !== 'string' || !value.trim() || enc.encode(value).length > max) fail();
}
