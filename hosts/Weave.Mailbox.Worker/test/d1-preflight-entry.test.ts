import assert from 'node:assert/strict';
import test from 'node:test';
import { existsSync,readFileSync } from 'node:fs';

const path=new URL('../../../.github/workflows/inspect-weave-worker-d1.yml',import.meta.url);
test('D1 preflight is manual, main-only and shares the existing release queue without changing its workflow',()=>{
    assert.ok(existsSync(path),'Independent CI read-only entry is missing');
    const workflow=readFileSync(path,'utf8');
    assert.match(workflow,/on:\n  workflow_dispatch:\n/);
    assert.doesNotMatch(workflow,/\b(?:push|pull_request|pull_request_target|workflow_call|schedule|inputs|environment):/);
    assert.equal((workflow.match(/if: github\.ref == 'refs\/heads\/main'/g)||[]).length,2);
    assert.match(workflow,/permissions:\n  contents: read\n/);
    assert.match(workflow,/group: deploy-weave-worker-demo\n  cancel-in-progress: false/);
    assert.match(workflow,/test "\$\(git rev-parse HEAD\)" = "\$GITHUB_SHA"/);
    assert.match(workflow,/git merge-base --is-ancestor "\$GITHUB_SHA" origin\/main/);
    assert.equal((workflow.match(/persist-credentials: false/g)||[]).length,2);
    assert.equal((workflow.match(/ref: \$\{\{ github\.sha \}\}/g)||[]).length,2);
    const inspect=workflow.split('  inspect:\n')[1];assert.ok(inspect);
    assert.match(inspect,/needs: verify/);
    assert.equal((workflow.match(/secrets\./g)||[]).length,4);
    assert.match(inspect,/run: node scripts\/inspect-live-oauth\.ts/);
    assert.match(inspect,/CLOUDFLARE_API_TOKEN: \$\{\{ secrets\.CLOUDFLARE_API_TOKEN \}\}/);
    assert.match(inspect,/CLOUDFLARE_ACCOUNT_ID: \$\{\{ secrets\.CLOUDFLARE_ACCOUNT_ID \}\}/);
    assert.match(inspect,/run: node scripts\/d1-preflight\.ts/);
    assert.doesNotMatch(inspect,/npm |wrangler|deploy\.ts|normalize|schema-plan|latest-schema|upload-artifact|curl /);
    assert.match(workflow,/node --test test\/d1-preflight\.test\.ts test\/d1-preflight-entry\.test\.ts/);
    assert.doesNotMatch(workflow.split('  inspect:\n')[0],/secrets\./);
});
