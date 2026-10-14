import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { createContext, runInContext } from 'node:vm';
const html=readFileSync(new URL('../src/registry.html',import.meta.url),'utf8');
const script=/<script nonce="WEAVE_NONCE">([\s\S]*?)<\/script>/.exec(html)[1];
const item=label=>({label,addressId:label,accountId:'public-owner',isPublic:true});
const response=(label,nextCursor=null)=>new Response(JSON.stringify({items:[item(label)],nextCursor}));
async function ui(fetcher=async path=>path.includes('afterCursor')?response('older'):response('newest','cursor/+?')){
    const elements=new Map(),calls=[];
    const element=()=>({value:'',textContent:'',children:[],disabled:false,handlers:{},append(child){this.children.push(child);},replaceChildren(...children){this.children=children;},addEventListener(event,handler){this.handlers[event]=handler;}});
    const get=id=>{if(!elements.has(id))elements.set(id,element());return elements.get(id);};
    runInContext(script,createContext({document:{getElementById:get,createElement:element},fetch:(path,options)=>{calls.push({path,options});return fetcher(path,options);}}));
    const settle=async()=>{for(let i=0;i<100;i++){await new Promise(resolve=>setImmediate(resolve));if(get('status').textContent!=='Loading…')return;}throw Error('Registry did not settle');};
    await settle();
    return {get,calls,settle,async click(id){assert.equal(typeof get(id).handlers.click,'function',id+' click action is missing');get(id).handlers.click();await settle();}};
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
