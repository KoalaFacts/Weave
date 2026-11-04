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
    // Release relies on the passed Worker verification run of this exact commit on main, not a repeated suite.
    assert.match(workflow,/node typescript\/mailbox-worker\/scripts\/require-ci-success\.ts/);
    assert.match(workflow,/verify:[\s\S]*?permissions:\s*\n\s*contents: read\s*\n\s*actions: read/);
    assert.doesNotMatch(workflow,/npm test/);
    assert.match(readFileSync(new URL('../../../.github/workflows/worker-browser.yml',import.meta.url),'utf8'),/push:\s*\n\s*branches: \[main\][\s\S]*npm test/,
        'the gated workflow runs the Worker suite on main pushes');
});
test('manual deployment retains maintain choice while publish branch requires completed-only',()=>{
    const workflow=readFileSync(path,'utf8');
    assert.match(workflow,/database_mode:\s*\n\s*description:[^\n]+\n\s*required: true\n\s*type: choice\n\s*default: maintain\n\s*options: \[maintain, completed-only\]/);
    assert.match(workflow,/WEAVE_DEPLOY_DATABASE_MODE: \$\{\{ github\.event_name == 'push' && 'completed-only' \|\| inputs\.database_mode \}\}/);
    assert.match(workflow,/WEAVE_DEPLOY_DATABASE_MODE: \$\{\{ github\.event_name == 'issue_comment' && 'completed-only' \|\| inputs\.database_mode \}\}/);
    assert.doesNotMatch(workflow,/inputs\.database_mode \|\| 'maintain'/,'A manually supplied empty mode must reach the rejecting script unchanged.');
    assert.doesNotMatch(workflow,/github\.event_name == 'push' && 'maintain'/,'Automatic publish must never perform an unapproved migration.');
    assert.equal((workflow.match(/node scripts\/verify-public-mcp\.ts/g)||[]).length,2,'Both deployment jobs must probe real public MCP.');
});
test('the retired Access trial input is absent from the deployment workflow',()=>{
    const workflow=readFileSync(path,'utf8');
    assert.doesNotMatch(workflow,/ACCESS_TRIAL|access_trial/);
});

test('issue comment release requires owner identity, #236, exact command and protected production',()=>{
    const workflow=readFileSync(path,'utf8');
    assert.match(workflow,/issue_comment:\s*\n\s*types: \[created\]/);
    assert.match(workflow,/github\.event\.issue\.number == 236/);
    assert.match(workflow,/github\.event\.issue\.pull_request == null/);
    assert.match(workflow,/github\.event\.comment\.body == '\/weave-deploy-demo'/);
    assert.match(workflow,/github\.event\.comment\.user\.login == 'ldsenow'/);
    assert.match(workflow,/github\.event\.comment\.author_association == 'MEMBER'/);
    assert.match(workflow,/github\.actor == 'ldsenow'/);
    assert.match(workflow,/test "\$GITHUB_ACTOR" = ldsenow/);
    assert.match(workflow,/test "\$WEAVE_EVENT_BODY" = \/weave-deploy-demo/);
    assert.match(workflow,/test "\$WEAVE_EVENT_IS_PR" = false/);
    assert.equal((workflow.match(/test "\$\(git rev-parse origin\/main\)" = "\$GITHUB_SHA"/g)||[]).length,2,'Before verify and after protected environment approval');
    assert.match(workflow,/    environment: production/);
    assert.match(workflow,/WEAVE_RELEASE_CONFIGURATION_SCOPE: \$\{\{ github\.event_name == 'issue_comment' && 'production' \|\| inputs\.configuration_scope \}\}/);
    assert.match(workflow,/WEAVE_RELEASE_FIRST_PARTY_OAUTH: \$\{\{ github\.event_name == 'issue_comment' && 'false' \|\| inputs\.enable_first_party_oauth \}\}/);
    assert.match(workflow,/WEAVE_DEPLOY_DATABASE_MODE: \$\{\{ github\.event_name == 'issue_comment' && 'completed-only' \|\| inputs\.database_mode \}\}/);
    assert.match(workflow,/WEAVE_APPROVED_FIRST_PARTY_OAUTH_BINDING: \$\{\{ github\.event_name == 'issue_comment' && 'false'/);
    assert.match(workflow,/  contents: read/);
    assert.doesNotMatch(workflow,/contents: write/);
    assert.doesNotMatch(workflow,/CLOUDFLARE_ACCESS_TRIAL_TOKEN/);
});
