import { createInterface } from 'node:readline';

type ObjectValue = Record<string, unknown>;
const object = (value: unknown): value is ObjectValue =>
  value !== null && typeof value === 'object' && !Array.isArray(value);
const tool = {
  name: 'echo', description: 'Return the supplied text unchanged.',
  inputSchema: { type: 'object', properties: { text: { type: 'string' } },
    required: ['text'], additionalProperties: false },
};
let initialized = false, ready = false;
const error = (id: unknown, code: number, message: string) =>
  ({ jsonrpc: '2.0', id, error: { code, message } });

function handle(message: unknown): ObjectValue | null {
  if (!object(message) || message.jsonrpc !== '2.0' || typeof message.method !== 'string')
    return error(null, -32600, 'Invalid request.');
  const method = message.method;
  if (!Object.hasOwn(message, 'id')) {
    if (method === 'notifications/initialized' && initialized) ready = true;
    return null;
  }
  const id = message.id;
  if (typeof id !== 'string' && !Number.isSafeInteger(id))
    return error(null, -32600, 'Invalid request ID.');
  const params = message.params === undefined ? {} : message.params;
  if (!object(params)) return error(id, -32602, 'Expected object parameters.');
  let result: ObjectValue;
  if (method === 'initialize') {
    initialized = true;
    result = { protocolVersion: '2024-11-05', capabilities: { tools: {} },
      serverInfo: { name: 'echo-plugin', version: '0.1.0' } };
  } else if (method === 'ping') {
    result = {};
  } else if (!ready) {
    return error(id, -32002, 'Server not initialized.');
  } else if (method === 'tools/list') {
    result = { tools: [tool] };
  } else if (method === 'tools/call') {
    if (params.name !== 'echo') return error(id, -32602, 'Unknown tool.');
    const args = params.arguments;
    const valid = object(args) && Object.keys(args).length === 1 && typeof args.text === 'string';
    // Replace this expression with your own tool's behavior.
    const text = valid ? args.text : 'Expected exactly one string argument: text.';
    result = { content: [{ type: 'text', text }], isError: !valid };
  } else {
    return error(id, -32601, 'Unknown method.');
  }
  return { jsonrpc: '2.0', id, result };
}

for await (const line of createInterface({ input: process.stdin, crlfDelay: Infinity })) {
  let message: unknown;
  try { message = JSON.parse(line); }
  catch { console.log(JSON.stringify(error(null, -32700, 'Invalid JSON.'))); continue; }
  const response = handle(message);
  if (response !== null) console.log(JSON.stringify(response));
}
