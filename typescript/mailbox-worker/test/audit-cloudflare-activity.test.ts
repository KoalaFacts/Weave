import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import { inspectCloudflareAudit } from '../scripts/audit-cloudflare-activity.ts';

const account = '0123456789abcdef0123456789abcdef';
const db = '3ebbd419-be70-423d-8723-61fe5838ed38';
const metadata = (input: string) => {
    if (input.endsWith('/workers/subdomain'))
        return Response.json({success:true,result:{subdomain:'weave-389'}});
    if (input.endsWith('/workers/scripts/demo/settings'))
        return Response.json({success:true,result:{bindings:[{name:'db',type:'d1',id:db}]}});
    if (input.endsWith('/d1/database/' + db))
        return Response.json({success:true,result:{uuid:db,name:'weave-demo-db'}});
    return null;
};
const entry = (uri: string, time = '2026-11-02T00:04:01Z', method = 'POST') => ({
    raw:{uri,method,status_code:200, user_agent:'secret-private-agent'},
    actor:{email:'private@example.com',ip_address:'10.0.0.1',token_name:'private-token'},
    resource:{id:db,product:'d1',request:{password:'secret-private-credential'}},
    action:{time,type:'update',result:'success'},
});
test('read-only audit paginates, targets only the existing demo and never returns actor or secret data',async()=>{
    const calls:string[]=[];
    const request:typeof fetch=async(input,init)=>{
        const url=String(input);
        calls.push(url);
        assert.equal(init?.method,'GET');
        assert.equal(init?.redirect,'error');
        assert.equal((init?.headers as Record<string,string>)?.Authorization,'Bearer private-cloudflare-token');
        if (metadata(url)) return metadata(url)!;
        const requested=new URL(url);
        assert.equal(requested.origin,'https://api.cloudflare.com');
        assert.equal(requested.pathname,'/client/v4/accounts/'+account+'/logs/audit');
        assert.equal(requested.searchParams.get('since'),'2026-10-29T12:00:00Z');
        assert.equal(requested.searchParams.get('before'),'2026-11-02T02:00:00Z');
        if (!requested.searchParams.has('cursor'))
            return Response.json({success:true,result:[
                entry('/accounts/'+account+'/d1/database/'+db+'/query'),
                {...entry('/accounts/'+account+'/workers/scripts/demo/versions'),'resource':{id:'demo',product:'workers'}},
                entry('/accounts/'+account+'/d1/database/aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee/query'),
                {...entry('/accounts/'+account+'/d1/database', '2026-11-02T00:04:02Z'),
                    resource:{id:db,product:'d1'}, action:{time:'2026-11-02T00:04:02Z',type:'create',result:'success'}},
                {...entry('/accounts/'+account+'/d1/database', '2026-11-02T00:04:03Z'),
                    resource:{id:'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee',product:'d1'}}
            ],result_info:{cursor:'private-cursor'}});
        assert.equal(requested.searchParams.get('cursor'),'private-cursor');
        return Response.json({success:true,result:[
            entry('/accounts/'+account+'/d1/database/'+db+'/time_travel/bookmark',
                '2026-10-29T13:50:00Z','GET'),
            entry('/accounts/'+account+'/d1/database/'+db+'/query',
                '2026-10-27T00:00:00Z','POST'),
            entry('/accounts/'+account+'/d1/database/'+db+'/query',
                '2026-11-02T02:00:00.000Z','POST'),
        ],result_info:{}});
    };
    const result=await inspectCloudflareAudit(account,'private-cloudflare-token',request);
    assert.equal(result.status,'complete');
    assert.equal(result.pages,2);
    assert.equal(result.totalScanned,8);
    assert.equal(result.matchedD1,3);
    assert.equal(result.matchedWorker,1);
    assert.equal(result.possibleD1Mutations,2);
    assert.equal(result.events.length,4);
    assert.equal(result.historicalRowsVerified,false);
    assert.equal(result.migrationPermitted,false);
    assert.equal(calls.length,5);
    const output=JSON.stringify(result);
    for(const secret of ['private-cloudflare-token','private@example.com','10.0.0.1',
        'private-credential','private-cursor',db,account,'secret-private-agent'])
        assert.equal(output.includes(secret),false);
});
test('HTTP 403 is a precise permission-denied receipt, not an empty clean audit',async()=>{
    const request:typeof fetch=async(input)=>{
        const url=String(input);
        return metadata(url) ?? new Response('private raw Cloudflare error',{status:403});
    };
    const result=await inspectCloudflareAudit(account,'secret',request);
    assert.equal(result.status,'permission-denied');
    assert.equal(result.httpStatus,403);
    assert.equal(result.pages,0);
    assert.equal(JSON.stringify(result).includes('private raw'),false);
});
test('invalid account and unexpected resource identity fail closed before audit requests',async()=>{
    let calls=0;
    const request:typeof fetch=async()=>{calls++; throw Error('secret from provider');};
    await assert.rejects(inspectCloudflareAudit('https://evil.example','secret',request));
    assert.equal(calls,0);
    const other:typeof fetch=async(input)=>{
        const url=String(input);
        if (url.endsWith('/workers/subdomain'))
            return Response.json({success:true,result:{subdomain:'another-project'}});
        throw Error('should not reach another resource');
    };
    const result=await inspectCloudflareAudit(account,'secret',other);
    assert.equal(result.status,'api-error');
    assert.equal(result.pages,0);
});
test('malformed response or repeated cursor is not recorded as complete',async()=>{
    const malformed:typeof fetch=async(input)=>{
        const url=String(input);
        return metadata(url) ?? Response.json({success:true,result:{not:'array'}});
    };
    assert.equal((await inspectCloudflareAudit(account,'secret',malformed)).status,'api-error');
    const loop:typeof fetch=async(input)=>{
        const url=String(input);
        return metadata(url) ?? Response.json({success:true,result:[],result_info:{cursor:'repeat'}});
    };
    const res=await inspectCloudflareAudit(account,'secret',loop);
    assert.equal(res.status,'incomplete');
    assert.equal(res.pages,2);
});

test('D1-only timeline survives a worker-heavy audit and identifies the narrow migration window',async()=>{
    const request:typeof fetch=async(input,init)=>{
        assert.equal(init?.method,'GET');
        const url=String(input);
        const initial=metadata(url);
        if (initial) return initial;
        const prefix='/accounts/'+account;
        const worker=Array.from({length:60}, (_,i) =>
            ({...entry(prefix+'/workers/scripts/demo/versions','2026-10-29T12:50:01Z'),
                action:{time:'2026-10-29T12:50:01Z',type:'view',result:'success'}}));
        return Response.json({success:true,result:[
            ...worker,
            entry(prefix+'/d1/database/'+db+'/query','2026-11-02T00:04:01.969Z','POST'),
            entry(prefix+'/d1/database/'+db+'/time_travel/restore','2026-11-02T00:05:00Z','POST'),
            entry(prefix+'/d1/database/'+db+'/time_travel/bookmark','2026-10-29T13:50:00Z','GET'),
            entry(prefix+'/d1/database/'+db+'/query','2026-11-02T00:16:00Z','POST'),
            entry(prefix+'/d1/database/aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee/query','2026-11-02T00:04:01Z','POST'),
        ],result_info:{}});
    };
    const result=await inspectCloudflareAudit(account,'private-cloudflare-token',request);
    assert.equal(result.status,'complete');
    assert.equal(result.events.length,40); // legacy first-40 mixed sample is not D1 evidence
    assert.equal(result.sampleTruncated,true);
    assert.equal(result.matchedD1,4);
    assert.deepEqual(result.d1Timeline.map(e=>e.endpoint),['query','restore','bookmark','query']);
    assert.deepEqual(result.focusWindow,{since:'2026-11-01T23:57:00Z',
        before:'2026-11-02T00:16:00Z',matchedD1:2,nonGetCalls:2,
        events:[
            {time:'2026-11-02T00:04:01.969Z',endpoint:'query',action:'update',method:'POST',outcome:'success'},
            {time:'2026-11-02T00:05:00.000Z',endpoint:'restore',action:'update',method:'POST',outcome:'success'},
        ],truncated:false});
    assert.equal(result.d1TimelineTruncated,false);
    assert.equal(result.historicalRowsVerified,false);
    assert.equal(result.migrationPermitted,false);
    const serialized=JSON.stringify(result);
    for (const secret of ['private-cloudflare-token',account,db,'private@example.com','10.0.0.1','private-credential'])
        assert.equal(serialized.includes(secret),false);
});
