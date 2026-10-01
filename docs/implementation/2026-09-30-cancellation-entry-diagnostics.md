# HTTP cancellation entry diagnostics

## Observed failure

During the network-recovery change, the second full Release test run on commit `50742a7` timed out while `GovernedHttpCancellationTests` waited for the outcome-query request to enter its authorization gate. A focused rerun and the third full run passed. The original failure did not retain the HTTP completion status or whether the authorizer had started, so it does not establish why entry was delayed.

## Delivered diagnostic boundary

The test now observes both the gate and the pending HTTP task. A response that completes before authorization reports its HTTP status immediately; a fault propagates its original error. The existing authorization-entry deadline reports the operation, HTTP task status and whether the real Grain authorizer started. These diagnostics contain no capability token, signature, request body or response body.

The authorization-entry deadline remains 15 seconds, the cancellation observation remains 5 seconds, and the gate release remains bounded. Tests still require cancellation inside the real ToolActor Grain, unchanged filesystem content, no new invocation admission and an unchanged pending approval where applicable.

A second regression keeps two real Host/Orleans factories alive with the same Tool actor key. It connects one actor, verifies the second remains unconnected, and checks both HTTP health endpoints. This checks actual actor-state isolation rather than assuming that equal default cluster option values establish interference.

## Investigation and limits

One whole Host suite and three concurrent focused cancellation-test processes passed during investigation. The simultaneous-Host actor checks also passed. The default-cluster-name hypothesis was not supported by actual state sharing and produced no configuration change.

At this stage the intermittent failure's root cause remained unconfirmed. This diagnostic increment improved failure evidence; it did not claim to fix the original intermittent timeout. The subsequent [runtime readiness increment](2026-09-30-workspace-runtime-readiness.md) proceeded as a separate query change and retained these diagnostics. There were no production authorization, cancellation, journal, persistence or runtime changes in this diagnostic increment.

The change's PR records final verification commands, repetitions, skips and the exact tested commit. The existing default-suite Docker/Postgres, Windows symlink and live Echo endpoint prerequisites still apply.

## Follow-up: initialize HTTP routing before the cancellation probe

On 2026-10-01, a temporary diagnostic driver reused the five existing cancellation paths with 24 concurrent workers and up to ten fresh factories per worker. It reproduced the authorization-entry timeout twice: first for an invocation POST, then for an outcome GET. In both failures the HTTP middleware had entered, but neither Grain resolution nor authorization had started. The GET additionally reported no matched endpoint and no server body read. Request processing measured inside the audit middleware stayed below the 15-second entry deadline.

The real host had started, but its first HTTP request was still initializing routing. ASP.NET Core's [routing middleware](https://github.com/dotnet/aspnetcore/blob/v10.0.7/src/Http/Routing/src/EndpointRoutingMiddleware.cs) creates its matcher on the first request, and the [WebApplication pipeline](https://github.com/dotnet/aspnetcore/blob/v10.0.7/src/DefaultBuilder/src/WebApplicationBuilder.cs) puts that routing stage before the application's audit and invocation handlers. `CreateClient()` and a successful direct Grain setup call do not establish that HTTP initialization has completed.

The cancellation fixture now completes a real `GET /health` and requires HTTP 200 before sending the governed request. Its observer records that a matched health endpoint completed before the cancellation probe; removing the readiness request makes all five regression cases fail for that ordering violation. An unsuccessful readiness response fails setup rather than starting the probe. Readiness uses the client's existing bounded timeout and test cancellation token, with no retries.

The five regression cases failed before the readiness request was added and passed afterward. The same 24-worker driver then completed all 240 cancellation flows successfully, retaining each path's real cancellation and side-effect assertions.

The authorization-entry deadline remains 15 seconds and cancellation observation remains 5 seconds. The real HTTP-to-Grain cancellation, unchanged file, absent admission and unchanged pending approval assertions remain in place. No production behavior, Orleans ports, cluster identity, dependencies or test parallelism are changed.

This identifies the cold-routing cause in the reproduced failures; older failures did not capture routing progress, so their individual causes cannot be established retroactively. It does not establish a production cold-start latency guarantee. Temporary stress drivers and body-read instrumentation were removed before the final build. Profiler and log artifacts stay outside the repository; the PR records verification results, tested commits and any operator-local cleanup limits.
