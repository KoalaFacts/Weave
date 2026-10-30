/** Optional machine-readable hint naming the rejected input field. Never echoes the input value. */
export interface ProblemField { field: string; reason: string; }
export class Problem extends Error {
    status: number; code: string; retryAfter?: number; detail?: ProblemField;
    constructor(status: number, code: string, retryAfter?: number, detail?: ProblemField) { super(code); this.status = status; this.code = code; this.retryAfter = retryAfter; this.detail = detail; }
}
export function invalidField(field: string, reason: string): never { throw new Problem(400, 'invalid', undefined, {field, reason}); }
export function fail(status = 400, code = 'invalid', retryAfter?: number): never { throw new Problem(status, code, retryAfter); }
export const enc = new TextEncoder();
export function stringify(value: unknown): string {
    if (typeof value === 'bigint') return value.toString();
    if (Array.isArray(value)) return '[' + value.map(stringify).join(',') + ']';
    if (value !== null && typeof value === 'object') return '{' + Object.entries(value).map(([k,v]) => JSON.stringify(k) + ':' + stringify(v)).join(',') + '}';
    return JSON.stringify(value);
}
export function parse(text: string): any {
    // ES2024 source-aware parsing retains exact v1 int64 values before Number rounding.
    return (JSON.parse as Function)(text, (key: string, value: unknown, context: { source?: string }) => {
        if (['generation', 'expectedGeneration', 'contactGeneration'].includes(key) && typeof value === 'number') {
            if (!context.source || !/^-?\d+$/.test(context.source)) fail();
            return BigInt(context.source);
        }
        return value;
    });
}
export function json(value: unknown, status = 200, legacy = false): Response {
    return new Response(stringify(accountNames(value,legacy)), { status, headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json', 'Cache-Control': 'no-store', 'Pragma': 'no-cache' } });
}
const identityNames:Record<string,string>={agentId:'accountId',mailboxId:'accountId',ownerMailboxId:'ownerAccountId',senderMailboxId:'senderAccountId',recipientMailboxId:'recipientAccountId',requesterMailboxId:'requesterAccountId',peerMailboxId:'peerAccountId'};
export function legacyResponse(request:Request):boolean {
    return request.headers.has('X-Weave-Mailbox-Control')&&!request.headers.has('X-Weave-Account-Control');
}
export function accountNames(value:any,legacy=false):any {
    if(Array.isArray(value))return value.map(child=>accountNames(child,legacy));
    if(value&&typeof value==='object'){
        const result:Record<string,any>={};
        const summary = ('requestVersion' in value) && ('requesterMailboxId' in value);
        const envelope = ('payload' in value) && ('recipientMailboxId' in value) && value.version === 2;
        for(const [key,child] of Object.entries(value)){
            if(legacy&&(summary&&['requestVersion','requesterAddressId','recipientAddressId'].includes(key)||envelope&&key==='connectionToken'))continue;
            result[key]=legacy&&envelope&&key==='version'?1:accountNames(child,legacy);
            const canonical=key==='isBlockedByMailbox'?'isBlockedByAccount':identityNames[key];
            if(canonical&&!legacy)result[canonical]=child;
        }
        return result;
    }
    return value;
}
export function problem(error: unknown): Response {
    const p = error instanceof Problem ? error : new Problem(503, 'storage');
    const titles: Record<string,string> = { invalid: 'Invalid request', unauthenticated: 'Account control required', unavailable: 'Resource unavailable', conflict: 'State conflict', expired: 'Expired', capacity: 'Capacity reached', tooLarge: 'Request too large', storage: 'Storage unavailable', maintenance:'Maintenance in progress' };
    const response = json({ status: p.status, title: titles[p.code], code: p.code, ...(p.detail ?? {}) }, p.status);
    if (p.retryAfter !== undefined) response.headers.set('Retry-After', String(p.retryAfter));
    return response;
}
export function object(value: any, required: string[], optional: string[] = []): void {
    if (!value || typeof value !== 'object' || Array.isArray(value) || required.some(k => !(k in value)) || Object.keys(value).some(k => !required.includes(k) && !optional.includes(k))) fail();
}
export function identity(value: unknown): asserts value is string {
    if (typeof value !== 'string' || !value.trim() || value.length > 128) fail();
}
export function generation(value: unknown): asserts value is bigint {
    if (typeof value !== 'bigint' || value < 1n || value > 9223372036854775807n) fail();
}
export function uuid(value: unknown): asserts value is string {
    if (typeof value !== 'string' || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(value)) fail();
}
export async function body(request: Request): Promise<any> {
    if (!/^application\/(?:json|[\w.+-]+\+json)(?:\s*;|$)/i.test(request.headers.get('Content-Type') ?? '')) fail();
    if (Number(request.headers.get('Content-Length')) > 131072) fail(413, 'tooLarge');
    const reader = request.body?.getReader(); if (!reader) fail();
    const chunks: Uint8Array[] = []; let size = 0;
    try { while (true) { const next = await reader.read(); if (next.done) break; size += next.value.length; if (size > 131072) fail(413, 'tooLarge'); chunks.push(next.value); } }
    finally { await reader.cancel(); }
    const all = new Uint8Array(size); let offset = 0; for (const chunk of chunks) { all.set(chunk, offset); offset += chunk.length; }
    let value; try { value = parse(new TextDecoder('utf-8', { fatal: true }).decode(all)); } catch (e) { if (e instanceof Problem) throw e; fail(); }
    const depth = (v: any, n: number) => { if (n > 16) fail(); if (v && typeof v === 'object') for (const child of Object.values(v)) depth(child, n + 1); };
    depth(value, 0);
    if(value&&typeof value==='object'&&!Array.isArray(value))for(const [legacy,canonical] of Object.entries(identityNames)){
        if ((legacy in value) && (canonical in value) && value[legacy] !== value[canonical]) fail();
        const from:string=value.requestVersion===2?legacy:canonical,to:string=value.requestVersion===2?canonical:legacy;
        if(from in value){value[to]=value[from];delete value[from];}
    }
    return value;
}
export async function hash(value: string): Promise<string> {
    return Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', enc.encode(value))), b => b.toString(16).padStart(2,'0')).join('');
}
export function control(request: Request): string | null {
    const canonical=request.headers.get('X-Weave-Account-Control'),legacy=request.headers.get('X-Weave-Mailbox-Control');
    if(canonical!==null&&legacy!==null&&canonical!==legacy)fail(401,'unauthenticated');
    const value = canonical??legacy;
    if (value !== null && (value.length < 64)) fail(401, 'unauthenticated');
    return value;
}
export function identityQuery(url:URL,canonical:string,legacy:string):string {
    const a=query(url,canonical),b=query(url,legacy);
    if(a!==null&&b!==null&&a!==b)fail();
    const value=a??b;if(value===null)fail();identity(value);return value;
}
export function query(url: URL, name: string, required = false): string | null {
    const all = url.searchParams.getAll(name); if (all.length > 1 || required && all.length !== 1) fail(); return all[0] ?? null;
}
export function limit(url: URL): number {
    const raw = query(url, 'limit'); if (raw !== null && !/^\d+$/.test(raw)) fail();
    const n = raw === null ? 10 : Number(raw); if (n < 1 || n > 10) fail(); return n;
}
