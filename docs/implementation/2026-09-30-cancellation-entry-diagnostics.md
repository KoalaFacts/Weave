# HTTP cancellation entry diagnostics

## Observed failure

During the network-recovery change, the second full Release test run on commit `50742a7` timed out while `GovernedHttpCancellationTests` waited for the outcome-query request to enter its authorization gate. A focused rerun and the third full run passed. The original failure did not retain the HTTP completion status or whether the authorizer had started, so it does not establish why entry was delayed.

## Delivered diagnostic boundary

The test now observes both the gate and the pending HTTP task. A response that completes before authorization reports its HTTP status immediately; a fault propagates its original error. The existing authorization-entry deadline reports the operation, HTTP task status and whether the real Grain authorizer started. These diagnostics contain no capability token, signature, request body or response body.

The authorization-entry deadline remains 15 seconds, the cancellation observation remains 5 seconds, and the gate release remains bounded. Tests still require cancellation inside the real ToolActor Grain, unchanged filesystem content, no new invocation admission and an unchanged pending approval where applicable.

A second regression keeps two real Host/Orleans factories alive with the same Tool actor key. It connects one actor, verifies the second remains unconnected, and checks both HTTP health endpoints. This checks actual actor-state isolation rather than assuming that equal default cluster option values establish interference.

## Investigation and limits

One whole Host suite and three concurrent focused cancellation-test processes passed during investigation. The simultaneous-Host actor checks also passed. The default-cluster-name hypothesis was not supported by actual state sharing and produced no configuration change.

The intermittent failure's root cause remains unconfirmed. This change improves failure evidence; it does not claim to fix the original intermittent timeout. Workspace readiness behavior awaits a confirmed diagnosis. There are no production authorization, cancellation, journal, persistence or runtime changes in this increment.

The change's PR records final verification commands, repetitions, skips and the exact tested commit. The existing default-suite Docker/Postgres, Windows symlink and live Echo endpoint prerequisites still apply.
