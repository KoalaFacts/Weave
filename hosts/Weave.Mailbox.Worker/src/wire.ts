export class Problem extends Error {
    constructor(public status: number, public code: string, public retryAfter?: number) { super(code); }
}
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
export function json(value: unknown, status = 200): Response {
    return new Response(stringify(value), { status, headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json', 'Cache-Control': 'no-store', 'Pragma': 'no-cache' } });
}
export function problem(error: unknown): Response {
    const p = error instanceof Problem ? error : new Problem(503, 'storage');
    const titles: Record<string,string> = { invalid: 'Invalid request', unauthenticated: 'Mailbox control required', unavailable: 'Resource unavailable', conflict: 'State conflict', expired: 'Expired', capacity: 'Capacity reached', tooLarge: 'Request too large', storage: 'Storage unavailable' };
    const response = json({ status: p.status, title: titles[p.code], code: p.code }, p.status);
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
    depth(value, 0); return value;
}
export async function hash(value: string): Promise<string> {
    return Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', enc.encode(value))), b => b.toString(16).padStart(2,'0')).join('');
}
export function control(request: Request): string | null {
    const value = request.headers.get('X-Weave-Mailbox-Control');
    if (value !== null && (value.length < 64)) fail(401, 'unauthenticated');
    return value;
}
export function query(url: URL, name: string, required = false): string | null {
    const all = url.searchParams.getAll(name); if (all.length > 1 || required && all.length !== 1) fail(); return all[0] ?? null;
}
export function limit(url: URL): number {
    const raw = query(url, 'limit'); if (raw !== null && !/^\d+$/.test(raw)) fail();
    const n = raw === null ? 10 : Number(raw); if (n < 1 || n > 10) fail(); return n;
}
