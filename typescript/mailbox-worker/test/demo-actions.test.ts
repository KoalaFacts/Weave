import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFile } from 'node:fs/promises';
import { createContext, runInContext } from 'node:vm';
import { webcrypto } from 'node:crypto';
import { agentKey } from './agent-key.ts';
const html=await readFile('src/demo.html','utf8');
const script=/<script nonce="WEAVE_NONCE">([\s\S]*?)<\/script>/.exec(html)[1];
const keyFiles=new Map(),accountsByKey=new Map();
// Each synthetic account ID is one character; its fake access token repeats that character.
function keyFile(accountId){
    if(!keyFiles.has(accountId)){const {privateJwk}=agentKey();accountsByKey.set(privateJwk.x,accountId);keyFiles.set(accountId,JSON.stringify({accountId,key:{kty:'OKP',crv:'Ed25519',x:privateJwk.x,d:privateJwk.d}}));}
    return keyFiles.get(accountId);
}
const decode=part=>JSON.parse(Buffer.from(part,'base64url').toString());
const who=options=>/^DPoP (.)/.exec(options.headers.Authorization??'')?.[1];
function ui(fetch){
    const elements=new Map();
    function element(){return {value:'',textContent:'',checked:false,disabled:false,readOnly:false,children:[],handlers:{},classList:{toggle(){}},addEventListener(name,fn){this.handlers[name]=fn;},append(...children){this.children.push(...children);},replaceChildren(...children){this.children=children;this.textContent='';}};}
    const get=id=>{if(!elements.has(id))elements.set(id,element());return elements.get(id);};
    const context=createContext({document:{getElementById:get,createElement:element},fetch,location:{origin:'http://127.0.0.1:43210'},crypto:webcrypto,TextEncoder,TextDecoder,AbortController,btoa,atob,navigator:{clipboard:{writeText:async()=>{}}}});
    runInContext(script,context);
    return {get,click:id=>get(id).handlers.click(),async register(accountId='a'){get('agentKey').value=keyFile(accountId);await get('register').handlers.click();}};
}
const json=(value,status=200)=>new Response(JSON.stringify(value),{status});
const base=(path,options)=>path.startsWith('/v1/contacts/accounts/')?json({accountId:'b',connectionToken:'c'.repeat(64),canSend:true}):path==='/v1/accounts'?json({accountId:accountsByKey.get(decode(options.headers.DPoP.split('.')[0]).jwk.x),displayName:null,description:null}):path==='/v1/token'?json({access_token:decode(new URLSearchParams(options.body).get('client_assertion').split('.')[1]).iss.repeat(43),token_type:'DPoP',expires_in:900}):path.startsWith('/v1/addresses')?json({items:[],nextCursor:null}):null;
test('demo confirmed send plus failed receipt refresh does not turn into a resend',async()=>{
    const posts=[];
    const app=ui(async(path,options)=>base(path,options)??(path.startsWith('/v1/contacts/channel')?json({generation:1,peerAccountId:'b'}):options.method==='POST'?(posts.push(JSON.parse(options.body)),json({state:'pending'})):json({title:'Storage unavailable'},503)));
    await app.register();app.get('peer').value='b';app.get('message').value='one message';await app.click('send');
    assert.equal(posts.length,1);assert.equal(app.get('send').textContent,'Send message');assert.equal(app.get('message').value,'');assert.match(app.get('status').textContent,/Message sent; refresh outbox/);
    await app.click('send');assert.equal(posts.length,1);
});
test('demo unknown send result preserves the exact envelope on retry',async()=>{
    const posts=[];
    const app=ui(async(path,options)=>{const initial=base(path,options);if(initial)return initial;if(path.startsWith('/v1/contacts/channel'))return json({generation:1,peerAccountId:'b'});if(options.method==='POST'){posts.push(JSON.parse(options.body));if(posts.length===1)throw Error('Connection lost');return json({state:'pending'});}return json({items:[],nextCursor:null});});
    await app.register();app.get('peer').value='b';app.get('message').value='one message';await app.click('send');assert.equal(app.get('send').textContent,'Retry same message');assert.equal(app.get('message').readOnly,true);
    await app.click('send');assert.equal(posts.length,2);assert.deepEqual(posts[0],posts[1]);assert.equal(app.get('message').readOnly,false);
});
test('demo account switch cancels stale continuations before any message dispatch',async()=>{
    let resolveChannel,entered;const waiting=new Promise(resolve=>{entered=resolve;});const posts=[];
    const app=ui(async(path,options)=>{const initial=base(path,options);if(initial)return initial;if(path.startsWith('/v1/contacts/channel')){entered();return new Promise(resolve=>{resolveChannel=resolve;});}if(options.method==='POST')posts.push(options);return json({items:[],nextCursor:null});});
    await app.register();app.get('peer').value='b';app.get('message').value='old-account message';const pending=app.click('send');await waiting;
    await app.register('c');resolveChannel(json({generation:1,peerAccountId:'b'}));await pending;
    assert.equal(posts.length,0);assert.equal(app.get('identity').textContent,'Account ID: c');assert.equal(app.get('message').value,'');
});
test('demo confirmed contact plus failed refresh stays submitted',async()=>{
    const posts=[];
    const app=ui(async(path,options)=>base(path,options)??(options.method==='POST'?(posts.push(JSON.parse(options.body)),json({generation:1})):json({title:'Storage unavailable'},503)));
    await app.register();app.get('requesterAddressId').value='my-address';app.get('recipientAccountId').value='b';app.get('addressId').value='public-address';app.get('intro').value='hello';await app.click('requestContact');
    assert.equal(posts.length,1);assert.equal(app.get('requestContact').textContent,'Request contact');assert.match(app.get('status').textContent,/Contact request submitted; refresh/);
    await app.click('requestContact');assert.equal(posts.length,1);
});
for(const transition of ['clear session','switch account'])test('demo '+transition+' discards private address edits and public checkbox before another account can create',async()=>{
    const writes=[];
    const privateAddress={addressId:'a-private-address',accountId:'a',label:'private label',isPublic:false,effectiveFrom:'2026-01-01T00:00:00Z',effectiveTo:'2027-01-01T00:00:00Z',directUri:'https://private.example.test/contact'};
    const app=ui(async(path,options)=>{
        if(path.startsWith('/v1/addresses')){
            if(options.method==='POST'){writes.push({account:who(options),body:JSON.parse(options.body)});return json({});}
            return json({items:who(options)==='a'?[privateAddress]:[],nextCursor:null});
        }
        return base(path,options);
    });
    await app.register();
    const edit=app.get('addresses').children[0].children.find(child=>child.textContent==='Edit');
    await edit.handlers.click();assert.equal(app.get('label').value,privateAddress.label);
    app.get('label').value='unsaved private label';app.get('directUri').value='https://unsaved.example.test/private';app.get('isPublic').checked=true;
    if(transition==='clear session')await app.click('disconnect');else await app.register('b');
    for(const id of ['label','from','to','directUri'])assert.equal(app.get(id).value,'',id+' retained old-account data');
    assert.equal(app.get('isPublic').checked,false);assert.equal(app.get('createAddress').textContent,'Create address');
    if(transition==='clear session')await app.register('b');
    await app.click('createAddress');assert.deepEqual(writes,[{account:'b',body:{label:null,isPublic:false,effectiveFrom:null,effectiveTo:null,directUri:null}}]);
});

const introductionRequest={requestId:'request-from-b',requestMessageId:'intro-from-b',requesterAccountId:'b',recipientAccountId:'a',status:'pending',generation:1};
const introduction=(messageId='intro-from-b',sender='b',recipient='a',text='Hello 你好 <img src=x onerror=alert(1)>')=>({senderAccountId:sender,envelope:{recipientAccountId:recipient,payload:{messageId,payloadEncoding:'text/plain',bytes:Buffer.from(text).toString('base64')}}});
const rendered=node=>[node.textContent,...node.children.map(rendered)].join('\n');

test('contact history shows expired, accepted and rejected requests without offering expired decisions',async()=>{
    const requests=['expired','accepted','rejected'].map((status,i)=>({...introductionRequest,requestId:'history-'+i,status,expiresAt:'2026-01-01T00:00:00Z'}));
    const app=ui(async(path,options)=>base(path,options)??(path==='/v1/contacts/requests'?json({items:requests,nextCursor:null}):json({items:[],nextCursor:null})));
    await app.register();await app.click('loadContacts');
    const rows=app.get('contacts').children;assert.equal(rows.length,3);
    for(let i=0;i<3;i++)assert.match(rendered(rows[i]),new RegExp(requests[i].status+' · generation 1'));
    assert.match(rendered(rows[0]),/Expired on 2026-01-01T00:00:00Z.*stays in your history/);
    for(const row of rows)assert.equal(row.children.some(child=>['Accept contact','Ask for follow-up','Reject contact'].includes(child.textContent)),false);
    assert.match(rendered(rows[0]),/Introduction is no longer pending/);
});

test('contact metadata is visible while its separate introduction read is still pending',async()=>{
    let release,entered;const waiting=new Promise(resolve=>{entered=resolve;});
    const app=ui(async(path,options)=>base(path,options)??(path==='/v1/contacts/requests'?json({items:[introductionRequest],nextCursor:null}):(entered(),new Promise(resolve=>{release=resolve;}))));
    await app.register();app.get('contacts').textContent='No contact requests yet.';
    const pending=app.click('loadContacts');await waiting;
    try {
        assert.match(rendered(app.get('contacts')),/request-from-b/);
        assert.match(rendered(app.get('contacts')),/From: b/);
        assert.doesNotMatch(rendered(app.get('contacts')),/No contact requests yet/);
        assert.match(rendered(app.get('contacts')),/Loading introduction/);
    } finally {release(json({items:[introduction()],nextCursor:null}));await pending;}
    assert.match(rendered(app.get('contacts')),/Hello 你好/);
});

test('contact review shows only its exact incoming introduction as inert text without ACK or acceptance',async()=>{
    const reads=[];
    const app=ui(async(path,options)=>{
        const initial=base(path,options);if(initial)return initial;reads.push([path,options.method,who(options)]);
        if(path==='/v1/contacts/requests')return json({items:[introductionRequest],nextCursor:null});
        return json({items:[introduction('intro-from-b','foreign','a','wrong sender'),introduction('intro-from-b','b','foreign','wrong recipient'),introduction('unrelated','b','a','wrong message'),introduction()],nextCursor:null});
    });
    await app.register();await app.click('loadContacts');
    const row=app.get('contacts').children[0],text=rendered(row);
    assert.match(text,/Hello 你好 <img src=x onerror=alert\(1\)>/);
    assert.doesNotMatch(text,/wrong sender|wrong recipient|wrong message/);
    assert.match(text,/request-from-b/);
    assert.ok(row.children.some(child=>child.textContent==='Accept contact'));
    assert.ok(reads.some(([path])=>path.startsWith('/v1/inbox')));
    assert.ok(reads.every(([,method,account])=>method==='GET'&&account==='a'));
});

for(const mode of ['missing','read failed'])test('contact review honestly reports introduction '+mode+' and preserves explicit decisions',async()=>{
    const app=ui(async(path,options)=>base(path,options)??(path==='/v1/contacts/requests'?json({items:[introductionRequest],nextCursor:null}):mode==='missing'?json({items:[],nextCursor:null}):json({title:'Storage unavailable'},503)));
    await app.register();await app.click('loadContacts');
    const row=app.get('contacts').children[0];
    assert.match(rendered(row),mode==='missing'?/no longer pending.*acknowledged.*expired/i:/could not load.*refresh requests/i);
    assert.ok(row.children.some(child=>child.textContent==='Accept contact'));
});

test('clearing while an introduction read is pending prevents a late private body from rendering',async()=>{
    let release,entered;const waiting=new Promise(resolve=>{entered=resolve;});
    const app=ui(async(path,options)=>base(path,options)??(path==='/v1/contacts/requests'?json({items:[introductionRequest],nextCursor:null}):(entered(),new Promise(resolve=>{release=resolve;}))));
    await app.register();const pending=app.click('loadContacts');await waiting;
    await app.click('disconnect');release(json({items:[introduction()],nextCursor:null}));await pending;
    assert.equal(rendered(app.get('contacts')),'');assert.equal(app.get('workspace').hidden,true);
});

test('an older introduction response cannot replace a newer contact page',async()=>{
    let release,entered,contactReads=0,nextDisabled;const waiting=new Promise(resolve=>{entered=resolve;});
    const newer={...introductionRequest,requestId:'newer-request-from-c',requesterAccountId:'c',requestMessageId:'intro-from-c'};
    const app=ui(async(path,options)=>{
        const initial=base(path,options);if(initial)return initial;
        if(path.startsWith('/v1/contacts/requests'))return json({items:[++contactReads===1?introductionRequest:newer],nextCursor:contactReads===1?'contacts:a:10':null});
        if(contactReads===1){entered();return new Promise(resolve=>{release=resolve;});}
        return json({items:[introduction('intro-from-c','c','a','Newer introduction')],nextCursor:null});
    });
    await app.register();const pending=app.click('loadContacts');await waiting;
    try {
        assert.equal(app.get('contactsNext').disabled,false);
        await app.click('contactsNext');
        assert.match(rendered(app.get('contacts')),/newer-request-from-c/);
        assert.match(rendered(app.get('contacts')),/Newer introduction/);
        nextDisabled=app.get('contactsNext').disabled;
    } finally {release(json({items:[introduction()],nextCursor:null}));await pending;}
    assert.match(rendered(app.get('contacts')),/newer-request-from-c/);
    assert.doesNotMatch(rendered(app.get('contacts')),/request-from-b|Hello 你好/);
    assert.equal(app.get('contactsNext').disabled,nextDisabled);
});

test('contact introduction lookup follows inbox cursors and keeps colliding message IDs bound to their requester',async()=>{
    const other={...introductionRequest,requestId:'request-from-c',requesterAccountId:'c'};
    const paths=[];
    const app=ui(async(path,options)=>{
        const initial=base(path,options);if(initial)return initial;paths.push(path);
        if(path==='/v1/contacts/requests')return json({items:[introductionRequest,other],nextCursor:null});
        if(!path.includes('afterCursor'))return json({items:[introduction('intro-from-b','c','a','Introduction from C')],nextCursor:'inbox:a:10'});
        return json({items:[introduction()],nextCursor:null});
    });
    await app.register();await app.click('loadContacts');
    const [fromB,fromC]=app.get('contacts').children.map(rendered);
    assert.match(fromB,/Hello 你好/);assert.doesNotMatch(fromB,/Introduction from C/);
    assert.match(fromC,/Introduction from C/);assert.doesNotMatch(fromC,/Hello 你好/);
    assert.ok(paths.includes('/v1/inbox?limit=10&afterCursor=inbox%3Aa%3A10'));
});

test('contact introduction lookup is bounded and does not call a partial traversal acknowledged or expired',async()=>{
    let pages=0;
    const app=ui(async(path,options)=>base(path,options)??(path==='/v1/contacts/requests'?json({items:[introductionRequest],nextCursor:null}):json({items:[],nextCursor:'inbox:a:'+ ++pages})));
    await app.register();await app.click('loadContacts');
    assert.equal(pages,8);
    const text=rendered(app.get('contacts'));
    assert.match(text,/not found in the loaded inbox pages/);assert.doesNotMatch(text,/acknowledged|expired/);
});
