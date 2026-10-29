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
test('manual deployment retains maintain choice while publish branch requires completed-only',()=>{
    const workflow=readFileSync(path,'utf8');
    assert.match(workflow,/database_mode:\s*\n\s*description:[^\n]+\n\s*required: true\n\s*type: choice\n\s*default: maintain\n\s*options: \[maintain, completed-only\]/);
    assert.equal((workflow.match(/WEAVE_DEPLOY_DATABASE_MODE: \$\{\{ github\.event_name == 'push' && 'completed-only' \|\| inputs\.database_mode \}\}/g)||[]).length,2);
    assert.doesNotMatch(workflow,/inputs\.database_mode \|\| 'maintain'/,'A manually supplied empty mode must reach the rejecting script unchanged.');
    assert.doesNotMatch(workflow,/github\.event_name == 'push' && 'maintain'/,'Automatic publish must never perform an unapproved migration.');
    assert.equal((workflow.match(/node scripts\/verify-public-mcp\.ts/g)||[]).length,2,'Both deployment jobs must probe real public MCP.');
});
test('Access trial activation is passed only to the protected production deployment',()=>{
    const workflow=readFileSync(path,'utf8');
    assert.equal((workflow.match(/WEAVE_APPROVED_ACCESS_TRIAL_APPLICATION: \$\{\{ inputs\.access_trial_application \}\}/g)||[]).length,1);
    const production=workflow.slice(workflow.indexOf('  deploy-production:'));
    assert.match(production,/environment: production/);
    assert.match(production,/WEAVE_APPROVED_ACCESS_TRIAL_APPLICATION/);
    assert.doesNotMatch(workflow.slice(0,workflow.indexOf('  deploy-production:')),/WEAVE_APPROVED_ACCESS_TRIAL_APPLICATION/,
        'Repository-scoped and publish-branch releases must never activate the Access trial.');
});
