import { createHash } from 'node:crypto';

/**
 * Lets media code that runs in this Node process (private MCP tools called directly) store files: Workers' DigestStream
 * is not in Node, and the Miniflare R2 proxy cannot take a Node stream, so it receives the same file as one buffer.
 * Tests through the Worker itself use the real runtime and real R2 streaming.
 */
export function inProcessMedia(bucket: any) {
    const subtle = crypto as unknown as {DigestStream?: unknown};
    subtle.DigestStream ??= class extends WritableStream<Uint8Array> {
        digest: Promise<ArrayBuffer>;
        constructor(algorithm: string) {
            const hash = createHash(algorithm.replace('-', '').toLowerCase());
            let done!: (value: ArrayBuffer) => void;
            super({write: chunk => { hash.update(chunk); }, close: () => { const out = hash.digest(); done(out.buffer.slice(out.byteOffset, out.byteOffset + out.byteLength)); }});
            this.digest = new Promise(resolve => { done = resolve; });
        }
    };
    return {get: (key: string) => bucket.get(key), delete: (key: string) => bucket.delete(key),
        put: async (key: string, body: ReadableStream, options: unknown) => bucket.put(key, new Uint8Array(await new Response(body).arrayBuffer()), options)};
}
