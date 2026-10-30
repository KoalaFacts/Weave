import { serveStdio } from '@modelcontextprotocol/server/stdio';
import { createEchoServer } from './index.js';

export function serveEchoStdio() {
    return serveStdio(createEchoServer);
}
