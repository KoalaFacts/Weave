# External Python TCP client: routing readiness and pipe readers

## Reproduced failure

The real TCP regression starts Kestrel, connects a FileSystem tool through Orleans, and launches an external Python client. The client must read the original file with an exact read grant, receive HTTP 403 for an unauthorized write, and leave the file unchanged.

A temporary driver ran 24 concurrent workers, each attempting up to five fresh fixtures. The original test failed on its first read: Python raised `TimeoutError` while waiting for the HTTP response status line. The TCP connection had already succeeded. Two subsequent valid instrumented runs reproduced the same first-read timeout.

An instrumented failure measured 10.2499 seconds between server request entry and the matched-endpoint event. By 10.2621 seconds the response had completed, within about 12 milliseconds of matching. This isolates the delay before the application handler in that reproduction. An earlier observation captured a matched endpoint at the timeout without recording its matching time; that observation alone could not locate the delay.

Starting the host and completing a direct Grain setup call do not initialize HTTP routing. ASP.NET Core's [routing middleware](https://github.com/dotnet/aspnetcore/blob/v10.0.12/src/Http/Routing/src/EndpointRoutingMiddleware.cs) creates the matcher on the first request. The [WebApplication pipeline](https://github.com/dotnet/aspnetcore/blob/v10.0.12/src/DefaultBuilder/src/WebApplicationBuilder.cs) places this routing stage before application middleware. Concurrent fresh-host initialization consumed the Python client's 10-second request budget.

## Warming alone was insufficient

The focused test passed after a health request, but the same concurrent driver still reproduced a first-read timeout. Further instrumentation measured routing at 3 milliseconds and completed authorization at 61 milliseconds, followed by a timeout at 10.29 seconds. At that point the shared worker pool had 49 threads and 601 pending work items. The investigation therefore continued rather than treating a passing focused test as a complete fix.

A successful instrumented stress run was sampled with `dotnet-trace`. Its stacks recorded 49 distinct worker threads inside blocking native `ReadFile`, reached through `ThreadPoolWorkQueue.Dispatch`, `AsyncOverSyncWithIoCancellation` and `RandomAccess.ReadAtOffset`. The failing run supplied the worker backlog measurements; this separate profile supplied the blocking call stacks.

The runtime's [Windows Process implementation](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.Windows.cs) creates synchronous stdout/stderr pipes. Its [asynchronous file read implementation](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Private.CoreLib/src/System/IO/RandomAccess.Windows.cs) performs reads of synchronous handles on worker threads. Calling `ReadToEndAsync` on these pipes can occupy one worker per pipe until the child emits output or exits. Those same workers also serve the colocated HTTP/Orleans runtime; the Python child is waiting for that runtime to respond. The warmed failure's backlog and the sampled stacks identify this additional contention mechanism.

## Replacement and regressions

The test now completes one real `GET /health` and requires HTTP 200 before launching Python. It also waits for the fixture-local observer to finish that matched health request, preventing the client's response completion from racing the observer's continuation. Both setup waits use the existing HTTP client's bounded timeout and the test cancellation token. A setup failure stops the test before launching the child; there is no readiness retry.

A fixture-local startup observer records whether a matched health endpoint completed before each POST. Both Python invocations must observe that readiness. Adding this assertion before the health request makes the focused test fail with `[false, false]`; adding the health request makes it pass. This ordering regression remains deterministic even when initialization is fast.

Both pipe readers now perform synchronous reads on separate `LongRunning` tasks using the default task scheduler. They run concurrently on dedicated threads, leaving the host worker pool available. The test records the actual reader thread identity: the worker-pool implementation fails with `[true, true]`, while the dedicated implementation must return `[false, false]`. Capture retains at most 1,048,576 characters per stream, drains any excess to EOF, then rejects excess output.

The test-local process helper kills a still-running process tree on cancellation and observes both readers under an independent five-second cleanup deadline before disposing pipe handles. Real Python regressions cover dual streams larger than pipe capacity, excess stdout and stderr, and cancellation after the child has atomically published its PID. Cancellation checks that the actual child exits; no mock process is used.

The Python request timeout remains 10 seconds, the child-process deadline remains 45 seconds, and process-tree termination retains its five-second cleanup deadline. Concurrent stdout/stderr draining, stderr and exit-code assertions, proxy and redirect restrictions, real TCP transport, exact read permission, HTTP 403 for the write, and unchanged filesystem content are preserved.

The existing private fixture accepts an optional startup observer for this test. Other fixtures retain their behavior. No product code, packages, Orleans configuration, global thread pool settings, test parallelism or execution retry changes. The reader strategy adds two threads per test child and is confined to this test harness.

## Verification limits

The reproduced failures identify cold-routing setup and worker contention in this regression. The older full-suite failure did not retain stderr or matching times, so its individual cause cannot be established retrospectively. This change does not guarantee production cold-start latency, future invocation success, or the absence of every possible Python process failure.

Temporary stress and timing instrumentation are removed before the final build. The PR records the tested commit, repeated stress and full-suite results, prerequisite skips and any cleanup limitations. Repository checklist review is self-review.
