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
    assert.match(workflow,/node typescript\/mailbox-worker\/scripts\/require-ci-success\.ts worker-browser\.yml/);
    assert.match(workflow,/node typescript\/mailbox-worker\/scripts\/require-ci-success\.ts oauth-auth-worker\.yml/);
    assert.match(workflow,/verify:[\s\S]*?permissions:\s*\n\s*contents: read\s*\n\s*actions: read/);
    assert.doesNotMatch(workflow,/npm test/);
    assert.match(readFileSync(new URL('../../../.github/workflows/worker-browser.yml',import.meta.url),'utf8'),/push:\s*\n\s*branches: \[main\][\s\S]*npm test/,
        'the gated workflow runs the Worker suite on main pushes');
});
test('generic demo releases cannot select a D1 schema mutation; only the separately pinned migration can',()=>{
    const workflow=readFileSync(path,'utf8');
    assert.doesNotMatch(workflow,/^\s+database_mode:\s*$/m,
        'Normal manual releases must not expose maintain mode while historic rows are unverified.');
    assert.equal((workflow.match(/WEAVE_RELEASE_DATABASE_MODE: completed-only/g)||[]).length,2,
        'Both early and protected production release gates must use completed-only.');
    assert.equal((workflow.match(/WEAVE_DEPLOY_DATABASE_MODE: completed-only/g)||[]).length,2,
        'Both actual deploy jobs must refuse schema changes.');
    assert.doesNotMatch(workflow,/WEAVE_(?:RELEASE|DEPLOY)_DATABASE_MODE:[^\n]*(?:maintain|inputs\.database_mode)/,
        'None of the generic demo release paths can select maintenance migrations.');
    assert.equal((workflow.match(/node scripts\/verify-public-mcp\.ts/g)||[]).length,2,
        'Both deployments must still verify the public MCP endpoint.');
    const reviewed=readFileSync(new URL('../../../.github/workflows/approved-human-schema-migration.yml',import.meta.url),'utf8');
    assert.match(reviewed,/confirm_approved_source/);
    assert.match(reviewed,/WEAVE_DEPLOY_DATABASE_MODE: maintain/,
        'The exact-source-pinned protected migration entry must remain available.');
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
    const protectedDeploy=workflow.split(/^  deploy-production:/m)[1];
    assert.ok(protectedDeploy,'The protected production release job must exist.');
    assert.match(protectedDeploy,/WEAVE_DEPLOY_DATABASE_MODE: completed-only/,
        'The owner-triggered protected release must never select a schema migration mode.');
    assert.match(workflow,/WEAVE_APPROVED_FIRST_PARTY_OAUTH_BINDING: \$\{\{ github\.event_name == 'issue_comment' && 'false'/);
    assert.match(workflow,/  contents: read/);
    assert.doesNotMatch(workflow,/contents: write/);
    assert.doesNotMatch(workflow,/CLOUDFLARE_ACCESS_TRIAL_TOKEN/);
});

test('unrelated issue comments cannot replace a pending protected D1 audit during release',()=>{
    // GitHub concurrency keeps one pending group member by default. The
    // audit issue command also invokes an unrelated deploy workflow whose
    // release job skips; it must not evict the queued protected read.
    const workflow=readFileSync(path,'utf8');
    const top=workflow.split(/^jobs:/m)[0];
    assert.match(top,/concurrency:\s*\n\s*group:/);
    for(const requirement of [
        /github\.event_name == 'issue_comment'/,
        /github\.event\.issue\.number != 236/,
        /github\.event\.issue\.pull_request != null/,
        /github\.event\.comment\.body != '\/weave-deploy-demo'/,
        /github\.event\.comment\.user\.login != 'ldsenow'/,
        /github\.event\.comment\.author_association != 'MEMBER'/,
        /github\.actor != 'ldsenow'/,
        /github\.run_id/,
        /'deploy-weave-worker-demo'/,
        /cancel-in-progress: false/,
    ]) assert.match(top,requirement);
});
test('the production release updates the authorization Worker code after the mailbox it calls, and only there',()=>{
    const workflow=readFileSync(path,'utf8');
    assert.equal((workflow.match(/node scripts\/update-demo\.ts\n/g)||[]).length,1,'one publication; the preflight only reads');
    const production=workflow.slice(workflow.indexOf('  deploy-production:'));
    const mailbox=production.indexOf('node scripts/deploy.ts'),auth=production.indexOf('node scripts/update-demo.ts\n');
    assert.ok(mailbox>0&&auth>mailbox,'the mailbox deploys first');
    assert.doesNotMatch(production.slice(auth),/GITHUB_CLIENT_SECRET|HUMAN_SESSION_KEY|WEAVE_TRIAL_GITHUB_SUBJECTS/,'a code update receives no authentication secrets');
});

test('protected production preflights the live authorization Worker before mutating the mailbox',()=>{
    const workflow=readFileSync(path,'utf8');
    const production=workflow.split(/^  deploy-production:/m)[1];
    assert.ok(production,'the protected deployment job must exist');
    const preflight=production.indexOf('node scripts/update-demo.ts --preflight');
    const mailbox=production.indexOf('node scripts/deploy.ts');
    assert.ok(preflight>=0 && mailbox>preflight,
        'read-only OAuth settings/secret/binding preflight must run before any mailbox deploy or migration');
    const authInstall=production.indexOf('working-directory: typescript/auth-worker');
    assert.ok(authInstall>=0 && authInstall<preflight,'auth dependencies must be installed before preflight');
    assert.match(production.slice(preflight,mailbox),/CLOUDFLARE_API_TOKEN:/);
    assert.match(production.slice(preflight,mailbox),/CLOUDFLARE_ACCOUNT_ID:/);
});
