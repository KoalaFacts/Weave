# @koalafacts/weave-plugin-echo

A small, runnable MCP Echo plugin for Weave. It returns the supplied text
unchanged. The package uses the official MCP TypeScript server SDK and offers
Streamable HTTP for a loopback service and stdio for a Host-launched process.
The same tool definition serves both transports.

## Install and run

Requires Node.js 22.14 or later.

```sh
npm install @koalafacts/weave-plugin-echo
npx weave-plugin-echo --stdio
```

The stdio command waits for an MCP client on stdin and writes only protocol
messages to stdout. For a local HTTP endpoint, choose an unused port:

```sh
npx weave-plugin-echo --http --port 9450
```

This listens on `127.0.0.1` at `/mcp`. It validates local Host and Origin
headers. It prints the endpoint to stderr so stdio output stays clean.
Streamable HTTP may use an SSE response for a tool call; it is not the older
two-endpoint HTTP+SSE transport.

## Connect from Weave

An administrator can add one of these tool definitions to a workspace
manifest. For stdio, run `npm root` in the installation directory and append
`@koalafacts/weave-plugin-echo/dist/cli.js` to find the JavaScript entry
point. Set `server` to the approved Node executable and `args[0]` to the
absolute path of that file. This works on Windows too, where npm's command
shim cannot be launched directly by Weave's process runner:

```json
{
  "echo-sample": {
    "type": "mcp",
    "mcp": {
      "server": "node",
      "args": ["<absolute path to installed dist/cli.js>", "--stdio"]
    }
  }
}
```

For the loopback HTTP process started above:

```json
{
  "echo-sample": {
    "type": "mcp",
    "mcp": {
      "url": "http://127.0.0.1:9450/mcp",
      "allow_private_endpoints": true
    }
  }
}
```

The JSON blocks are the entries for a workspace manifest's `tools` map.
Grant an Agent access to `echo-sample` and explicitly grant
`tool:echo-sample:invoke:echo` before it can call the Echo operation. The
invocation method is `echo`; its `text` argument is a string. Weave
authorizes the caller, records the attempt and controls approval where
configured. This plugin does not receive Weave signing keys or implement a
second policy engine. Installing an npm package never grants a Weave operation.

The package is a small external MCP plugin, not Weave's internal
`PluginInstallation` implementation. Weave currently uses the legacy MCP
initialize handshake, which this package's server accepts; modern MCP
clients can use the same Streamable HTTP endpoint.

## Use the Echo definition in another server

```js
import { createEchoServer, createEchoHttpHandler } from '@koalafacts/weave-plugin-echo';
import { serveEchoStdio } from '@koalafacts/weave-plugin-echo/stdio';

// Choose one entry point for a process.
serveEchoStdio();

// Alternatively, mount createEchoHttpHandler().fetch in a web runtime.
```

`createEchoHttpHandler()` returns the official SDK's web-standard handler.
It verifies no HTTP authentication itself. For remote deployment, put
authentication, Host/Origin validation, TLS and network access controls in
front of the handler. The executable's built-in HTTP mode intentionally binds
only loopback.

## Build from this repository

```sh
cd npm/weave-plugin-echo
npm ci
npm run check
npm test
npm pack --dry-run --json
```

The independent repository workflow also drives the compiled stdio entry
through Weave's real MCP connector, exact operation authorization and
invocation journal. The package includes only built JavaScript, declarations,
this README and both license texts. This early package does not provide
network isolation for plugin code or a general plugin authoring framework.

## Publishing

The first release requires an npm account with publish access to the
`@koalafacts` scope. After this change is merged and checks pass, the
maintainer runs `npm ci`, `npm test`, `npm run pack:check`, and
`npm publish --access public` from this directory, completing npm's
interactive authentication. The package must exist on npm before its
trusted publisher can be configured.

For later releases, configure a GitHub Actions trusted publisher on npm for
`KoalaFacts/Weave`, workflow `publish-npm-plugin-echo.yml`, and environment
`npm`, with direct `npm publish` allowed. Protect that GitHub environment
with required reviewers. Bump the version in `package.json`,
`package-lock.json`, and `src/index.ts` together; the pack check rejects a
version mismatch. Merge the reviewed change, then manually dispatch the
publish workflow from `main` with that exact version. The workflow uses OIDC
and does not require a stored npm write token. See the
[npm trusted publishing guide](https://docs.npmjs.com/trusted-publishers/).
