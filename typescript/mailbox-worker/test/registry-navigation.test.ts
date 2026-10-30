import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { createContext, runInContext } from 'node:vm';
const html=readFileSync(new URL('../src/registry.html',import.meta.url),'utf8');
const script=/<script nonce="WEAVE_NONCE">([\s\S]*?)<\/script>/.exec(html)[1];
const item=label=>({label,addressId:label,accountId:'public-owner',isPublic:true});
const response=(label,nextCursor=null)=>new Response(JSON.stringify({items:[item(label)],nextCursor}));
async function ui(fetcher=async path=>path.includes('afterCursor')?response('older'):response('newest','cursor/+?'),{waitForInitial=true}={}){
    const elements=new Map(),calls=[];
    const element=()=>({value:'',textContent:'',children:[],disabled:false,handlers:{},append(child){this.children.push(child);},replaceChildren(...children){this.children=children;},addEventListener(event,handler){this.handlers[event]=handler;}});
    const get=id=>{if(!elements.has(id))elements.set(id,element());return elements.get(id);};
    const ready=runInContext(script,createContext({document:{getElementById:get,createElement:element},fetch:(path,options)=>{calls.push({path,options});return fetcher(path,options);}}));
    const settle=async()=>{for(let i=0;i<100;i++){await new Promise(resolve=>setImmediate(resolve));if(get('status').textContent!=='Loading…')return;}throw Error('Registry did not settle');};
    if(waitForInitial)await ready;
    return {get,calls,ready,settle,async click(id){assert.equal(typeof get(id).handlers.click,'function',id+' click action is missing');await get(id).handlers.click();await settle();}};
}
test('registry immediately browses anonymously without a required search or account',async()=>{
    const app=await ui();assert.equal(app.calls.length,1);assert.equal(new URL(app.calls[0].path,'https://local').searchParams.get('q'),'');
    assert.equal(app.get('results').children[0].children[0].textContent,'newest');
    assert.equal(app.calls[0].options.credentials,'omit');assert.equal(app.calls[0].options.headers,undefined);
});
test('registry previous page refetches its cursor instead of caching public address bodies',async()=>{
    const app=await ui();assert.equal(app.get('previous').disabled,true);assert.equal(app.get('pageNumber').textContent,'Page 1');
    await app.click('next');assert.equal(app.get('pageNumber').textContent,'Page 2');assert.equal(app.get('previous').disabled,false);
    assert.match(app.calls[1].path,/afterCursor=cursor%2F%2B%3F/);
    await app.click('previous');assert.equal(app.calls.length,3);assert.equal(app.calls[2].path,app.calls[0].path);
    assert.equal(app.get('pageNumber').textContent,'Page 1');assert.equal(app.get('previous').disabled,true);
});
test('browse all clears a prior search and returns to the newest first page',async()=>{
    const app=await ui();app.get('search').value='Chosen label';await app.click('find');await app.click('next');
    await app.click('browse');assert.equal(app.get('search').value,'');assert.equal(app.get('pageNumber').textContent,'Page 1');
    assert.equal(app.calls.at(-1).path,'/v1/directory?limit=10&q=');assert.equal(app.get('previous').disabled,true);
});
test('editing a search input does not mix another query into next page and Search resets navigation',async()=>{
    const app=await ui();app.get('search').value='not submitted';await app.click('next');
    assert.equal(new URL(app.calls.at(-1).path,'https://local').searchParams.get('q'),'');
    await app.click('find');assert.equal(app.get('pageNumber').textContent,'Page 1');assert.equal(app.get('previous').disabled,true);
    assert.equal(new URL(app.calls.at(-1).path,'https://local').searchParams.get('afterCursor'),null);
});
test('empty exact lookup keeps current browse results and makes no invalid HTTP request',async()=>{
    const app=await ui();app.get('address').value='  ';await app.click('lookup');
    assert.equal(app.calls.length,1);assert.equal(app.get('results').children[0].children[0].textContent,'newest');
    assert.match(app.get('status').textContent,/enter.*address/i);
});
test('older pending page cannot overwrite a newer search or its navigation',async()=>{
    let release;
    const app=await ui(path=>path.includes('afterCursor')?new Promise(resolve=>{release=resolve;}):Promise.resolve(response(path.includes('new-query')?'new query':'first','next')));
    app.get('next').handlers.click();app.get('search').value='new-query';await app.click('find');
    release(response('stale page'));await app.settle();
    assert.equal(app.get('results').children[0].children[0].textContent,'new query');assert.equal(app.get('pageNumber').textContent,'Page 1');
});
test('failed next page retains an explicit retry of the same cursor and does not auto retry',async()=>{
    let failed=true;
    const app=await ui(async path=>path.includes('afterCursor')&&failed?new Response('{}',{status:503}):response('page','next'));
    await app.click('next');assert.equal(app.calls.length,2);assert.equal(app.get('results').children.length,0);
    assert.equal(app.get('retry').disabled,false);failed=false;await app.click('retry');
    assert.equal(app.calls[2].path,app.calls[1].path);assert.equal(app.get('pageNumber').textContent,'Page 2');
});

const guidance='Enter a public address ID, or use Browse all.';
async function pendingPage(direction){
    const requests=[];
    const app=await ui(path=>new Promise((resolve,reject)=>requests.push({path,resolve,reject})),{waitForInitial:false});
    let pending=app.ready;
    if(direction!=='initial'){
        requests.at(-1).resolve(response('page one','cursor/+?'));await pending;
        pending=app.get('next').handlers.click();
        if(direction==='previous'){
            requests.at(-1).resolve(response('page two','third'));await pending;
            pending=app.get('previous').handlers.click();
        }
    }
    return {app,requests,pending};
}
function visiblePage(app){
    return {rows:JSON.stringify(app.get('results').children),page:app.get('pageNumber').textContent};
}
async function emptyLookup(app){
    const before=visiblePage(app),count=app.calls.length;
    for(const value of ['', ' \t ']){
        app.get('address').value=value;await app.get('lookup').handlers.click();
        assert.equal(app.calls.length,count,'Empty lookup must not fetch');
        assert.deepEqual(visiblePage(app),before,'Empty lookup must preserve displayed rows and page');
        assert.equal(app.get('status').textContent,guidance);
    }
    return before;
}
const oldResponses={
    success:request=>request.resolve(response('stale page','stale cursor')),
    unavailable:request=>request.resolve(new Response('{}',{status:503})),
    rateLimited:request=>request.resolve(new Response('{}',{status:429,headers:{'Retry-After':'42'}})),
    networkError:request=>request.reject(new Error('old connection failed'))
};
for(const direction of ['initial','next','previous']){
    for(const [outcome,complete] of Object.entries(oldResponses)){
        test(`empty lookup invalidates ${direction} ${outcome} and permits explicit retry, lookup and browse`,async()=>{
            const {app,requests,pending}=await pendingPage(direction);
            const old=requests.at(-1),before=await emptyLookup(app);
            complete(old);await pending;
            assert.deepEqual(visiblePage(app),before,'Old response must not replace retained rows or page');
            assert.equal(app.get('status').textContent,guidance,'Old response must not overwrite input guidance');
            assert.equal(app.get('previous').disabled,direction!=='previous');
            assert.equal(app.get('next').disabled,direction==='initial');
            assert.equal(app.get('retry').disabled,false,'The invalidated attempt remains explicitly retryable');

            const retry=app.get('retry').handlers.click();
            assert.equal(app.calls.length,requests.length);
            assert.notEqual(requests.at(-1),old,'Retry must actually fetch');
            assert.equal(requests.at(-1).path,old.path);
            requests.at(-1).resolve(response('retried','new cursor'));await retry;
            assert.equal(app.get('pageNumber').textContent,direction==='next'?'Page 2':'Page 1');
            assert.equal(app.get('results').children[0].children[0].textContent,'retried');
            assert.equal(app.get('retry').disabled,true);

            app.get('address').value=' 12345678-1234-1234-1234-123456789abc ';
            const lookup=app.get('lookup').handlers.click();
            assert.equal(requests.at(-1).path,'/v1/directory/address?addressId=12345678-1234-1234-1234-123456789abc');
            requests.at(-1).resolve(new Response(JSON.stringify(item('exact result'))));await lookup;
            assert.equal(app.get('pageNumber').textContent,'Exact lookup');
            assert.equal(app.get('results').children[0].children[0].textContent,'exact result');

            const browse=app.get('browse').handlers.click();
            assert.equal(requests.at(-1).path,'/v1/directory?limit=10&q=');
            requests.at(-1).resolve(response('fresh first page','fresh cursor'));await browse;
            assert.equal(app.get('pageNumber').textContent,'Page 1');
            assert.equal(app.get('next').disabled,false);
            assert.equal(app.get('previous').disabled,true);
        });
    }
    test(`empty lookup during ${direction} restores navigation before the old response settles`,async()=>{
        const {app,requests,pending}=await pendingPage(direction);
        const old=requests.at(-1);await emptyLookup(app);
        const action=direction==='initial'?'browse':direction;
        assert.equal(app.get(action).disabled,false);
        const next=app.get(action).handlers.click(),current=requests.at(-1);
        assert.notEqual(current,old,'Navigation must not remain blocked by the invalidated attempt');
        old.reject(new Error('stale failure'));await pending;
        assert.equal(app.get('status').textContent,'Loading…');
        for(const id of ['previous','next','retry'])assert.equal(app.get(id).disabled,true);
        current.resolve(response('current page','current cursor'));await next;
        assert.equal(app.get('results').children[0].children[0].textContent,'current page');
        assert.equal(app.get('pageNumber').textContent,direction==='next'?'Page 2':'Page 1');
    });
    test(`empty lookup during ${direction} JSON decoding ignores the late body`,async()=>{
        const {app,requests,pending}=await pendingPage(direction);
        let releaseBody,bodyStarted;
        const started=new Promise(resolve=>{bodyStarted=resolve;});
        requests.at(-1).resolve({ok:true,status:200,json:()=>{bodyStarted();return new Promise(resolve=>{releaseBody=resolve;});}});
        await started;const before=await emptyLookup(app);
        releaseBody({items:[item('late decoded body')],nextCursor:'late'});await pending;
        assert.deepEqual(visiblePage(app),before);
        assert.equal(app.get('status').textContent,guidance);
    });
}
test('empty lookup after a page failure preserves explicit retry and recovers to an empty page',async()=>{
    const {app,requests,pending}=await pendingPage('next');
    requests.at(-1).resolve(new Response('{}',{status:503}));await pending;
    await emptyLookup(app);assert.equal(app.get('retry').disabled,false);
    const retry=app.get('retry').handlers.click();
    requests.at(-1).resolve(new Response(JSON.stringify({items:[],nextCursor:null})));await retry;
    assert.equal(app.get('status').textContent,'No current public addresses.');
    assert.equal(app.get('pageNumber').textContent,'Page 2');
    assert.equal(app.get('next').disabled,true);assert.equal(app.get('retry').disabled,true);
});

const exactId='12345678-1234-1234-1234-123456789abc';
const invalidGuidance='Enter a valid public address ID (lowercase UUID), or use Browse all.';
for(const invalid of ['not-an-address',exactId.toUpperCase(),exactId.replaceAll('-',''),'{'+exactId+'}',exactId.slice(1),exactId.replace('a','g')]){
    test('invalid exact address input preserves browsing without requesting or retrying: '+invalid,async()=>{
        const app=await ui(async path=>path.includes('/address?')?new Response('{}',{status:400}):response('newest','next')),before=visiblePage(app),count=app.calls.length;
        app.get('address').value=invalid;await app.click('lookup');
        assert.equal(app.get('status').textContent,invalidGuidance);
        assert.equal(app.calls.length,count,'Invalid input must not make an HTTP request');
        assert.deepEqual(visiblePage(app),before);
        assert.equal(app.get('retry').disabled,true);
        await app.click('retry');assert.equal(app.calls.length,count);
        await app.click('browse');assert.equal(app.get('address').value,'');
        assert.equal(app.get('pageNumber').textContent,'Page 1');
    });
}
for(const id of ['00000000-0000-0000-0000-000000000000',exactId,'ffffffff-ffff-ffff-ffff-ffffffffffff']){
    test('exact lookup accepts the server UUID shape without inventing version restrictions: '+id,async()=>{
        const app=await ui(async path=>path.includes('/address?')?new Response(JSON.stringify(item('found'))):response('browse'));
        app.get('address').value=' '+id+' ';await app.click('lookup');
        assert.equal(app.calls.at(-1).path,'/v1/directory/address?addressId='+id);
        assert.equal(app.get('pageNumber').textContent,'Exact lookup');
        assert.equal(app.get('results').children[0].children[0].textContent,'found');
    });
}
for(const [status,message,retryable] of [
    [400,invalidGuidance,false],
    [404,'Public address unavailable.',false],
    [429,'Too many registry requests. Try again in 42 seconds.',true],
    [503,'Registry unavailable (503).',true],
    [null,'Registry unavailable. Please try again.',true]
]){
    test('exact lookup classifies '+status+' and recovers with a corrected address and Browse all',async()=>{
        let fail=true;
        const app=await ui(async path=>{
            if(!path.includes('/address?'))return response('browse');
            if(!fail)return new Response(JSON.stringify(item('corrected')));
            if(status===null)throw Error('private network diagnostic');
            return new Response('{}',{status,headers:{'Retry-After':'42'}});
        });
        app.get('address').value=exactId;await app.click('lookup');
        assert.equal(app.get('status').textContent,message);
        assert.equal(app.get('retry').disabled,!retryable);
        assert.equal(app.get('pageNumber').textContent,retryable?'Page not loaded':'Exact lookup');
        assert.equal(app.get('results').children.length,0);
        const count=app.calls.length;
        await app.click('retry');assert.equal(app.calls.length,count+(retryable?1:0));
        fail=false;app.get('address').value='00000000-0000-0000-0000-000000000001';await app.click('lookup');
        assert.equal(app.get('results').children[0].children[0].textContent,'corrected');
        assert.equal(app.get('retry').disabled,true);
        await app.click('browse');assert.equal(app.get('pageNumber').textContent,'Page 1');
        assert.equal(app.get('results').children[0].children[0].textContent,'browse');
    });
}

test('invalid input cancels stale exact lookup recovery without letting its late error overwrite guidance',async()=>{
    const waiting=[];
    const app=await ui(path=>path.includes('/address?')?new Promise(resolve=>{waiting.push(resolve);}):Promise.resolve(response('browse','next')));
    app.get('address').value=exactId;const old=app.get('lookup').handlers.click();
    app.get('address').value='not-an-address';const invalid=app.get('lookup').handlers.click();
    try{
        assert.equal(app.get('status').textContent,invalidGuidance);assert.equal(app.get('retry').disabled,true);
        assert.equal(waiting.length,1,'Invalid input must not start another lookup');
    }finally{
        for(const resolve of waiting)resolve(new Response('{}',{status:400}));
        await Promise.all([old,invalid]);
    }
    assert.equal(app.get('status').textContent,invalidGuidance);assert.equal(app.get('retry').disabled,true);
    await app.click('browse');assert.equal(app.get('pageNumber').textContent,'Page 1');
});
