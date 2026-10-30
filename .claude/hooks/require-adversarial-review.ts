// PreToolUse hook for Bash calls whose command starts with `git commit`.
// Blocks the commit unless the adversarial-review skill has been run
// against the currently staged diff: the marker file's contents must
// equal the SHA-1 fingerprint of the raw `git diff --cached` bytes.
//
// To mark a review complete after running the skill:
//   node .claude/hooks/require-adversarial-review.ts --mark
//
// To override for trivial commits (lockfile regen, typo fixes only):
//   WEAVE_SKIP_ADVERSARIAL_REVIEW=1 git commit ...

import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFileSync, statSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { stdin, stderr } from 'node:process';

type GitResult = { status: number | null; stdout: Buffer };

function runGit(args: string[], captureStdout: boolean): Promise<GitResult> {
  return new Promise(resolve => {
    const chunks: Buffer[] = [];
    const child = spawn('git', args, {
      stdio: ['ignore', captureStdout ? 'pipe' : 'ignore', captureStdout ? 'inherit' : 'ignore'],
    });
    child.stdout?.on('data', (chunk: Buffer) => chunks.push(chunk));
    // A missing or unrunnable git behaves like a failed command with no output.
    child.on('error', () => resolve({ status: null, stdout: Buffer.alloc(0) }));
    child.on('close', status => resolve({ status, stdout: Buffer.concat(chunks) }));
  });
}

function commandOf(input: string): string {
  try {
    const command = JSON.parse(input)?.tool_input?.command;
    return typeof command === 'string' ? command : '';
  } catch {
    // Unparseable input carries no commit command to gate.
    return '';
  }
}

function readMarker(path: string): string | undefined {
  try {
    if (!statSync(path).isFile()) return undefined;
    return readFileSync(path, 'utf8').replace(/[\r\n]+$/, '');
  } catch {
    return undefined;
  }
}

async function stagedFingerprint(projectDir: string): Promise<string> {
  const diff = await runGit(['-C', projectDir, 'diff', '--cached'], true);
  return createHash('sha1').update(diff.stdout).digest('hex');
}

async function main(): Promise<number> {
  const projectDir = process.env.CLAUDE_PROJECT_DIR || process.cwd();
  const markerPath = join(projectDir, '.claude', '.adversarial-review-marker');
  if (process.argv.includes('--mark')) {
    writeFileSync(markerPath, `${await stagedFingerprint(projectDir)}\n`);
    return 0;
  }
  if (process.env.WEAVE_SKIP_ADVERSARIAL_REVIEW === '1') return 0;

  let input = '';
  stdin.setEncoding('utf8');
  for await (const chunk of stdin) input += chunk;

  // Fast path: input that never mentions `git commit` is not a commit.
  if (!input.includes('git commit')) return 0;
  if (!commandOf(input).startsWith('git commit')) return 0;

  // Nothing staged: let `git commit` produce its own error.
  if ((await runGit(['-C', projectDir, 'diff', '--cached', '--quiet'], false)).status === 0) return 0;

  const stagedSha = await stagedFingerprint(projectDir);

  if (readMarker(markerPath) === stagedSha) return 0;

  stderr.write(`adversarial-review missing for the staged diff (fingerprint: ${stagedSha.slice(0, 12)})

This commit is blocked because the adversarial-review skill has not been
run against the currently staged content. Required workflow:

  1. Invoke the adversarial-review skill — see
     .claude/skills/adversarial-review/SKILL.md for the 7-category checklist.
  2. Report the punch list to the user. Fix or acknowledge each finding.
  3. Re-stage if you changed code, then mark the review complete:
       node .claude/hooks/require-adversarial-review.ts --mark
  4. Re-run the commit.

Override for trivial commits (lockfile regen, typo fixes only):
  WEAVE_SKIP_ADVERSARIAL_REVIEW=1 git commit ...

Configuration:
  hook script:    .claude/hooks/require-adversarial-review.ts
  hook config:    .claude/settings.json -> hooks.PreToolUse
  marker file:    .claude/.adversarial-review-marker  (gitignored)
`);
  return 2;
}

process.exitCode = await main();
