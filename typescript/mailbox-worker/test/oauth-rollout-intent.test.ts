import assert from 'node:assert/strict';
import {test} from 'node:test';
import {validateFirstPartyOAuthRollout,validateHumanSchemaRollout,type ReleaseIntent,type HumanSchemaIntent} from '../scripts/oauth-rollout-intent.ts';

const approved: ReleaseIntent = {
    eventName:'workflow_dispatch', ref:'refs/heads/main',
    configurationScope:'production', databaseMode:'completed-only', enabled:'true'
};

test('explicit main-only protected production rollout is permitted after separate approval',()=>{
    assert.equal(validateFirstPartyOAuthRollout(approved),true);
});
test('normal main and publish/demo releases leave first-party OAuth bootstrap off',()=>{
    for(const fields of [
        {...approved,enabled:'false'},
        {...approved,enabled:''},
        {...approved,eventName:'push',ref:'refs/heads/publish/demo',enabled:''}
    ])assert.equal(validateFirstPartyOAuthRollout(fields),false);
});
test('OAuth activation cannot be routed through repository secrets, maintain mode or a non-main release',()=>{
    for(const fields of [
        {...approved,configurationScope:'repository'},
        {...approved,databaseMode:'maintain'},
        {...approved,ref:'refs/heads/publish/demo'},
        {...approved,ref:'refs/heads/feature'},
        {...approved,eventName:'push'},
        {...approved,eventName:'pull_request'}
    ])assert.throws(()=>validateFirstPartyOAuthRollout(fields),/explicit|protected|approval|completed-only/i);
});
test('ambiguous rollout selector must fail closed',()=>{
    for(const enabled of ['TRUE','1','yes',' false',' true','maybe'])
        assert.throws(()=>validateFirstPartyOAuthRollout({...approved,enabled}),/invalid|selector|approval/i);
});

const humanApproved: HumanSchemaIntent = {
    eventName:'workflow_dispatch', ref:'refs/heads/main',
    configurationScope:'production', databaseMode:'maintain',
    enabled:'true',firstPartyOAuthEnabled:'false',accessTrialApplication:''
};
test('human D1 compatibility migration can be explicitly requested only on protected main production maintain',()=>{
    assert.equal(validateHumanSchemaRollout(humanApproved),true);
    for(const fields of [
        {...humanApproved,enabled:'false'},
        {...humanApproved,enabled:''},
        {...humanApproved,enabled:'false',eventName:'push',ref:'refs/heads/publish/demo'}
    ])assert.equal(validateHumanSchemaRollout(fields),false);
});
test('human D1 migration rejects automatic, repository, completed-only or simultaneous OAuth/Access activation',()=>{
    for(const fields of [
        {...humanApproved,configurationScope:'repository'},
        {...humanApproved,databaseMode:'completed-only'},
        {...humanApproved,eventName:'push'},
        {...humanApproved,eventName:'pull_request'},
        {...humanApproved,ref:'refs/heads/publish/demo'},
        {...humanApproved,firstPartyOAuthEnabled:'true'},
        {...humanApproved,accessTrialApplication:'not-empty'}
    ])assert.throws(()=>validateHumanSchemaRollout(fields),/explicit protected main production maintain/i);
    for(const enabled of ['TRUE','yes','1',' true'])
        assert.throws(()=>validateHumanSchemaRollout({...humanApproved,enabled}),/Invalid human onboarding migration selector/);
});
