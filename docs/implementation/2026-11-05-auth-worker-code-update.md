# One release for both demo Workers

## Problem

The demo runs two Workers:

- `demo`, the mailbox Worker;
- `weave-auth-demo`, which serves GitHub sign-in, the OAuth issuer and the owner pages.

**Deploy Weave Worker** published only `demo`. The only workflow that publishes `weave-auth-demo` is the
[protected issuer release](2026-11-05-protected-first-party-issuer-rollout.md). That workflow is a one-time first
publication: it uploads the authentication secrets and refuses when the Worker already exists. So nothing could
update the running authorization Worker's code.

A release that changes a contract between the two Workers left demo half updated. The
[publishing router](2026-11-05-publishing-router.md) replaced `connectPostiz` and `connectBluesky` with
`connectAccount`, and after that release the running owner page still called the removed RPCs.

## Change

The production job of **Deploy Weave Worker** now deploys the mailbox Worker first, then runs
`typescript/auth-worker/scripts/update-demo.ts`. The script:

1. reads the running `weave-auth-demo` settings with one GET;
2. refuses unless the Worker exists with every expected binding and secret:
   - the fixed issuer and resource;
   - the GitHub client ID;
   - the KV namespace;
   - the `ConsentNonce` Durable Object;
   - the `MAILBOX` → `demo#HumanDirectory` service;
   - the three authentication secrets, checked by metadata only. In particular, it refuses if the trial allowlist
     is missing, because the current code fails closed without it.
3. rebuilds the configuration from those bindings (the same `demoManifest` the first publication uses);
4. publishes only the code;
5. reads the settings back and checks OAuth discovery.

The update never receives, uploads or rotates secrets. KV data, consent nonces and the Durable Object migration
are left as they are.

The release also waits for **OAuth verification** to pass on main for the exact commit, because that workflow
runs the authorization Worker's suite. OAuth verification skips commits outside its paths, so for such a commit
it must first be run manually on main. `require-ci-success.ts` now takes the workflow to require and accepts a
manual main run only for OAuth verification.

The first publication and its secrets remain the protected issuer workflow, unchanged.

## Verification

- auth-worker `test/update-demo.test.ts`:
  - the configuration is rebuilt from the running Worker's own bindings, using GET only;
  - an update is refused, without any change, when:
    - the Worker is not yet published;
    - the settings lookup is denied;
    - any expected binding or secret is missing;
    - the service binding or issuer is unexpected;
    - the account or token is missing.
- mailbox-worker:
  - `test/require-ci-success.test.ts` covers both gates. Worker verification accepts only main push runs; OAuth
    verification also accepts a manual main run, but never a PR run.
  - `test/deploy-entry.test.ts` checks that production updates the authorization Worker after the mailbox and
    passes it no authentication secrets.

## Limits

- The two Workers are published one after the other, not atomically. If the mailbox succeeds and the update
  fails, the release fails and demo is half updated until the next release.
- The `repository` scope still publishes only the mailbox Worker.
