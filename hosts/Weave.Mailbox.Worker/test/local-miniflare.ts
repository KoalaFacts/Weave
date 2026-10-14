import { createRequire } from 'node:module';
import { isMainThread, MessagePort, workerData } from 'node:worker_threads';
import type { Pool } from 'undici';

const require = createRequire(import.meta.url);
const miniflareEntry = require.resolve('miniflare');
const testProcess = isMainThread && process.env.NODE_TEST_CONTEXT !== undefined;
const synchronousProxy = !isMainThread && workerData?.filename === miniflareEntry
    && workerData.port instanceof MessagePort && workerData.notifyHandle instanceof Int32Array;

// Test-only interception for the pinned Miniflare transport. Its main thread
// and synchronous proxy worker each construct an explicit Undici Pool, with
// no per-instance transport option. Use --require: Node 24's evaluated CommonJS
// workers inherit --import flags without executing those ESM preloads.
if (testProcess || synchronousProxy) {
    const undici = createRequire(miniflareEntry)('undici') as typeof import('undici');
    const OriginalPool = undici.Pool;
    undici.Pool = class extends OriginalPool {
        constructor(origin: string | URL, options?: Pool.Options) {
            const url = new URL(origin);
            const loopback = url.protocol === 'http:'
                && ['127.0.0.1', 'localhost', '[::1]'].includes(url.hostname);
            // Disable keep-alive, not timeouts: a blocked event loop cannot
            // reuse a socket that workerd has already closed. Never retry.
            super(origin, loopback ? { ...options, pipelining: 0 } : options);
        }
    };
}
