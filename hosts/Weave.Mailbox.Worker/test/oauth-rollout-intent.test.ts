import assert from 'node:assert/strict';
import {test} from 'node:test';
import {validateFirstPartyOAuthRollout,type ReleaseIntent} from '../scripts/oauth-rollout-intent.ts';

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
