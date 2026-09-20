# One Echo plugin, four languages

Choose **one** implementation: [Python](python/server.py),
[TypeScript](typescript/server.ts), [Rust](rust/src/main.rs), or
[Java](java/src/main/java/EchoServer.java). They all expose the same tool:

```text
echo(text: string) -> the same text
```

These are small plugin-side MCP stdio examples, not client SDKs. There is no
account, model key, HTTP server, database or UI to configure in the plugin.
Weave launches the program, discovers `echo`, and governs its invocation.
The plugin neither receives Weave signing keys nor implements authorization.

## Run the language you use

Run these commands **from the repository root**. Starting a server directly waits
for JSON-RPC on stdin; it is not an interactive chat prompt.

| Language | Requirement | Build | Start |
| --- | --- | --- | --- |
| Python | Python 3.10+ | None | `python3 examples/plugins/echo/python/server.py` |
| TypeScript | Node 22.16+ | None; native type stripping | `node --experimental-strip-types examples/plugins/echo/typescript/server.ts` |
| Rust | Rust/Cargo | `cargo build --manifest-path examples/plugins/echo/rust/Cargo.toml` | `examples/plugins/echo/rust/target/debug/weave-echo-plugin-example` |
| Java | JDK 17+ and Maven | `mvn -q -f examples/plugins/echo/java/pom.xml compile dependency:copy-dependencies` | `java -cp 'examples/plugins/echo/java/target/classes:examples/plugins/echo/java/target/dependency/*' EchoServer` |

Python and TypeScript use their standard runtimes only. Rust uses `serde_json`;
Java uses Gson for JSON, not Spring or an application framework. The TypeScript
launch strips types but is not a compiler type check. Build diagnostics belong
on stderr, never on the protocol's stdout. On Windows, use `;` in the Java
classpath and append `.exe` to the Rust executable path. CI exercises Linux.

## Connect to Weave

Each language has a `tool.json` with the current Weave `ToolSpec` shape. For example:

```json
{
  "name": "echo-sample",
  "type": "mcp",
  "mcp": {
    "server": "python3",
    "args": ["examples/plugins/echo/python/server.py"]
  }
}
```

In an existing workspace manifest, copy its `type` and `mcp` fields into the
`tools` dictionary under the `echo-sample` key (not an array). Alternatively, pass
an equivalent `ToolSpec` to `IToolActor.ConnectAsync` from trusted host code. Use only one variant under this name in a workspace. The paths
assume the Host starts in the repository root; use absolute executable/script or
classpath paths in another deployment. Java's supplied tool.json is for POSIX.

Connecting requires `tool:echo-sample:connect`; calling requires the separate
`tool:echo-sample:invoke:echo` grant. Merely discovering the tool is not authority.
Use the existing administrator/manifest flow to grant it to the intended Agent.
For the governed invocation, use `toolName: "echo-sample"`, `method: "echo"`, and
`parameters: {"text": "hello"}`. Keep the caller's invocation ID when retrying or
querying; the example does not change Weave's execution rules.

To write a different tool, change its discovery schema and the marked expression
in `tools/call`. Do not add a second policy or approval engine inside the plugin.

## Verify the sample

After building your chosen language, this runs the same contract against its real
process, including EOF shutdown:

```bash
python3 examples/plugins/echo/tests/verify_stdio.py examples/plugins/echo/python/tool.json
# Replace python with typescript, rust or java to verify that implementation.
```

The independent **Echo Plugin Examples** CI matrix also runs a shared test through
real `ToolActor`, MCP connector, signed authorization and SQLite journal. It checks
that missing operation permission causes **zero connector calls**, an explicit
grant succeeds, and a duplicate retained ID does not dispatch a second time.
No mocked echo server or mocked journal is used.

The example test source is included in the existing test assembly only when
`EchoPluginExamples=true`. Ordinary backend tests do not require four language
toolchains and the regular CI remains unchanged. To run the opt-in check locally:

```bash
export WEAVE_ECHO_SPEC="$PWD/examples/plugins/echo/python/tool.json"
export EchoPluginExamples=true
# Build your selected language first.
dotnet build tests/Weave.Silo.Tests/Weave.Silo.Tests.csproj -c Release
dotnet test --project tests/Weave.Silo.Tests/Weave.Silo.Tests.csproj --no-build -c Release \
  -- --filter-class Weave.Silo.Tests.Invocations.EchoPluginTests
```

Python is used by the shared verification harness; it is not required to run the
TypeScript, Rust or Java plugin itself. No test silently passes when its selected
runtime or executable is missing.

## Deliberately limited

These examples implement the stdio/tool subset for MCP revision **2024-11-05**,
the version used by the repository's existing MCP connector. They are not complete
MCP SDKs or hardened public servers. No HTTP/SSE, progress streaming, resources,
prompts, arbitrary process isolation or general Provider lifecycle is demonstrated.
Keep inputs small and trusted for this teaching example. Do not send secrets to
Echo: it intentionally returns its input.

A plugin process is not automatically sandboxed. Weave remains responsible for
its actual launch, filesystem/network/credential restrictions and authority.
The existing `examples/echo-mcp` HTTP/SSE fixture is retained unchanged for its
separate regression coverage. The unmerged client-SDK work in #108 is not needed
by these examples, and no SDK/CLI package is published here.

References: [stdio transport](https://modelcontextprotocol.io/specification/2024-11-05/basic/transports),
[lifecycle](https://modelcontextprotocol.io/specification/2024-11-05/basic/lifecycle),
[tools](https://modelcontextprotocol.io/specification/2024-11-05/server/tools).
