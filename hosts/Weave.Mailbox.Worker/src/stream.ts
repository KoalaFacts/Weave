import { atomic, type Env } from './database';
import { read } from './reads';
import { enc, fail, stringify } from './wire';

export interface Context { waitUntil(promise: Promise<unknown>): void; }
export async function stream(env: Env,owner: string,request: Request,context: Context): Promise<Response> {
    const id = crypto.randomUUID();
    const {expires,progress} = await atomic(env.db,async tx => {
        const count = await tx.db.prepare(`SELECT count(*) total,sum(CASE WHEN owner=? THEN 1 ELSE 0 END) owned FROM mailbox_records
            WHERE kind='stream' AND json_extract(data,'$.expires')>?`).bind(owner,Date.now()).first<{total:number;owned:number}>();
        if (!count || count.total >= 100 || count.owned >= 2) fail(429,'capacity');
        tx.writes.push(tx.db.prepare("DELETE FROM mailbox_records WHERE kind='stream' AND json_extract(data,'$.expires')<=?").bind(Date.now()));
        const leaseExpiry=Date.now()+30000;
        tx.put('stream',owner,id,{expires:leaseExpiry});
        return {expires:leaseExpiry,progress:await tx.get('stream-progress',owner,'cursor')};
    });
    const {readable,writable} = new TransformStream<Uint8Array,Uint8Array>(undefined,{highWaterMark:1},{highWaterMark:0});
    const writer = writable.getWriter();
    let stopped = false, progressed = false;
    let cursor: string|null = progress?.cursor ?? null;
    let endLifetime: (reason: Error) => void = () => {};
    const lifetime = new Promise<never>((_,reject) => { endLifetime=reject; });
    void lifetime.catch(() => {});
    const abort = () => { stopped = true; endLifetime(new Error('stream ended')); void writer.abort().catch(() => {}); };
    const deadline = setTimeout(abort,Math.max(0,expires-10000-Date.now()));
    request.signal.addEventListener('abort',abort,{once:true});
    async function write(frame: string): Promise<boolean> {
        let timer: ReturnType<typeof setTimeout> | undefined;
        try {
            await Promise.race([writer.write(enc.encode(frame)), lifetime, new Promise<never>((_,reject) => { timer=setTimeout(() => reject(new Error('stream write timeout')),10000); })]);
            return true;
        } catch { abort(); return false; }
        finally { clearTimeout(timer); }
    }
    context.waitUntil((async () => {
        try {
            if (!await write(': pending\n\n')) return;
            // Each event gets a fresh owned snapshot. Finite connections continue a
            // server-owned traversal; wrapping replays pending messages without ACK.
            for (let cycle=0;cycle<8 && !stopped;cycle++) {
                const url = new URL('https://local/v1/inbox?limit=1'); if (cursor) url.searchParams.set('afterCursor',cursor);
                const page = await atomic(env.db,tx => read(tx,url,owner));
                if (stopped) break;
                if (page.items.length) {
                    const message = page.items[0];
                    if (!await write(`id: ${message.envelope.payload.messageId}\nevent: message\ndata: ${stringify(message)}\n\n`)) break;
                }
                if (stopped) break;
                cursor = page.nextCursor; progressed = true;
                if (!cursor) await new Promise(resolve => setTimeout(resolve,1000));
            }
            if (!stopped) await writer.close();
        } catch { abort(); }
        finally {
            clearTimeout(deadline);
            request.signal.removeEventListener('abort',abort);
            // A terminated isolate can miss release; its durable lease still expires.
            try { await atomic(env.db,async tx => {
                tx.delete('stream',owner,id);
                const latest = await tx.get('stream-progress',owner,'cursor');
                if (progressed && (latest?.version ?? null) === (progress?.version ?? null))
                    tx.put('stream-progress',owner,'cursor',{cursor,version:crypto.randomUUID()});
            }); } catch { /* lease expires; a missed progress update safely replays */ }
        }
    })());
    return new Response(readable,{headers:{'Content-Type':'text/event-stream','Cache-Control':'no-store','X-Accel-Buffering':'no'}});
}
