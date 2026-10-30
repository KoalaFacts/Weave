// PreToolUse hook for mcp__github__create_pull_request.
// Runs `dotnet format --verify-no-changes` with the same excludes as CI's
// "Check formatting" step (.github/workflows/ci.yml). On failure, blocks the
// PR creation and surfaces the diagnostic so it can be fixed before opening a PR.

import { spawn } from 'node:child_process';
import { stdout } from 'node:process';

// Keep in sync with the --exclude list of CI's "Check formatting" step.
const excludes = [
  'csharp/src/Authority/CapabilityGrantMatcher.cs',
  'csharp/src/Composition/Cqrs/CommandDispatcher.cs', 'csharp/src/Composition/Cqrs/ICommandDispatcher.cs',
  'csharp/src/Composition/Cqrs/ICommandHandler.cs', 'csharp/src/Composition/Cqrs/IQueryDispatcher.cs',
  'csharp/src/Composition/Cqrs/IQueryHandler.cs', 'csharp/src/Composition/Cqrs/QueryDispatcher.cs',
  'csharp/src/Composition/Events/DomainEvent.cs', 'csharp/src/Composition/Events/IDomainEvent.cs',
  'csharp/src/Composition/Events/IEventBus.cs', 'csharp/src/Composition/Events/InProcessEventBus.cs',
  'csharp/src/Agents/AgentId.cs', 'csharp/src/Agents/AgentTaskId.cs', 'csharp/src/Channels/ChannelId.cs',
  'csharp/src/Resources/ContainerId.cs', 'csharp/src/Memory/EpisodeId.cs', 'csharp/src/Plugins/Catalog/MarketplaceItemId.cs',
  'csharp/src/Resources/NetworkId.cs', 'csharp/src/Skills/SkillId.cs', 'csharp/src/Workspaces/Templates/TemplateId.cs',
  'csharp/src/Users/UserId.cs', 'csharp/src/Workspaces/WorkspaceId.cs',
  'csharp/src/Composition/Lifecycle/ILifecycleHook.cs', 'csharp/src/Composition/Lifecycle/ILifecycleManager.cs',
  'csharp/src/Composition/Lifecycle/LifecycleContext.cs', 'csharp/src/Composition/Lifecycle/LifecycleManager.cs',
  'csharp/src/Composition/Lifecycle/LifecyclePhase.cs', 'csharp/src/Plugins/EventBusProxy.cs',
  'csharp/src/Plugins/PluginServiceBroker.cs', 'csharp/src/Credentials/SecretSafeException.cs',
  'csharp/src/Credentials/SecretValue.cs', 'csharp/src/Credentials/SecretValueJsonConverter.cs',
  'csharp/src/Composition/Text/ControlCharFilter.cs', 'csharp/src/Composition/VirtualActors/IActorState.cs',
  'csharp/src/Composition/VirtualActors/IActorTimerRegistry.cs', 'csharp/src/Composition/VirtualActors/IVirtualActorProvider.cs',
  'csharp/src/Composition/VirtualActors/VirtualActorId.cs', 'csharp/src/Composition/WeavePorts.cs',
];

type FormatResult = { missing: boolean; status: number | null; output: string };

function runFormat(cwd: string): Promise<FormatResult> {
  return new Promise(resolve => {
    const chunks: Buffer[] = [];
    const child = spawn('dotnet',
      ['format', 'csharp/Weave.slnx', '--no-restore', '--verify-no-changes', '--exclude', ...excludes],
      { cwd, stdio: ['ignore', 'pipe', 'pipe'] });
    const collect = (chunk: Buffer) => chunks.push(chunk);
    child.stdout.on('data', collect);
    child.stderr.on('data', collect);
    child.on('error', (error: NodeJS.ErrnoException) => resolve({
      missing: error.code === 'ENOENT',
      status: null,
      output: error.message,
    }));
    child.on('close', status => resolve({
      missing: false,
      status,
      output: Buffer.concat(chunks).toString('utf8'),
    }));
  });
}

const result = await runFormat(process.env.CLAUDE_PROJECT_DIR || '.');

// No SDK available: don't block; CI still gates format.
if (!result.missing && result.status !== 0) {
  const reason = 'dotnet format --verify-no-changes failed. Fix with:\n' +
    `  dotnet format csharp/Weave.slnx --no-restore --exclude ${excludes.join(' ')}\n\n` +
    result.output.replace(/[\r\n]+$/, '');
  stdout.write(`${JSON.stringify({
    hookSpecificOutput: {
      hookEventName: 'PreToolUse',
      permissionDecision: 'deny',
      permissionDecisionReason: reason,
    },
  }, null, 2)}\n`);
}
