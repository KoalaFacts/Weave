import assert from 'node:assert/strict';
import { after, test } from 'node:test';
import { createEchoHttpHandler } from '../dist/index.js';

const handler = createEchoHttpHandler();
after(() => handler.close());

async function post(message) {
    const response = await handler.fetch(new Request('http://localhost/mcp', {
        method: 'POST',
        headers: {
            accept: 'application/json, text/event-stream',
            'content-type': 'application/json'
        },
        body: JSON.stringify(message)
    }));
    const body = await response.text();
    const data = response.headers.get('content-type')?.includes('text/event-stream')
        ? body.split(/\r?\n/).filter(line => line.startsWith('data:')).map(line => JSON.parse(line.slice(5)))
        : body ? [JSON.parse(body)] : [];
    return { response, data };
}

test('Streamable HTTP accepts the Weave legacy handshake and echoes over SSE', async () => {
    const initialized = await post({
        jsonrpc: '2.0', id: 1, method: 'initialize',
        params: {
            protocolVersion: '2024-11-05',
            capabilities: {},
            clientInfo: { name: 'weave', version: '0.1.0' }
        }
    });
    assert.equal(initialized.response.status, 200);
    assert.equal(initialized.data.at(-1)?.result?.protocolVersion, '2024-11-05');

    const notice = await post({ jsonrpc: '2.0', method: 'notifications/initialized' });
    assert.equal(notice.response.status, 202);

    const listed = await post({ jsonrpc: '2.0', id: 2, method: 'tools/list' });
    assert.equal(listed.response.status, 200);
    assert.deepEqual(listed.data.at(-1)?.result?.tools.map(tool => tool.name), ['echo']);

    const called = await post({
        jsonrpc: '2.0', id: 3, method: 'tools/call',
        params: { name: 'echo', arguments: { text: 'through Weave' } }
    });
    assert.equal(called.response.status, 200);
    assert.deepEqual(called.data.at(-1)?.result?.content, [
        { type: 'text', text: 'through Weave' }
    ]);
});
