import {resolve} from 'node:path';
import {pathToFileURL} from 'node:url';

// Deployment gate: only successful runs triggered by an actual main push at
// precisely this source commit count. PR runs, skipped jobs and stale commits
// are not substitutes for release evidence.
export const mandatoryWorkflows=['ci.yml','worker-browser.yml','scan-security.yml','oauth-auth-worker.yml'] as const;

export async function requireMainVerification(
    repo:string,sha:string,token:string,request:typeof fetch=fetch
):Promise<Record<string,string>>{
    if(!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repo)||
        !/^[0-9a-f]{40}$/.test(sha)||!token)
        throw Error('Exact repository, SHA and GitHub Actions read token are required.');
    const evidence:Record<string,string>={};
    for(const workflow of mandatoryWorkflows){
        // OAuth is path-filtered on push. A successful manual run at this exact
        // main SHA is an explicit equivalent; other release gates remain push-only.
        const query=new URLSearchParams({branch:'main',head_sha:sha,per_page:'20'});
        if(workflow!=='oauth-auth-worker.yml')query.set('event','push');
        const url='https://api.github.com/repos/'+repo+'/actions/workflows/'+workflow+'/runs?'+query;
        let response:Response;
        try{response=await request(url,{method:'GET',redirect:'error',
            headers:{Authorization:'Bearer '+token,Accept:'application/vnd.github+json'},
            signal:AbortSignal.timeout(10000)});}catch{
            throw Error('Main '+workflow+' lookup unavailable; issuer deployment not permitted.');
        }
        if(!response.ok)throw Error('Main '+workflow+' lookup returned HTTP '+response.status+'; issuer deployment not permitted.');
        const json=await response.json().catch(()=>null) as {workflow_runs?:{
            head_sha?:unknown;head_branch?:unknown;event?:unknown;status?:unknown;
            conclusion?:unknown;html_url?:unknown}[]} | null;
        if(!json||!Array.isArray(json.workflow_runs))
            throw Error('Main '+workflow+' returned malformed run history.');
        const matching=json.workflow_runs.filter(r=>r.head_sha===sha&&r.head_branch==='main'&&
            (r.event==='push'||(workflow==='oauth-auth-worker.yml'&&r.event==='workflow_dispatch')));
        const passed=matching.find(r=>r.status==='completed'&&r.conclusion==='success');
        if(!passed)throw Error('Main '+workflow+' has no successful exact-SHA main verification; no issuer deployment.');
        evidence[workflow]=typeof passed.html_url==='string'?passed.html_url:'';
    }
    return evidence;
}

if(process.argv[1]&&import.meta.url===pathToFileURL(resolve(process.argv[1])).href){
    await requireMainVerification(process.env.GITHUB_REPOSITORY??'',process.env.GITHUB_SHA??'',
        process.env.GITHUB_TOKEN??'');
    console.log('Four required exact-main-push verification workflows passed; no cloud change.');
}
