import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync, existsSync } from 'node:fs';
const path=new URL('../../../.github/workflows/deploy-weave-worker.yml',import.meta.url);
test('explicit publish branch keeps main ancestry and existing verification before deployment',()=>{
    assert.ok(existsSync(path),'Deployment workflow is missing');
    const workflow=readFileSync(path,'utf8');
    assert.match(workflow,/branches: \[publish\/demo\]/);
    assert.match(workflow,/git merge-base --is-ancestor "\$GITHUB_SHA" origin\/main/);
    assert.match(workflow,/test "\$\(git rev-parse HEAD\)" = "\$GITHUB_SHA"/);
    assert.equal((workflow.match(/needs: verify/g)||[]).length,2);
    assert.equal((workflow.match(/node scripts\/verify-agent-entry\.ts/g)||[]).length,2);
    assert.match(workflow,/github\.event_name == 'push' && github\.ref == 'refs\/heads\/publish\/demo'/);
    assert.match(workflow,/environment: production/);
    assert.match(workflow,/npm test/);assert.match(workflow,/npm run test:compat/);
});
