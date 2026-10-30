#!/usr/bin/env node
import { createServer } from 'node:http';
import { localhostHostValidation, localhostOriginValidation, toNodeHandler } from '@modelcontextprotocol/node';
import { createEchoHttpHandler } from './index.js';
import { serveEchoStdio } from './stdio.js';

function readPort(raw: string | undefined): number {
    const port = Number(raw);
    if (!Number.isInteger(port) || port < 0 || port > 65535)
        throw new Error('--port must be an integer from 0 to 65535.');
    return port;
}

async function main(args: string[]): Promise<void> {
    if (args.length === 1 && (args[0] === '--help' || args[0] === '-h')) {
        process.stdout.write('Usage: weave-plugin-echo [--stdio | --http --port PORT]\n');
        return;
    }
    if (args.length === 0 || (args.length === 1 && args[0] === '--stdio')) {
        serveEchoStdio();
        return;
    }
    if (args.length !== 3 || args[0] !== '--http' || args[1] !== '--port')
        throw new Error('Use --stdio or --http --port PORT.');

    const port = readPort(args[2]);
    const handler = createEchoHttpHandler();
    const nodeHandler = toNodeHandler(handler);
    const validateHost = localhostHostValidation();
    const validateOrigin = localhostOriginValidation();
    const server = createServer((request, response) => {
        if (request.url?.split('?', 1)[0] !== '/mcp') {
            response.writeHead(404).end();
            return;
        }
        if (!validateHost(request, response) || !validateOrigin(request, response))
            return;
        void nodeHandler(request, response).catch(() => {
            process.stderr.write('weave-plugin-echo: MCP request failed.\n');
            if (!response.headersSent)
                response.writeHead(500).end();
            else
                response.destroy();
        });
    });
    await new Promise<void>((resolve, reject) => {
        server.once('error', reject);
        server.listen(port, '127.0.0.1', resolve);
    });
    const address = server.address();
    if (!address || typeof address === 'string')
        throw new Error('Echo HTTP listener has no TCP port.');
    process.stderr.write(`Echo MCP endpoint: http://127.0.0.1:${address.port}/mcp\n`);
    const close = async () => {
        server.close();
        await handler.close();
    };
    const closeOnSignal = () => {
        void close().catch(() => {
            process.stderr.write('weave-plugin-echo: shutdown failed.\n');
            process.exitCode = 1;
        });
    };
    process.once('SIGINT', closeOnSignal);
    process.once('SIGTERM', closeOnSignal);
}

main(process.argv.slice(2)).catch(error => {
    process.stderr.write(`weave-plugin-echo: ${error instanceof Error ? error.message : String(error)}\n`);
    process.exitCode = 1;
});
