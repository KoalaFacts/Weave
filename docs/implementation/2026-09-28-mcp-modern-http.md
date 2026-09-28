# Outbound MCP Streamable HTTP: 2026-07-28

The outbound MCP HTTP connector probes `server/discover` with the `2026-07-28` request envelope. A modern server advertising that revision is used without `initialize` or a session. Every later request carries protocol version, client capabilities, and client identity in `params._meta`, plus the required HTTP routing headers. The connector mirrors `x-mcp-header` tool arguments into `Mcp-Param-*` headers, including the protocol's Base64 sentinel encoding for unsafe values. Invalid annotations make that tool unavailable.

A legacy HTTP endpoint that rejects the probe with an unrecognized HTTP 400, or responds with a non-modern JSON-RPC error, continues through the existing `initialize` path. A recognized modern protocol error does not trigger a downgrade. The existing stdio connector remains on its legacy handshake.

The installation still pins server identity, selected operation, and operation contract digest. Probe, connection, and pre-dispatch refresh all use the same revision selection; a changed contract blocks the call. The integration path covers installation, invocation, restart recovery, and disable with a modern-only loopback service. The Python Echo HTTP smoke test covers the legacy path.

An installed tool's `x-mcp-header` annotations expose the selected argument values to HTTP intermediaries. Base64 encoding is transport encoding, not secrecy. Operators should reject schemas that promote credentials or sensitive data into headers.

This slice implements outbound governed tools, not every MCP primitive or extension. `input_required` and task results fail as unsupported without an automatic second `tools/call`. Modern subscriptions, prompts, resources, HTTP+SSE fallback, and sessionful 2025 Streamable HTTP negotiation are outside this connector's current scope. Transport delivery after a timeout still has an unknown outcome; the connector never automatically retries a tool call.

Protocol reference: [MCP 2026-07-28 Streamable HTTP](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/streamable-http) and [versioning](https://modelcontextprotocol.io/specification/2026-07-28/basic/versioning).
