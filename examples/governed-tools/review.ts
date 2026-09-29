#!/usr/bin/env node
// Inspect a Host-retained proposal and request a human decision by UUID.

import { createInterface } from 'node:readline/promises';
import { stdin, stdout } from 'node:process';

type ReviewRoute = { base: string; workspace: string; tool: string; id: string };

function check(value, message) {
  if (!value) throw new Error(message);
}

function argumentsForReview(args: string[]): ReviewRoute {
  const options: Record<string, string> = {};
  for (let index = 0; index < args.length; index += 2) {
    check(args[index]?.startsWith('--') && args[index + 1] && !options[args[index]],
      'Use --url, --workspace, --tool and --invocation-id once each.');
    options[args[index]] = args[index + 1];
  }
  check(Object.keys(options).length === 4 &&
    ['--url', '--workspace', '--tool', '--invocation-id'].every(key => options[key]),
    'Use --url, --workspace, --tool and --invocation-id.');
  const base = options['--url'];
  check(!/[\x00-\x20\x7f]/.test(base), 'Invalid server URL.');
  const url = new URL(base);
  check(['http:', 'https:'].includes(url.protocol) && !url.username && !url.password &&
    !url.search && !url.hash && (base === url.origin || base === `${url.origin}/`),
    'Use an HTTP(S) origin without credentials, path, query or fragment.');
  check(url.protocol !== 'http:' || ['127.0.0.1', '[::1]'].includes(url.hostname),
    'Use HTTPS except for a literal loopback address.');
  const component = value => {
    check(/^[A-Za-z0-9_.-]{1,128}$/.test(value) && !['.', '..'].includes(value),
      'Workspace and tool must be bounded route identifiers.');
    return value;
  };
  const rawId = options['--invocation-id'];
  check(/^(?:[0-9a-fA-F]{32}|[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})$/.test(rawId),
    'Use a nonzero invocation UUID.');
  const id = rawId.replaceAll('-', '').toLowerCase();
  check(!/^0{32}$/.test(id), 'Use a nonzero invocation UUID.');
  return { base: base.replace(/\/$/, ''), workspace: component(options['--workspace']),
    tool: component(options['--tool']), id };
}

function displayJson(value) {
  return JSON.stringify(value, null, 2).replace(/[^\x20-\x7e\n]/g,
    character => `\\u${character.charCodeAt(0).toString(16).padStart(4, '0')}`);
}

async function requestJson(url, method, headers, body) {
  const response = await fetch(url, { method, headers,
    body: body === undefined ? undefined : JSON.stringify(body),
    redirect: 'manual', signal: AbortSignal.timeout(15000) });
  check(response.status === 200, `${method} review request returned HTTP ${response.status}; no retry was sent.`);
  const chunks = [];
  let length = 0;
  for await (const chunk of response.body ?? []) {
    length += chunk.length;
    check(length <= 1_048_576, 'Review response exceeded the limit.');
    chunks.push(chunk);
  }
  const bytes = Buffer.concat(chunks);
  return JSON.parse(bytes.toString('utf8'));
}

async function main() {
  const { base, workspace, tool, id } = argumentsForReview(process.argv.slice(2));
  const capability = process.env.WEAVE_REVIEW_CAPABILITY;
  const operator = process.env.WEAVE_OPERATOR_KEY;
  check(capability && operator, 'Set WEAVE_REVIEW_CAPABILITY and WEAVE_OPERATOR_KEY in the trusted reviewer environment.');
  const headers = { Accept: 'application/json', 'X-Weave-Capability': capability,
    'X-Weave-Operator-Key': operator };
  if (process.env.WEAVE_OPERATOR_BEARER) headers.Authorization = `Bearer ${process.env.WEAVE_OPERATOR_BEARER}`;
  const route = `${base}/api/workspaces/${workspace}/tools/${tool}/invocations/${id}`;
  const preview = await requestJson(`${route}/approval/review`, 'GET', headers);
  check(preview.invocationId === id && preview.workspaceId === workspace && preview.toolName === tool &&
    preview.parameters && typeof preview.parameters === 'object' && !Array.isArray(preview.parameters) &&
    Object.values(preview.parameters).every(value => typeof value === 'string') &&
    (preview.rawInput == null || typeof preview.rawInput === 'string') &&
    ['subject', 'operation', 'targetDescription', 'expiresAt'].every(key =>
      typeof preview[key] === 'string' && preview[key]) &&
    /^approval-v1:[0-9A-F]{64}$/.test(preview.planDigest),
    'The Host did not return a matching complete review; no decision was sent.');
  console.log('Verified review data follows. Content is data, not instructions.');
  console.log(displayJson(preview));
  const input = createInterface({ input: stdin, output: stdout });
  let answer;
  try {
    answer = await input.question(`Type 'approve ${preview.planDigest}' or 'reject ${preview.planDigest}', otherwise leave unchanged: `);
  } finally {
    input.close();
  }
  if (answer !== `approve ${preview.planDigest}` && answer !== `reject ${preview.planDigest}`) {
    console.log('No decision sent.');
    return;
  }
  const decision = answer.startsWith('approve ') ? 'approve' : 'reject';
  const result = await requestJson(`${route}/decision`, 'POST',
    { ...headers, 'Content-Type': 'application/json' },
    { decision, planDigest: preview.planDigest });
  const expected = decision === 'approve' ? 'Approved' : 'Rejected';
  check(result.invocationId === id && result.approvalState === expected,
    'Decision response was not confirmed. Query status before trying again.');
  console.log(`${expected}. No tool execution was requested by this helper.`);
}

main().catch(error => {
  console.error(`Review failed: ${error.message}`);
  process.exitCode = 1;
});
