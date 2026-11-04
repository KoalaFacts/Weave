import assert from 'node:assert/strict';
import {test} from 'node:test';
import {mandatoryWorkflows,requireMainVerification} from '../scripts/require-main-verification.ts';

const sha='a'.repeat(40),repo='KoalaFacts/Weave',token='masked-github-token';

test('accepts only successful exact-main-push verification across all release workflows',async()=>{
    let requests=0;
    const evidence=await requireMainVerification(repo,sha,token,async(input,init)=>{
        requests++;
        assert.equal(init?.method,'GET');
        assert.equal(init?.redirect,'error');
        assert.equal((init?.headers as Record<string,string>).Authorization,'Bearer '+token);
        assert.match(String(input),/event=push/);
        return Response.json({workflow_runs:[
            {head_sha:sha,head_branch:'main',event:'pull_request',status:'completed',conclusion:'success'},
            {head_sha:sha,head_branch:'main',event:'push',status:'completed',conclusion:'success',html_url:'https://github.com/KoalaFacts/Weave/actions/runs/123'}
        ]});
    });
    assert.equal(requests,mandatoryWorkflows.length);
    assert.equal(Object.keys(evidence).length,mandatoryWorkflows.length);
});

test('never treats a PR run, another commit, cancelled or pending run as accepted',async()=>{
    for(const data of [
        {head_sha:sha,head_branch:'main',event:'pull_request',status:'completed',conclusion:'success'},
        {head_sha:'b'.repeat(40),head_branch:'main',event:'push',status:'completed',conclusion:'success'},
        {head_sha:sha,head_branch:'main',event:'push',status:'completed',conclusion:'failure'},
        {head_sha:sha,head_branch:'main',event:'push',status:'in_progress',conclusion:null},
        {head_sha:sha,head_branch:'main',event:'push',status:'completed',conclusion:'cancelled'}
    ]){
        await assert.rejects(()=>requireMainVerification(repo,sha,token,
            (async()=>Response.json({workflow_runs:[data]})) as typeof fetch),/no successful exact-SHA push run/);
    }
});

test('HTTP permission failures and malformed GitHub replies abort the entire release',async()=>{
    await assert.rejects(()=>requireMainVerification(repo,sha,token,
        (async()=>new Response('{}',{status:403})) as typeof fetch),/HTTP 403/);
    await assert.rejects(()=>requireMainVerification(repo,sha,token,
        (async()=>Response.json({workflow_runs:null})) as typeof fetch),/malformed run history/);
});
