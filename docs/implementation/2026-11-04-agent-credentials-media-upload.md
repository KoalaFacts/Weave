# Agent credential listing, MCP media upload and URL import

## Scope

Closes two gaps for agents using [governed external actions](2026-11-04-worker-external-actions.md)
and [Postiz publishing](2026-11-04-postiz-publishing.md). Before this change an agent
could not discover which credentials its owner had connected or what content each
takes, and an agent connected through MCP OAuth could not upload an image or video:
`PUT /v1/external-media` needs an agent key, and an OAuth-connected agent has none.
Nothing here changes approval, dispatch or the plan digest. Nothing was deployed.

## Credential listing

`GET /v1/external-credentials` (agent key) and the MCP tool `list_my_credentials`
(scope `weave:actions.read`) return the agent's live credentials:
`credentialRef`, `provider`, `targetAccount`, `targetDisplay`, `operations` and
`content`. `content` is the provider's description of the content its operations
take for that account; for a Postiz channel it names the channel's `__type` and every
setting Postiz requires for that channel type. Revoked credentials are not listed and
secrets are never returned. A credential whose provider this deployment cannot use
(for example YouTube without Google configuration) lists no operations and a null
`content`. Each provider now implements `describeContent(targetAccount)`.

## MCP media upload

- `create_media_upload {contentType, bytes}` (scope `weave:actions.write`) checks
  the consent grant and returns `{mediaId, uploadUrl, contentType, bytes, expiresAt}`.
  It stores nothing.
- `uploadUrl` is `PUT /v1/external-media/{mediaId}/content?account&grant&type&bytes&expires&signature`.
  The HMAC (from `EXTERNAL_CREDENTIAL_KEY`, label `weave-media-upload-v1`, separate
  from the fetch-URL label) binds the agent, the consent grant, the media ID, the
  type, the exact size and an expiry 15 minutes ahead.
  - A missing, tampered or expired signature, or an extra query parameter, is 404.
  - Content-Type and Content-Length must equal the signed values (400).
  - The grant is re-checked before any byte is stored and again when the record is
    written (401 after a revocation).
- The response is the same media record as the direct upload. `get_my_media {mediaId}`
  (scope `weave:actions.read`) and `GET /v1/external-media/{mediaId}` (agent key)
  read it again.
- The media ID is fixed by the signature and R2 writes only when the key is empty, so
  a URL stores one file. A second PUT is 409 and leaves the first file untouched.
  This also fixes the shared storage path: a refused conditional write no longer
  deletes the existing object; only a size mismatch on a fresh write removes what
  was just written.

## Import from a public URL

For an agent that holds a link but cannot send a file (a chat-only client, or a
generated video at a public URL), `import_media_from_url {url}` (scope
`weave:actions.write`, open-world) and `POST /v1/external-media/imports {url}`
(agent key) make Weave fetch the file and store it like an upload.

- The URL must be https on the default port with a DNS host name. IP literals (every
  IPv4 spelling is normalized by URL parsing, IPv6 is bracketed), single-label hosts,
  `.localhost`, `.local`, `.internal`, `.home.arpa`, credentials and the Worker's own
  media host are refused before any request. One trailing DNS dot is removed first,
  so `localhost.` or `printer.local.` is refused as well.
- The caller's authority is checked before the request is made and again by the
  shared storage path.
- One GET with `redirect: manual` and `Accept-Encoding: identity`, 5-minute timeout.
  Only 200 with a supported Content-Type and a Content-Length of at most 100 MiB is
  accepted. The body is counted as it arrives and piped through `FixedLengthStream`,
  so R2 receives a stream of known length.
- Every refusal is 400 with field `url`, including a source body that breaks off or
  is not its stated length. Authority refusals and storage or database errors keep
  their own status (for example 503), so an outage is not blamed on the URL.

Workers cannot resolve DNS before fetching, so a public name that resolves to a
private address is not detected here; Cloudflare's network is the boundary for that.

## Retention

The 5-minute cron deletes media that was uploaded at least 7 days ago and that no
action has named or changed in the last 7 days. "Named" means the action's content
contains the media ID. Only the uploading agent's own actions count. The sweep handles
at most 50 files per run.

- The D1 record is deleted first, re-checked in the same transaction. A plan proposed
  in the meantime therefore keeps its file, because the optimistic revision serializes
  the two.
- The R2 object is deleted next. If that deletion fails, it is logged and the object
  is left behind; Weave can no longer reach it, and nothing retries it.
- Nothing is deleted in maintenance or read-only recovery.
- A plan naming deleted media is refused at proposal. An approved plan whose file is
  gone fails cleanly at dispatch: the media upload is refused, so nothing is posted.


- `test/agent-credentials-media.test.ts`:
  - Listing shows live credentials of this agent only, with Postiz operations and
    required settings, and an unavailable provider with no operations. The HTTP API
    and MCP return the same listing.
  - Mint, PUT and propose pin the uploaded file. A second PUT is 409 and the stored
    file is unchanged.
  - Tampered size, type, agent, grant or media ID, an expired URL, an extra parameter,
    a wrong Content-Type or Content-Length, and a grant revoked after minting store
    nothing.
  - Invalid tool arguments are refused.
- Mutation checks: restoring delete-on-refused-write fails the single-use test;
  skipping the grant re-check at PUT fails the revocation test; dropping the
  parameter-count check fails the extra-parameter case.
- `test/media-import.test.ts`, through the Worker with a fake web as its outbound
  service: the stored file is exactly what was fetched; unsafe or malformed URLs make
  no request; a redirect, 404, HTML, missing Content-Length or other status stores
  nothing. Through MCP: the request uses manual redirects, a revoked grant makes
  no request, a short body is a `url` refusal and a storage failure is 503. Mutation checks: dropping the IP-literal check, the authority check
  before fetching, manual redirects, trailing-dot removal or the source-failure
  distinction each fail a test.
- `test/media-retention.test.ts`, through the Worker's own cron and real R2: old,
  unused media loses its record and object while recent media is kept; media named by
  a recent action of its own agent is kept until that action is old, and another
  agent's action does not keep it; a failed object deletion still removes the record
  and the sweep continues; maintenance deletes nothing. Mutation checks: dropping the
  same-agent condition, the action condition, or the per-object error handling each
  fail a test.
- `test/mcp-tool-contract.test.ts` exercises the new tools against their output
  schemas. `test/openapi.test.ts` covers the new HTTP operations.
  `test/in-process-media.ts` lets tools called in the Node test process store media
  (Node has no `DigestStream`, and the R2 test proxy takes one buffer); Worker tests
  use real R2 streaming.

## Limits

- The PUT must start within 15 minutes; the body may take longer. One request body is
  at most 100 MiB.
- A file the agent holds only locally and cannot PUT, and that is not at a public
  URL, still cannot be uploaded.
