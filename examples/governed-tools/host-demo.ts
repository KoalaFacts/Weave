#!/usr/bin/env node
// Exercise the real Host over HTTP with a Node client and scripted reviewer.
// This uses temporary files and the Host's durable SQLite journal.

import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { randomBytes, randomUUID } from 'node:crypto';
import { closeSync, existsSync, mkdirSync, mkdtempSync, openSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, dirname, join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';

const BASE = 'http://127.0.0.1:9401';
const ROUTE = '/api/workspaces/first-use/tools/files/invocations';
const HOST = fileURLToPath(new URL('../../hosts/Weave.Host/bin/Release/net10.0/Weave.Silo.dll', import.meta.url));
const SOURCE = 'Project meeting - synthetic demo data\nDecision: Run a small Weave pilot.\nAction: Alex prepares a sample document.\n';
const INTRO = 'Not written. Awaiting an approved request.\n';

type Invocation = { invocationId: string; toolName: string; method: string;
  parameters: { path: string }; rawInput?: string };

function check(value, message) {
  if (!value) throw new Error(message);
}

function childEnvironment(operator, signing, root) {
  const allowed = new Set(['path', 'dotnet_root', 'ld_library_path', 'lang', 'lc_all', 'tmpdir',
    'systemroot', 'windir', 'temp', 'tmp', 'comspec', 'pathext']);
  const env = Object.fromEntries(Object.entries(process.env).filter(([key]) => allowed.has(key.toLowerCase())));
  return { ...env, HOME: join(root, 'home'), DOTNET_ENVIRONMENT: 'Production',
    ASPNETCORE_ENVIRONMENT: 'Production', DOTNET_NOLOGO: 'true', DOTNET_CLI_TELEMETRY_OPTOUT: 'true',
    CapabilityTokens__SigningKey: signing, Weave__Operator__Key: operator };
}

function configuration(root) {
  const profile = (subject, grants) => ({ WorkspaceId: 'first-use', IssuedTo: subject,
    Lifetime: '00:05:00', Grants: grants });
  return {
    Logging: { LogLevel: { Default: 'Warning', 'Microsoft.AspNetCore': 'Warning', Orleans: 'Warning' } },
    CapabilityTokens: { RevocationDirectory: join(root, 'state', 'revocations'), RequireExistingStorage: false },
    Weave: { LocalMode: true, Auth: { Mode: 'none' },
      Invocations: { DatabasePath: join(root, 'state', 'invocations.db'), RequireExistingStorage: false,
        ApprovalRequiredGrants: ['tool:files:invoke:write_file'],
        Http: { Enabled: true, AgentOnly: false, DecisionsEnabled: true } },
      Operator: { Enabled: true,
        Tools: { files: { WorkspaceId: 'first-use', Tool: { Name: 'files', Type: 'FileSystem',
          FileSystem: { Root: join(root, 'tools') } } } },
        Credentials: {
          reader: profile('agent', ['tool:files:invoke:read_file', 'invocation:read']),
          writer: profile('agent', ['tool:files:invoke:write_file', 'invocation:read']),
          reviewer: profile('reviewer', ['invocation:read', 'approval:decide', 'tool:files:approve:write_file']),
        } } },
  };
}

async function call(method, path, expected, { body, capability, operator } = {}) {
  check(path.startsWith('/api/'), 'Unexpected endpoint in this demo.');
  const headers = { Accept: 'application/json' };
  if (capability) headers['X-Weave-Capability'] = capability;
  if (operator) headers['X-Weave-Operator-Key'] = operator;
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const response = await fetch(BASE + path, { method, headers,
    body: body === undefined ? undefined : JSON.stringify(body),
    redirect: 'manual', signal: AbortSignal.timeout(15000) });
  check(response.status === expected, `${method} ${path}: expected HTTP ${expected}, got ${response.status}.`);
  const chunks = [];
  let length = 0;
  for await (const chunk of response.body ?? []) {
    length += chunk.length;
    check(length <= 1_048_576, 'Response exceeded the demo limit.');
    chunks.push(chunk);
  }
  const bytes = Buffer.concat(chunks);
  return { value: bytes.length ? response.headers.get('content-type')?.includes('json')
    ? JSON.parse(bytes.toString('utf8')) : bytes.toString('utf8') : null, headers: response.headers };
}

async function waitForHost(child, operator, startError) {
  const deadline = Date.now() + 60000;
  while (Date.now() < deadline) {
    if (startError()) throw startError();
    check(child.exitCode === null && child.signalCode === null, 'Host exited before readiness.');
    try {
      await call('GET', '/api/workspaces/', 200, { operator });
      return;
    } catch (error) {
      if (!['TypeError', 'TimeoutError'].includes(error.name)) throw error;
      await delay(250);
    }
  }
  throw new Error('Host readiness deadline exceeded.');
}

async function stopHost(child) {
  if (!child?.pid || child.exitCode !== null || child.signalCode !== null) return;
  const exited = new Promise(resolve => child.once('exit', resolve));
  child.kill('SIGTERM');
  let timer;
  const timedOut = new Promise(resolve => { timer = setTimeout(() => resolve(true), 10000); timer.unref(); });
  const forced = await Promise.race([exited.then(() => false), timedOut]);
  clearTimeout(timer);
  if (forced && child.exitCode === null && child.signalCode === null) {
    child.kill('SIGKILL');
    await exited;
  }
}

function removeTemporaryRoot(root) {
  const resolved = realpathSync(root);
  const parent = realpathSync(tmpdir());
  check(dirname(resolved).toLowerCase() === parent.toLowerCase() &&
    basename(resolved).startsWith('weave-node-host-demo-'), 'Refusing to remove an unexpected directory.');
  rmSync(resolved, { recursive: true, force: true });
}

async function main() {
  check(existsSync(HOST), 'Build the Host first: dotnet build hosts/Weave.Host/Weave.Host.csproj -c Release');
  const root = mkdtempSync(join(tmpdir(), 'weave-node-host-demo-'));
  const operator = randomBytes(48).toString('base64url');
  const signing = randomBytes(48).toString('base64url');
  let child;
  try {
    for (const name of ['tools', 'state', 'home']) mkdirSync(join(root, name));
    writeFileSync(join(root, 'tools', 'meeting.txt'), SOURCE, 'utf8');
    const target = join(root, 'tools', 'summary.md');
    writeFileSync(target, INTRO, 'utf8');
    writeFileSync(join(root, 'appsettings.json'), JSON.stringify(configuration(root)), 'utf8');
    const log = openSync(join(root, 'host.log'), 'w');
    let startError;
    try {
      child = spawn('dotnet', [HOST, '--urls', BASE], { cwd: root,
        env: childEnvironment(operator, signing, root), stdio: ['ignore', log, log] });
      child.once('error', error => { startError = error; });
    } finally {
      closeSync(log);
    }
    await waitForHost(child, operator, () => startError);
    console.log('PASS real Host ready');

    await call('POST', '/api/operator/credentials/writer/issue', 401);
    await call('POST', '/api/operator/tools/files/connect', 204, { operator });
    async function issue(name) {
      const result = await call('POST', `/api/operator/credentials/${name}/issue`, 200, { operator });
      check(result.headers.get('cache-control')?.includes('no-store'), 'Credential response was cacheable.');
      check(typeof result.value === 'string' && /^[A-Za-z0-9_-]+$/.test(result.value), 'Invalid credential.');
      return result.value;
    }
    const reader = await issue('reader');
    const writer = await issue('writer');
    const reviewer = await issue('reviewer');
    console.log('PASS operator-issued, separate capabilities');

    const invocation = (method: string, path: string, rawInput?: string): Invocation => ({
      invocationId: randomUUID().replaceAll('-', ''),
      toolName: 'files', method, parameters: { path }, ...(rawInput === undefined ? {} : { rawInput }) });
    const read = (await call('POST', ROUTE, 200, { body: invocation('read_file', 'meeting.txt'), capability: reader })).value;
    assert.equal(read.output, SOURCE);
    const summary = ['# Meeting summary', '', ...read.output.split('\n').filter(line =>
      line.startsWith('Decision: ') || line.startsWith('Action: ')), ''].join('\n');
    console.log('PASS read and summarize through real HTTP');

    const write = invocation('write_file', 'summary.md', summary);
    await call('POST', ROUTE, 403, { body: write, capability: reader });
    assert.equal(readFileSync(target, 'utf8'), INTRO);
    const pending = (await call('POST', ROUTE, 202, { body: write, capability: writer })).value;
    assert.equal(pending.errorCode, 'approval-pending');
    assert.equal(readFileSync(target, 'utf8'), INTRO);
    console.log('PASS denied read-only write and pending write with no effect');

    const approval = `${ROUTE}/${write.invocationId}`;
    const preview = (await call('GET', `${approval}/approval/review`, 200,
      { capability: reviewer, operator })).value;
    assert.equal(preview.rawInput, summary);
    assert.deepEqual(preview.parameters, write.parameters);
    check(/^approval-v1:[0-9A-F]{64}$/.test(preview.planDigest), 'Missing verified plan digest.');
    const reviewScript = fileURLToPath(new URL('./review.ts', import.meta.url));
    const reviewerEnv = Object.fromEntries(Object.entries(process.env).filter(([key]) =>
      ['path', 'systemroot', 'windir', 'comspec', 'pathext'].includes(key.toLowerCase())));
    const decision = spawnSync(process.execPath, [reviewScript, '--url', BASE,
      '--workspace', 'first-use', '--tool', 'files', '--invocation-id', write.invocationId],
    { cwd: root, env: { ...reviewerEnv, WEAVE_REVIEW_CAPABILITY: reviewer, WEAVE_OPERATOR_KEY: operator },
      input: `approve ${preview.planDigest}\n`, encoding: 'utf8', timeout: 35000, maxBuffer: 1_048_576 });
    check(decision.status === 0 && decision.stdout.includes('Approved. No tool execution'),
      'Node reviewer did not confirm approval.');
    assert.equal(readFileSync(target, 'utf8'), INTRO);
    console.log('PASS separate reviewer approves exact proposal without executing it');

    await call('POST', `${approval}/resume`, 403, { capability: reader });
    const executed = (await call('POST', `${approval}/resume`, 200, { capability: writer })).value;
    assert.equal(executed.success, true);
    assert.deepEqual(readFileSync(target), Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), Buffer.from(summary)]));
    const reread = (await call('POST', ROUTE, 200,
      { body: invocation('read_file', 'summary.md'), capability: reader })).value;
    assert.equal(reread.output, summary);
    console.log('PASS original writer resumes and exact approved content is read back');

    const outcome = (await call('GET', approval, 200, { capability: writer })).value;
    assert.equal(outcome.outcome, 'Succeeded');
    const sentinel = 'Local verification marker: duplicate must not overwrite this line.\n';
    writeFileSync(target, sentinel, 'utf8');
    const duplicate = (await call('POST', ROUTE, 200, { body: write, capability: writer })).value;
    assert.equal(duplicate.isReplay, true);
    assert.equal(readFileSync(target, 'utf8'), sentinel);
    console.log('PASS recorded outcome and same-ID duplicate without a second effect');
    console.log('Real Host flow passed. This scripted reviewer is not independent human approval.');
  } finally {
    await stopHost(child);
    removeTemporaryRoot(root);
  }
}

main().catch(error => {
  console.error(`FAIL real Host demo: ${error.message}`);
  process.exitCode = 1;
});
