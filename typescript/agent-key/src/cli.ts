#!/usr/bin/env node
import { parseArgs } from 'node:util';
import { accessToken, fetchWithKey, register, WeaveHttpError } from './client.ts';
import { createKey, KeyFileError, loadKey } from './key.ts';

const usage = `Usage:
  weave-agent-key init <keyfile>
  weave-agent-key register <origin> <keyfile> [--name <displayName>]
  weave-agent-key token <origin> <keyfile> <accountId> [--print-token]
  weave-agent-key whoami <origin> <keyfile> <accountId>

The private key never leaves <keyfile>. "token" prints only the expiry unless --print-token is given.`;

class UsageError extends Error {}

const print = (value: unknown): void => { process.stdout.write(JSON.stringify(value) + '\n'); };

async function main(argv: string[]): Promise<void> {
    let parsed;
    try {
        parsed = parseArgs({args: argv, allowPositionals: true, strict: true, options: {name: {type: 'string'}, 'print-token': {type: 'boolean'}}});
    } catch (error) { throw new UsageError((error as Error).message); }
    const [command, ...args] = parsed.positionals;
    const {name, 'print-token': printToken} = parsed.values;
    const arity: Record<string, number> = {init: 1, register: 2, token: 3, whoami: 3};
    if (command === undefined || arity[command] !== args.length) throw new UsageError('Unknown command or wrong number of arguments.');
    if (name !== undefined && command !== 'register' || printToken && command !== 'token') throw new UsageError(`An option does not apply to "${command}".`);
    if (command === 'init') return print(await createKey(args[0]!));
    const [origin, keyfile, accountId] = args as [string, string, string];
    const key = await loadKey(keyfile);
    if (command === 'register') return print({...await register(origin, key, name === undefined ? {} : {displayName: name}), kid: key.kid});
    const token = await accessToken(origin, key, accountId);
    if (command === 'token') return print(printToken ? {token: token.token, expiresAt: token.expiresAt} : {expiresAt: token.expiresAt});
    const {response} = await fetchWithKey(origin, key, token, 'GET', '/v1/keys');
    if (!response.ok) throw new WeaveHttpError('Key list', response.status, null);
    const {items} = await response.json() as {items: unknown};
    print({accountId, kid: key.kid, keys: items});
}

try {
    await main(process.argv.slice(2));
} catch (error) {
    // This package's messages never contain key material or tokens; relay response bodies are never echoed.
    if (error instanceof UsageError) {
        process.stderr.write(`weave-agent-key: ${error.message}\n${usage}\n`);
        process.exitCode = 2;
    } else {
        const known = error instanceof KeyFileError || error instanceof WeaveHttpError;
        process.stderr.write(`weave-agent-key: ${known ? error.message : error instanceof Error ? `${error.name}: ${error.message}` : 'unexpected failure'}\n`);
        process.exitCode = 1;
    }
}
