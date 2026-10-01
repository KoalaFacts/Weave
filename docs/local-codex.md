# Use Codex with local document approval

This route connects your existing Codex to a single local Weave Host. You choose a
document folder. Codex can read its documents and propose writes; you personally
review each required write before the original Agent continues. Weave retains the
proposal and execution record by UUID.

## What you need

- A published bundle containing the `weave` executable and its sibling `host/`
  directory. Both executables include their .NET runtime; using this bundle needs
  no .NET SDK, Python, Node.js, npm install or wrapper script.
- The official Codex CLI installed and signed in using its normal login process.
  Weave does not copy login files, install a model provider or create API keys.
- One local Host at a time. HTTP port 9401 and Orleans ports 11111/30000 must be
  available. This profile binds its HTTP interface to literal `127.0.0.1`.

If `weave` is not on your PATH, substitute the path to the extracted executable in
the commands below. Keep the complete `host/` directory beside it.

## Start in a new directory

Create a project directory and an existing `documents/` folder containing your
test documents, such as `meeting.txt` and `constraints.txt`. Run from that project
directory:

```text
weave local init --documents documents
weave local serve
```

Initialization creates a private `.weave/` deployment, generates distinct signing
and operator keys, initializes the Host's journal and revocation storage through
the ordinary Host, then stops that initialization process. It marks the deployment
ready only after both stores exist and subsequent boots require preserved storage.
It does not overwrite an existing deployment or create summary answers.
The private directory also contains a Git ignore rule for its contents, reducing
the risk of accidentally committing credentials or retained records.

`serve` runs the published executable and reconnects the configured document tool
through the protected operator API. Keep this terminal open; Ctrl+C stops this
Host. The document folder, `.weave/` and the Agent working directory must be
separate sibling directories, without symbolic links or directory redirection in
their components. Documents cannot contain private state or configuration.

If you published Host and CLI separately, provide the published Host once:

```text
weave local init --documents documents --host runtime/Weave.Silo.exe
```

Use `runtime/Weave.Silo` on Linux/macOS. A supplied `.dll` is a development escape
hatch requiring a separately installed .NET runtime; it does not establish the
runtime-free bundle claim. `--directory` selects a different private deployment;
use that same value for all later local commands.

## Let Codex propose a write

In a second terminal, from the same project directory:

```text
weave local codex
```

This launches a fresh Codex with only the four `weave_files` business tools and a
30-minute Agent capability. Your global Codex configuration is preserved, but is
not loaded for this session; its normal login remains available. Codex runs in the
sibling `agent/` working directory. Weave does not pass signing, operator or
reviewer credentials to this process. If the executable is not on PATH, use
`--codex` with the actual Codex executable.

Give it your document task, for example: “Read meeting.txt and constraints.txt
through Weave and propose summary.md with decisions, actions, owners, dates and
unconfirmed information.” Codex generates the content. On `Pending`, it reports
the UUID and stops; that result is not execution success.

For an explicit noninteractive task, the CLI also supports:

```text
weave local codex --exec --task "Read the two documents through Weave and propose summary.md; stop at Pending."
```

This uses Codex's normal automatic tool review and workspace sandbox. It does not
bypass Weave's required human approval, supply a human decision, or skip Codex's
sandbox protections.

## Personally review the UUID

In a third interactive terminal, run:

```text
weave local review --id ORIGINAL_UUID
```

The CLI obtains a separate, independent reviewer capability and retrieves the
server-verified original. It displays the target, complete content, requester,
expiry and digest. Terminal control and direction-changing characters are escaped;
ordinary document text remains readable. Type the exact displayed `approve
approval-v1:...` or `reject approval-v1:...` confirmation yourself. Redirected input
or output cannot perform human review. There is no approval command-line flag.

Approval records a decision; it does not execute the write. Tell Codex to query
that same UUID and continue only when Approved with no recorded execution. On
rejection, expiry, cancellation or an unknown outcome, stop. Do not change the
body or choose another UUID to bypass that result.

## Query and recover

```text
weave local status --id ORIGINAL_UUID
```

After Host restart, run `serve` against the same private directory. After Agent
restart or capability expiry, launch `codex` again and tell it the original UUID.
The CLI obtains fresh credentials for the same narrow subject and operations;
the server retains the original proposal. A known submission is query-only, and a
recorded result is never automatically replayed. Local receipts contain only
UUID/hash data, not the original document body or tokens.

Keep `.weave/` intact, including its database and revocation directory. Missing
retained storage fails startup. Interrupted initialization leaves its private
files for investigation and cannot be served or silently reset. Do not delete a
deployment to work around an unknown outcome.

## Security boundary

This is a same-user local convenience profile, not operating-system isolation,
human login/MFA, remote deployment or distributed exactly-once execution. Windows
ACLs restrict the private deployment to the current user and SYSTEM; on Unix its
root has mode 0700. The same local user still owns the Host and Agent processes.

Global API authentication is explicitly `none` only in this brand-new loopback
profile. Agent endpoints require narrow signed capabilities, while management and
review additionally require the protected operator key. This route does not
reconfigure existing deployments, disable their authentication or trust remote
cleartext/forwarded headers. Protect or isolate the Host separately before using
an untrusted Agent or non-local deployment.

## Publish a local bundle (contributors)

From the repository root, with its supported SDK installed:

```text
dotnet publish hosts/Weave.Cli/Weave.Cli.csproj -c Release -r win-x64 --self-contained true -p:PackAsTool=false -o artifacts/local-bundle
dotnet publish hosts/Weave.Host/Weave.Host.csproj -c Release -r win-x64 --self-contained true -o artifacts/local-bundle/host
```

Change the RID for the target platform. The release workflow packages the same
CLI-plus-Host layout. These build commands require an SDK; operating the resulting
bundle does not. Publishing locally is not a public release or package upload.
