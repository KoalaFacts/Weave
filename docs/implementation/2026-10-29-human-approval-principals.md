# Human-only approval decisions

## Scope

This increment makes "only a person can approve" a property of the signed
capability token rather than of a grant. It changes the existing Governed Tools
approval path in place; it adds no identity provider, login flow, review UI or
notification channel, and it does not change the mailbox relay or Worker hosts.

## What changed

- `CapabilityToken` and `CapabilityTokenRequest` carry a `Kind`
  (`PrincipalKind.Agent`, the default, or `PrincipalKind.Human`). The kind is part
  of the signed payload, which moves from `Weave.CapabilityToken/v2` to `/v3`.
  A holder cannot relabel an Agent token as Human without invalidating it.
- `principal:human` is a reserved requirement checked by `CapabilityAuthorizer`
  against the token's kind. It is not a grant: minting rejects any grant with the
  `principal:` prefix, so neither `*` nor any explicit list can satisfy it. A deny
  is audited with reason `human-principal-required`.
- The `approval:decide` grant is removed. Reading another subject's approval,
  reviewing a proposal, and approving or rejecting now require a Human token plus
  the exact `tool:<name>:approve:<operation>` grant, in the actor's workspace.
  Cancelling one's own request still uses `invocation:cancel`.
- Operation approval grants (`tool:<name>:approve:<operation>`) can only be minted
  for Human principals. This is defence in depth; the Human check above is the
  decisive one, and it also stops an Agent holding a wildcard grant.
- The original requestor still cannot approve its own request, even as a Human.

## Operator configuration migration

Credential profiles gain an optional `Kind` (default `Agent`). A reviewer profile
must now be declared as Human and must drop `approval:decide`:

```json
"reviewer": {
  "WorkspaceId": "first-use",
  "IssuedTo": "reviewer",
  "Kind": "Human",
  "Lifetime": "00:05:00",
  "Grants": ["invocation:read", "tool:files:approve:write_file"]
}
```

Startup fails closed, without exposing the operator key, when a profile still lists
`approval:decide`, grants a reserved `principal:` value, gives an approval grant to
a non-Human profile, or uses an undefined `Kind`. `weave local` and the governed-tools
examples generate the new reviewer profile.

## Compatibility

- Tokens signed in the v2 format are rejected after upgrade, so callers must obtain
  fresh tokens. Operator-issued tokens live at most one hour; any deployment that
  stores longer-lived tokens must reissue them. Invocation and approval journal rows
  are unchanged.
- Callers that minted reviewer tokens in code must set `Kind = PrincipalKind.Human`
  and stop granting `approval:decide`.

## Limits

- "Human" means "issued from a profile the operator declared Human". Weave still
  does not authenticate people; deployments must ensure only people can obtain
  those operator credentials (for example behind an SSO-protected operator surface).
- Two-person approval for an operation is separate follow-up work.

## Verification

Security tests cover default kind, approval grants refused for Agents, reserved
grants refused for both kinds, relabelled tokens failing signature validation, and
the Human requirement denying an Agent `*` token with the audited reason. Silo tests
cover decisions denied for a Human without the operation grant and for Agents with
`*` or `tool:*`, a Human with only the operation grant approving, and operator
startup rejecting the removed grant, reserved grants and non-Human approval profiles.
The existing approval suites were updated to issue Human reviewer tokens.
