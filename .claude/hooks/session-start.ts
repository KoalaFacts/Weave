// SessionStart hook: installs the .NET SDK and restores NuGet packages in
// remote (Claude Code on the web) environments. A no-op everywhere else.

import { spawnSync } from 'node:child_process';
import { accessSync, appendFileSync, chmodSync, constants, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { delimiter, join } from 'node:path';
import { stderr, stdout } from 'node:process';

class HookFailure extends Error {
  readonly status: number;

  constructor(message: string, status: number) {
    super(message);
    this.status = status;
  }
}

function requiredEnv(name: string): string {
  const value = process.env[name];
  if (value === undefined) throw new HookFailure(`session-start: ${name} is not set`, 1);
  return value;
}

function run(file: string, args: string[], options: { cwd?: string; env?: NodeJS.ProcessEnv } = {}): void {
  const result = spawnSync(file, args, { ...options, stdio: 'inherit' });
  if (result.error) throw new HookFailure(`session-start: ${file}: ${result.error.message}`, 127);
  if (result.status !== 0) throw new HookFailure(`session-start: ${file} exited with ${result.status ?? result.signal}`, result.status ?? 1);
}

function installedVersion(dotnet: string): string | undefined {
  try {
    accessSync(dotnet, constants.X_OK);
  } catch {
    return undefined;
  }
  const result = spawnSync(dotnet, ['--version'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] });
  return result.status === 0 ? result.stdout.replace(/[\r\n]+$/, '') : undefined;
}

async function main(): Promise<void> {
  // Only run in remote (Claude Code on the web) environments.
  if (process.env.CLAUDE_CODE_REMOTE !== 'true') return;

  // Run asynchronously: the session starts while the SDK installs in the background.
  stdout.write('{"async": true, "asyncTimeout": 300000}\n');

  const dotnetInstallDir = join(requiredEnv('HOME'), '.dotnet');
  const dotnet = join(dotnetInstallDir, 'dotnet');

  const existing = installedVersion(dotnet);
  // global.json selects the newest SDK; anything older than .NET 11 still needs the preview install.
  if (existing?.startsWith('11.')) {
    stdout.write(`.NET SDK already installed: ${existing}\n`);
  } else {
    stdout.write('Installing .NET 10 and .NET 11 preview SDKs...\n');
    const response = await fetch('https://dot.net/v1/dotnet-install.sh');
    if (!response.ok) throw new HookFailure(`session-start: dotnet-install.sh download failed: HTTP ${response.status}`, 22);
    const installer = join(tmpdir(), 'dotnet-install.sh');
    writeFileSync(installer, Buffer.from(await response.arrayBuffer()));
    chmodSync(installer, 0o755);
    // The .NET 10 SDK supplies the net10.0 runtime; global.json selects the .NET 11 SDK for net11.0 targets.
    run(installer, ['--channel', '10.0', '--install-dir', dotnetInstallDir]);
    run(installer, ['--channel', '11.0', '--quality', 'preview', '--install-dir', dotnetInstallDir]);
    rmSync(installer, { force: true });
    stdout.write(`.NET SDK installed: ${installedVersion(dotnet) ?? ''}\n`);
  }

  // Persist PATH, DOTNET_ROOT, and suppress telemetry for the session.
  const envFile = requiredEnv('CLAUDE_ENV_FILE');
  appendFileSync(envFile, `export PATH="${dotnetInstallDir}:$PATH"\n`);
  appendFileSync(envFile, `export DOTNET_ROOT="${dotnetInstallDir}"\n`);
  appendFileSync(envFile, 'export DOTNET_CLI_TELEMETRY_OPTOUT=1\n');
  appendFileSync(envFile, 'export DOTNET_NOLOGO=1\n');

  // Restore NuGet packages so builds are fast.
  run(dotnet, ['restore', 'csharp/Weave.slnx', '--verbosity', 'quiet'], {
    cwd: requiredEnv('CLAUDE_PROJECT_DIR'),
    env: {
      ...process.env,
      PATH: `${dotnetInstallDir}${delimiter}${process.env.PATH ?? ''}`,
      DOTNET_ROOT: dotnetInstallDir,
    },
  });
  stdout.write('NuGet packages restored.\n');
}

try {
  await main();
} catch (error) {
  if (!(error instanceof HookFailure)) throw error;
  stderr.write(`${error.message}\n`);
  process.exitCode = error.status;
}
