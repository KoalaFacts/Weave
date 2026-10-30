import assert from 'node:assert/strict';
import { test } from 'node:test';
import { once } from 'node:events';
import { createServer } from 'node:http';
import { connect } from 'node:net';
import { createRequire } from 'node:module';
import { MessageChannel, Worker } from 'node:worker_threads';
import { channel } from 'node:diagnostics_channel';
import { start } from './harness.ts';

test('local D1 writes use fresh connections after a stall without replay or hidden constraint failures', async () => {
    const h = await start({ migrate: false });
    const requests = channel('undici:client:sendHeaders');
    const writes = [];
    const observe = ({ request, headers, socket }) => {
        if (request.method === 'POST') writes.push({ port: socket.localPort, close: /\r\nconnection: close\r\n/i.test(headers) });
    };
    try {
        await h.db.prepare('CREATE TABLE transport_probe(id INTEGER PRIMARY KEY, value TEXT NOT NULL)').run();
        const insert = h.db.prepare('INSERT INTO transport_probe(value) VALUES (?)').bind('once');
        requests.subscribe(observe);
        await insert.run();
        // workerd runs independently and expires idle connections after 5s.
        Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 6000);
        await insert.run();
        requests.unsubscribe(observe);
        assert.equal(writes.length, 2, 'Each write must be dispatched once without retry.');
        assert.ok(writes.every(write => write.close), 'Local transport must not retain idle connections.');
        assert.notEqual(writes[0].port, writes[1].port);
        assert.equal(await h.db.prepare('SELECT count(*) n FROM transport_probe').first('n'), 2);
        await assert.rejects(h.db.prepare("INSERT INTO transport_probe(id,value) VALUES (1,'duplicate')").run(), /UNIQUE constraint failed/);
        assert.equal(await h.db.prepare('SELECT count(*) n FROM transport_probe').first('n'), 2);
    } finally {
        requests.unsubscribe(observe);
        await h.close();
    }
});

test('test bootstrap covers sync proxy workers but preserves other Pools and workers', async () => {
    const server = createServer((_request, response) => response.end('real HTTP response'));
    let connections = 0;
    server.on('connection', () => connections++);
    server.listen(0, '127.0.0.1');
    await once(server, 'listening');
    const port = server.address().port;
    const require = createRequire(import.meta.url);
    const miniflareEntry = require.resolve('miniflare');
    const undiciPath = createRequire(miniflareEntry).resolve('undici');
    const { Pool } = require(undiciPath);
    const pool = new Pool('http://transport-scope.invalid', {
        connections: 1,
        connect: (_options, callback) => {
            const socket = connect(port, '127.0.0.1');
            socket.once('connect', () => callback(null, socket));
            socket.once('error', callback);
        }
    });
    try {
        for (let i = 0; i < 2; i++) assert.equal(await (await pool.request({ path: '/', method: 'GET' })).body.text(), 'real HTTP response');
        assert.equal(connections, 1, 'Non-loopback origins must retain their normal Pool options.');
        for (const syncProxy of [false, true]) {
            const { port1, port2 } = new MessageChannel();
            const before = connections;
            const worker = new Worker(`
                const { createRequire } = require('node:module');
                const { parentPort, workerData } = require('node:worker_threads');
                const { Pool } = createRequire(workerData.undiciPath)(workerData.undiciPath);
                const pool = new Pool(workerData.origin, { connections: 1 });
                (async () => {
                    try {
                        for (let i = 0; i < 2; i++) await (await pool.request({ path: '/', method: 'GET' })).body.text();
                        parentPort.postMessage('done');
                    } finally { await pool.close(); workerData.port.close(); }
                })();
            `, { eval: true, workerData: { undiciPath, origin: `http://127.0.0.1:${port}`, port: port2,
                filename: syncProxy ? miniflareEntry : 'unrelated-worker',
                notifyHandle: new Int32Array(new SharedArrayBuffer(4)) }, transferList: [port2] });
            try {
                assert.deepEqual(await once(worker, 'message'), ['done']);
                assert.equal(connections - before, syncProxy ? 2 : 1, 'Only the sync proxy worker must use fresh connections.');
            } finally { await worker.terminate(); port1.close(); }
        }
    } finally {
        try { await pool.close(); }
        finally {
            server.closeAllConnections();
            await new Promise(resolve => server.close(resolve));
        }
    }
});
