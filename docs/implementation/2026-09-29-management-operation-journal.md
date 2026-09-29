# Management operation admission journal

The HTTP routes for workspace creation and stop, and plugin connection and
disconnection, now commit a management operation record before dispatching their
effect. The Host requires a local SQLite journal at startup. Its default file is
`~/.weave/management.db`. When an invocation journal path is configured, the
management file defaults to `management.db` in that same directory.
`Weave:ManagementJournal:DatabasePath` selects another on-disk file.
`RequireExistingStorage=true` refuses a missing file or operation
table. There is no memory fallback or automatic record eviction.

Each admission stores a unique operation ID, capability subject and token ID,
workspace, action, exact target, authorized grant names, request digest, and UTC
time. It stores no capability body, configuration, or secret. A caller may send
`X-Weave-Management-Id` as a 32-character UUID before dispatch. When
the header is omitted, the Host generates an ID and returns it in that response
header. A second submission of an admitted ID returns 409 and does not dispatch
again, even when the first result is unknown. Callers that need recovery after a
lost response should generate and retain the ID before sending.

Successful dispatch is confirmed in the same journal before the endpoint
reports success. A storage failure blocks new work or prevents a success claim.
If dispatch fails or confirmation cannot be persisted, the retained outcome is
`OutcomeUnknown`; this is not evidence that no external effect happened. The
Host does not replay such operations. A caller with an exact
`management:operations:read` capability for the recorded workspace can inspect
`GET /api/management/operations/{workspaceId}/{id}`. Workspace creation is
recorded under the `silo` management scope, with the created workspace ID as
its target.

This is a single-Host, preserved-file boundary for these four HTTP writes. It
does not coordinate multiple Hosts, capture a human identity beyond the
validated capability subject, or cover every older administrative route. The
existing capability audit stream remains a diagnostic trace, and the invocation
journal still owns tool attempts and approvals. A later migration must extend
the same admission rule to remaining administrative writes before calling the
entire management surface durably governed.
