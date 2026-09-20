# Fail-closed capability revocation

This foundation fix changes the existing CapabilityTokenService, not the token
format, grant model, execution path or persistence schema.

## Fixed behavior

File.Exists collapses a missing marker and certain storage/access failures into
false. That is insufficient evidence that an authenticated token is unrevoked.
Validation now uses attribute lookups with explicit failure handling:

- An entry at the token's marker path blocks use, including an unexpected directory.
- A missing marker permits use only when the parent revocation store is still an
  accessible directory and the remaining signature/field/expiry checks succeed.
- Directory loss, an invalid parent path, I/O errors and access-denied lookups block
  use. Validation never recreates an empty directory to hide the fault.
- A transient lookup fault is not cached as a permanent token revocation. Restoring
  the original retained store allows valid, unrevoked tokens to validate again;
  persisted revoked tokens remain rejected.

For canonical IDs, IsRevoked conservatively returns true when the store cannot
establish non-revocation. This is a fail-closed decision, not proof that a new
permanent revocation record has been written. Invalid IDs retain their existing
behavior; Validate rejects them before this lookup.

Revoke still marks a token locally before attempting the persistent write. It
cancels and unregisters its local linked source in a finally block, so a write
failure cannot skip cooperative cancellation. The existing disposed-source race
handling is preserved. Cancellation cannot undo a committed external effect, nor
does it instantly cancel remote processes.

### Preserve both failures

Persistence and cancellation callbacks are independent failure points. A callback
exception must not erase the original storage error while leaving an operator to
assume only cleanup failed. The operation now preserves these distinctions:

| Storage write | Cancellation callbacks | Reported result |
| --- | --- | --- |
| Fails | Succeed or already disposed/cancelled | Original storage exception is rethrown. |
| Succeeds | Fail | Original callback AggregateException propagates; the marker remains recorded. |
| Fails | Fail | AggregateException contains both the original IOException/UnauthorizedAccessException and all callback failures. |
| Succeeds | Succeed | Normal completion; no tool execution is requested. |

The combined exception is assembled outside the cleanup clause. Original exception
objects and stack traces remain available; Flatten exposes every underlying cause.
A callback-thrown ObjectDisposedException remains a callback failure, not a reason
to suppress the whole cancellation error. Healthy registered callbacks still run.
Do not expose arbitrary callback exception messages directly to HTTP clients or
logs without the normal credential/content redaction rules.

After an unconfirmed storage write, restoring the original store and explicitly
retrying revocation for the same token can establish durable denial. This retries
revocation recording, NOT an Agent operation or an unknown external effect. Already
cancelled callbacks are not run again. Tests use an independent verifier before
and after the retry to distinguish local denial from a persisted marker.

## Operational boundaries

The default path and first-initialization behavior are unchanged. For deployment,
explicitly configure CapabilityTokens:RevocationDirectory to a protected persistent
location outside Agent-readable/writable tool roots. Protect and retain this store
with the signing configuration. Do not rely on a temporary directory surviving
cleanup/reboots. Do not replace lost evidence with a newly empty directory to make
requests work again.

This change detects lookup failures in an initialized service. It does NOT detect
an attacker deleting individual markers, replacing the whole store with an empty
usable directory, storage rollback, or loss followed by a fresh process recreating
the store. The file store is not tamper-proof and does not provide a distributed
revocation consensus or power-loss durability guarantee. If evidence is genuinely
lost, keep access blocked until it is restored or all affected credentials are
invalidated through an explicit signing-key/credential recovery procedure. No such
production recovery, credential rotation or data reset is performed by this PR.

A failed Revoke call is not proof either that the marker exists or that it does not.
Inspect the failure category and retained store instead of assuming success or no
change. Local memory and cancellation do not establish propagation to another
verifier or persistence after restart. The caller must surface failures and arrange
deliberate recovery; no automatic fresh token or blind operation retry is introduced.

The HTTP entrance continues to use the same token validator and returns its usual
401 on failed credential validation. Tests additionally inspect real file effects
and invocation records rather than inferring safety solely from the status code.

## Verification

Nine file-backed service cases and three real Host/HTTP/Orleans/filesystem/SQLite
cases were added first. At the test-only head, nine failed for the intended reasons:
missing/malformed storage was accepted and failed revocation writes skipped local
cancellation. Three healthy/persisted/cached controls passed. All pre-existing tests
also passed in that run.

Six additional callback/lifecycle cases cover simultaneous failure, successful
persistence with callback failure, independent-token isolation, explicit durable
retry, already disposed sources and already cancelled parents. The two compound
failure cases first reproduced the missing storage exception while four controls
passed. Exact-head regression and build evidence belongs in the PR record, not
in permanent contributor instructions.

No I/O mock, shared-directory mutation, new dependency, background worker, database
migration, SDK expansion or authentication bypass is involved. The tests restore
only their own isolated directories. Access-denied handling is implemented using
the documented exception contract; deterministic regressions use moved or invalid
paths rather than modifying the runner's user permissions. Blocking/malicious
callbacks and cross-process cancellation delivery are not solved by preserving
exception evidence.

References: [File.Exists](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.exists?view=net-10.0),
[File.GetAttributes](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.getattributes?view=net-10.0),
and [CancellationTokenSource.Cancel](https://learn.microsoft.com/en-us/dotnet/api/system.threading.cancellationtokensource.cancel?view=net-10.0).
