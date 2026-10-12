import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFile } from 'node:fs/promises';
import { createContext, runInContext } from 'node:vm';
import { webcrypto } from 'node:crypto';
const html=await readFile('src/demo.html','utf8');
const script=/<script nonce="WEAVE_NONCE">([\s\S]*?)<\/script>/.exec(html)[1];
function ui(fetch){
    const elements=new Map();
    function element(){return {value:'',textContent:'',checked:false,disabled:false,readOnly:false,children:[],handlers:{},classList:{toggle(){}},addEventListener(name,fn){this.handlers[name]=fn;},append(...children){this.children.push(...children);},replaceChildren(...children){this.children=children;this.textContent='';}};}
    const get=id=>{if(!elements.has(id))elements.set(id,element());return elements.get(id);};
    const context=createContext({document:{getElementById:get,createElement:element},fetch,crypto:webcrypto,TextEncoder,TextDecoder,AbortController,btoa,atob,navigator:{clipboard:{writeText:async()=>{}}}});
    runInContext(script,context);
    return {get,click:id=>get(id).handlers.click(),async register(control='a'.repeat(64)){get('control').value=control;await get('register').handlers.click();}};
}
const json=(value,status=200)=>new Response(JSON.stringify(value),{status});
const base=(path,options)=>path==='/v1/accounts'?json({agentId:options.headers['X-Weave-Mailbox-Control'][0],displayName:null,description:null}):path==='/v1/addresses'?json({items:[],nextCursor:null}):null;
test('demo confirmed send plus failed receipt refresh does not turn into a resend',async()=>{
    const posts=[];
    const app=ui(async(path,options)=>base(path,options)??(path.startsWith('/v1/contacts/channel')?json({generation:1,peerMailboxId:'b'}):options.method==='POST'?(posts.push(JSON.parse(options.body)),json({state:'pending'})):json({title:'Storage unavailable'},503)));
    await app.register();app.get('peer').value='b';app.get('message').value='one message';await app.click('send');
    assert.equal(posts.length,1);assert.equal(app.get('send').textContent,'Send message');assert.equal(app.get('message').value,'');assert.match(app.get('status').textContent,/Message sent; refresh outbox/);
    await app.click('send');assert.equal(posts.length,1);
});
test('demo unknown send result preserves the exact envelope on retry',async()=>{
    const posts=[];
    const app=ui(async(path,options)=>{const initial=base(path,options);if(initial)return initial;if(path.startsWith('/v1/contacts/channel'))return json({generation:1,peerMailboxId:'b'});if(options.method==='POST'){posts.push(JSON.parse(options.body));if(posts.length===1)throw Error('Connection lost');return json({state:'pending'});}return json({items:[],nextCursor:null});});
    await app.register();app.get('peer').value='b';app.get('message').value='one message';await app.click('send');assert.equal(app.get('send').textContent,'Retry same message');assert.equal(app.get('message').readOnly,true);
    await app.click('send');assert.equal(posts.length,2);assert.deepEqual(posts[0],posts[1]);assert.equal(app.get('message').readOnly,false);
});
test('demo account switch cancels stale continuations before any message dispatch',async()=>{
    let resolveChannel,entered;const waiting=new Promise(resolve=>{entered=resolve;});const posts=[];
    const app=ui(async(path,options)=>{const initial=base(path,options);if(initial)return initial;if(path.startsWith('/v1/contacts/channel')){entered();return new Promise(resolve=>{resolveChannel=resolve;});}if(options.method==='POST')posts.push(options);return json({items:[],nextCursor:null});});
    await app.register();app.get('peer').value='b';app.get('message').value='old-account message';const pending=app.click('send');await waiting;
    await app.register('c'.repeat(64));resolveChannel(json({generation:1,peerMailboxId:'b'}));await pending;
    assert.equal(posts.length,0);assert.equal(app.get('identity').textContent,'Agent ID: c');assert.equal(app.get('message').value,'');
});
test('demo confirmed contact plus failed refresh stays submitted',async()=>{
    const posts=[];
    const app=ui(async(path,options)=>base(path,options)??(options.method==='POST'?(posts.push(JSON.parse(options.body)),json({generation:1})):json({title:'Storage unavailable'},503)));
    await app.register();app.get('addressId').value='public-address';app.get('intro').value='hello';await app.click('requestContact');
    assert.equal(posts.length,1);assert.equal(app.get('requestContact').textContent,'Request contact');assert.match(app.get('status').textContent,/Contact request submitted; refresh/);
    await app.click('requestContact');assert.equal(posts.length,1);
});
for(const transition of ['clear session','switch account'])test('demo '+transition+' discards private address edits and public checkbox before another account can create',async()=>{
    const writes=[];
    const privateAddress={addressId:'a-private-address',accountId:'a',label:'private label',isPublic:false,effectiveFrom:'2026-01-01T00:00:00Z',effectiveTo:'2027-01-01T00:00:00Z',directUri:'https://private.example.test/contact'};
    const app=ui(async(path,options)=>{
        if(path==='/v1/addresses'){
            if(options.method==='POST'){writes.push({control:options.headers['X-Weave-Mailbox-Control'],body:JSON.parse(options.body)});return json({});}
            return json({items:options.headers['X-Weave-Mailbox-Control'][0]==='a'?[privateAddress]:[],nextCursor:null});
        }
        return base(path,options);
    });
    await app.register();
    const edit=app.get('addresses').children[0].children.find(child=>child.textContent==='Edit');
    await edit.handlers.click();assert.equal(app.get('label').value,privateAddress.label);
    app.get('label').value='unsaved private label';app.get('directUri').value='https://unsaved.example.test/private';app.get('isPublic').checked=true;
    if(transition==='clear session')await app.click('disconnect');else await app.register('b'.repeat(64));
    for(const id of ['label','from','to','directUri'])assert.equal(app.get(id).value,'',id+' retained old-account data');
    assert.equal(app.get('isPublic').checked,false);assert.equal(app.get('createAddress').textContent,'Create address');
    if(transition==='clear session')await app.register('b'.repeat(64));
    await app.click('createAddress');assert.deepEqual(writes,[{control:'b'.repeat(64),body:{label:null,isPublic:false,effectiveFrom:null,effectiveTo:null,directUri:null}}]);
});
