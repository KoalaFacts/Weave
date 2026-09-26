import { createMcpHandler, McpServer } from '@modelcontextprotocol/server';
import { z } from 'zod/v4';

export const echoPluginName = 'weave-plugin-echo';
export const echoPluginVersion = '0.1.0';

export function createEchoServer(): McpServer {
    const server = new McpServer({ name: echoPluginName, version: echoPluginVersion });
    server.registerTool(
        'echo',
        {
            description: 'Return the supplied text unchanged.',
            inputSchema: z.object({ text: z.string() })
        },
        async ({ text }) => ({ content: [{ type: 'text', text }] })
    );
    return server;
}

export function createEchoHttpHandler() {
    return createMcpHandler(createEchoServer, { responseMode: 'sse' });
}
