import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { Client, StreamableHTTPClientTransport } from '@modelcontextprotocol/client';
import { StdioClientTransport } from '@modelcontextprotocol/client/stdio';

const cli = fileURLToPath(new URL('../dist/cli.js', import.meta.url));

test('the packaged stdio command serves an Echo tool', async () => {
    const client = new Client({ name: 'echo-package-check', version: '0.1.0' });
    try {
        await client.connect(new StdioClientTransport({
            command: process.execPath,
            args: [cli]
        }));
        const tools = await client.listTools();
        assert.deepEqual(tools.tools.map(tool => tool.name), ['echo']);
        const result = await client.callTool({ name: 'echo', arguments: { text: 'stdio' } });
        assert.deepEqual(result.content, [{ type: 'text', text: 'stdio' }]);
    } finally {
        await client.close();
    }
});

test('the HTTP command serves current Streamable HTTP on loopback', async () => {
    const child = spawn(process.execPath, [cli, '--http', '--port', '0'], {
        stdio: ['ignore', 'ignore', 'pipe']
    });
    const ready = new Promise((resolve, reject) => {
        let output = '';
        child.once('error', reject);
        child.once('exit', code => reject(new Error(`Echo server exited before ready: ${code}. ${output}`)));
        child.stderr.on('data', chunk => {
            output += chunk.toString();
            if (output.includes('Echo MCP endpoint:'))
                resolve(output.match(/Echo MCP endpoint: (http:\/\/127\.0\.0\.1:\d+\/mcp)/)?.[1]);
        });
    });
    try {
        const endpoint = await ready;
        assert.ok(endpoint);
        const client = new Client(
            { name: 'echo-package-check', version: '0.1.0' },
            { versionNegotiation: { mode: 'auto' } }
        );
        try {
            await client.connect(new StreamableHTTPClientTransport(new URL(endpoint)));
            assert.equal(client.getProtocolEra(), 'modern');
            const result = await client.callTool({ name: 'echo', arguments: { text: 'HTTP' } });
            assert.deepEqual(result.content, [{ type: 'text', text: 'HTTP' }]);
        } finally {
            await client.close();
        }
    } finally {
        child.kill();
        if (child.exitCode === null)
            await once(child, 'exit');
    }
});
