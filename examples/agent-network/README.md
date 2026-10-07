# Independent agent endpoints

Run a synthetic network walkthrough with one real Weave mailbox relay process and two separate Node endpoint processes. Each endpoint has its own local state directory, its own mailbox-control secret, its own contact policies and its own direct HTTP listener. No model account, hosted agent, npm install or external network service is used.

## Run

From the repository root, use .NET SDK 10 (see `global.json`) and Node 24.19+; Node's built-in SQLite reader is used to inspect active relay rows.

```bash
dotnet restore Weave.slnx --locked-mode
dotnet build hosts/Weave.Mailbox.Host/Weave.Mailbox.Host.csproj --no-restore -c Release
node examples/agent-network/run-demo.ts
```

`--json` emits the actual HTTP responses, process IDs, endpoint-local receipts and active SQLite snapshots used by the acceptance test. All data is synthetic. Default runs remove only their newly created temporary directory after child close and pipe drain are observed. Cancellation interrupts both readiness and pending commands; an unresponsive child receives SIGKILL after a five-second grace period. Close and pipe-drain confirmation are independently bounded, and unconfirmed cleanup is reported as failure. To inspect retained synthetic state, supply an empty directory:

```bash
node examples/agent-network/run-demo.ts --json --state-directory /tmp/weave-network-inspection
```

Absolute and relative supplied paths are resolved once against the directory you run the coordinator from. The directory contains `relay.db`, `alice/state.json` and `bob/state.json`; a nonempty directory is refused. Endpoint state intentionally retains synthetic plaintext receipts after relay ACK. The demo never claims to delete endpoint copies, SQLite historical WAL/media or backups. It does not put control secrets or the fixture encryption key in files or output. The independent processes share the same OS user and are trusted test code; separate processes and credentials are not an OS sandbox.

## What happens

1. Bob publishes five independent cards: three public cards with pending, reject and explicitly selected autoaccept policies, plus long-lived intranet-shared manual and short-lived unlisted autoaccept cards. Public discovery exposes only the three public cards. An audience hint is not member authentication
2. Alice sends contact attempts. Every relay admission starts pending. Bob independently applies each card's endpoint policy. Both participants query body-free request metadata. A manual response includes an optional opaque reply in Alice's inbox only
3. Each policy scenario is deliberately ended with Bob's block/unblock before the next one, because a mailbox pair has one current contact. The final unlisted card's explicit acceptance establishes the delivery channel
4. Alice sends plaintext and AES-256-GCM fixture bytes. Exact retransmission returns the same receipt. An active SQLite read observes the admitted bytes unchanged: plaintext is visible to the relay, encrypted bytes carry no plaintext. Only the endpoint decrypts
5. Bob pulls twice, consumes SSE with Last-Event-ID, restarts with its own saved receipts, and pulls again. The relay repeats unacknowledged messages; Bob's local receipt keys prevent a second local acceptance. Pull and SSE do not ACK
6. Bob explicitly ACKs. Repeated ACK returns the same terminal receipt. A short message expires; another becomes blocked. Unblock restores neither bodies nor admission. A fresh contact and recipient acceptance permit new delivery
7. Both endpoints explicitly admit a direct peer. Alice uses Bob's advertised direct method, including after Bob's listener restart, to deliver and retry over endpoint HTTP. Bob's own direct receipt deduplicates it. The relay has no outbox receipt or message row for that ID
8. The relay restarts on the preserved SQLite file. The original acknowledged receipt remains acknowledged and its body stays removed. All child processes then stop

The [IndependentAgentsTests](../../tests/Weave.Mailboxes.Tests/Acceptance/IndependentAgentsTests.cs) acceptance tests assert these observations and independent state files. Process cleanup uses signal-zero live/stopped controls and a deliberately live negative control. Separate regressions cover noncooperative cancellation before readiness and during a pending command, relative retained-state layout and nonempty-root preservation. A separate SQLite read checks terminal payload removal and absence of a direct-message row. Lower-level mailbox tests exercise exact boundaries, concurrent admission, retained-schema migration and lost-response behavior.

## Fixture and product boundary

`agent.ts` is a deterministic endpoint fixture driven over its private process stdin. It consumes opaque relay messages and uses built-in Node crypto with a temporary symmetric key supplied only to the two endpoint processes. This proves opaque-byte transport; it is not a shipped client E2EE adapter, key exchange, identity authentication or OpenPGP interoperability implementation. The relay never receives that key. Its own control credentials are configured as hashes in trusted host environment settings.

Direct HTTP uses a separate synthetic endpoint-control secret exchanged by the coordinator after explicit endpoint admission. Direct receipts, expiry checks and local copies belong to these endpoints, not to Weave relay guarantees. This is loopback P2P, with no NAT traversal, global discovery, hosted-agent wake or business execution. It does not invoke the separate Governed Tools runtime or change its grants/approval behavior.

The demo refuses generations outside JavaScript's safe-integer range rather than rounding them. A production v1 client must preserve all signed 64-bit generations exactly. See the [wire contract](../../protocol/weave-mailbox/v1/README.md) for query/JSON identity locators, requester-scoped request keys, bounds and HTTPS/control provisioning requirements.
