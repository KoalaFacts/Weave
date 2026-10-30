import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';

const source=readFileSync(new URL('../../.github/workflows/provision-weave-access.yml',import.meta.url),'utf8');

test('completed Access trial cannot be reprovisioned by a manual workflow dispatch',()=>{
  assert.match(source,/\n  workflow_dispatch:\n/);
  assert.match(source,/\n  verify:\n/);
  assert.match(source,/permissions:\n  contents: read\n/);
  assert.match(source,/node --test experiments\/cloudflare-managed-oauth\/\*\.test\.ts/);
  assert.doesNotMatch(source,/\n  provision:\n/,'the privileged provisioning job must stay retired');
  assert.doesNotMatch(source,/\n\s+create_trial:/,'there must be no create-request dispatch input');
  assert.doesNotMatch(source,/secrets\./,'the retired workflow must not expose any secret');
  assert.doesNotMatch(source,/WEAVE_ACCESS_TRIAL_COMMIT/,'no one-time write bypass is allowed');
});
