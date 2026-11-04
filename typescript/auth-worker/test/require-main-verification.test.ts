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
        if(!String(input).includes('/oauth-auth-worker.yml/'))assert.match(String(input),/event=push/);
        else assert.doesNotMatch(String(input),/event=/);
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
            (async()=>Response.json({workflow_runs:[data]})) as typeof fetch),/no successful exact-SHA main verification/);
    }
});

test('HTTP permission failures and malformed GitHub replies abort the entire release',async()=>{
    await assert.rejects(()=>requireMainVerification(repo,sha,token,
        (async()=>new Response('{}',{status:403})) as typeof fetch),/HTTP 403/);
    await assert.rejects(()=>requireMainVerification(repo,sha,token,
        (async()=>Response.json({workflow_runs:null})) as typeof fetch),/malformed run history/);
});

test('accepts exact-main workflow_dispatch only for path-filtered OAuth verification',async()=>{
    const seen:string[]=[];
    const evidence=await requireMainVerification(repo,sha,token,async(input,init)=>{
        const url=String(input);seen.push(url);
        if(url.includes('/oauth-auth-worker.yml/')){
            assert.doesNotMatch(url,/event=push/);
            return Response.json({workflow_runs:[{head_sha:sha,head_branch:'main',event:'workflow_dispatch',
                status:'completed',conclusion:'success',html_url:'https://github.com/KoalaFacts/Weave/actions/runs/456'}]});
        }
        assert.match(url,/event=push/);
        return Response.json({workflow_runs:[{head_sha:sha,head_branch:'main',event:'push',status:'completed',conclusion:'success'}]});
    });
    assert.equal(seen.length,4);
    assert.match(evidence['oauth-auth-worker.yml'],/456/);
});

test('never accepts manually dispatched CI, Worker or Security results instead of main push',async()=>{
    await assert.rejects(()=>requireMainVerification(repo,sha,token,
        (async()=>Response.json({workflow_runs:[{
            head_sha:sha,head_branch:'main',event:'workflow_dispatch',status:'completed',conclusion:'success'
        }]})) as typeof fetch),/no successful exact-SHA main verification/);
});
