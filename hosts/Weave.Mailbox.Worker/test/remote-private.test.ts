import assert from 'node:assert/strict';
import {test} from 'node:test';
import {remoteMcp,type PrivateAccess} from '../src/remote-mcp.ts';
import {privateScope,privateMcpTools,withoutControl,executePrivateMcp} from '../src/remote-private.ts';
import {start,payload} from './harness.ts';
import type {Env} from '../src/database.ts';

const origin='https://demo.test';
const headers={'Content-Type':'application/json',Accept:'application/json, text/event-stream'};
const rpc=(name:string,args:Record<string,unknown>={})=>({jsonrpc:'2.0',id:31,method:'tools/call',params:{name,arguments:args}});
const request=(value:unknown)=>new Request(origin+'/mcp',{method:'POST',headers,body:JSON.stringify(value)});
const unused=async(_url:URL):Promise<Response>=>{throw Error('No public read expected');};
const accountId='01234567-89ab-4def-8123-456789abcdef';
test('without OAuth the remote endpoint does not expose private tools',async()=>{
    const response=await remoteMcp(request(rpc('send_message')),unused);
    assert.equal(response.status,200);
    assert.equal((await response.json()).error.code,-32602);
});
test('private tool discovery does not grant authority and rejected bearer never executes the core',async()=>{
    let writes=0;
    const denied:PrivateAccess={tools:privateMcpTools,scope:privateScope,
        authorize:async()=>new Response('{}',{status:401,headers:{'WWW-Authenticate':'Bearer'}}),
        invoke:async()=>{writes++;return new Response('{}');}};
    const list=await remoteMcp(request({jsonrpc:'2.0',id:1,method:'tools/list'}),unused,denied);
    assert.equal(list.status,200);
    const listed=(await list.json()).result.tools;
    assert.ok(listed.some((tool:{name:string})=>tool.name==='send_message'));
    assert.deepEqual(listed.find((tool:{name:string})=>tool.name==='send_message').securitySchemes,[{type:'oauth2',scopes:['weave:messages.send']}]);
    const forbidden=await remoteMcp(request(rpc('send_message',{})),unused,denied);
    assert.equal(forbidden.status,401);
    const notification=await remoteMcp(request({jsonrpc:'2.0',method:'tools/call',params:{name:'send_message',arguments:{}}}),unused,denied);
    assert.equal(notification.status,202);
    assert.equal(writes,0,'missing id/bearer must never dispatch a write');
});
test('private route receives only the introspected owner and required scope, never a supplied account override',async()=>{
    let actual:{owner:string;name:string;scope:string;args:Record<string,unknown>}|null=null,required='';
    const permitted:PrivateAccess={tools:privateMcpTools,scope:privateScope,
        authorize:async scope=>{required=scope;return accountId;},
        invoke:async (owner,name,args)=>{actual={owner,name,scope:required,args};return new Response('{"items":[]}',{headers:{'Content-Type':'application/json'}});}};
    const answer=await remoteMcp(request(rpc('list_my_inbox',{})),unused,permitted);
    assert.equal(answer.status,200);
    assert.deepEqual(actual,{owner:accountId,name:'list_my_inbox',scope:'weave:messages.read',args:{}});
    assert.deepEqual(JSON.parse((await answer.json()).result.content[0].text),{items:[]});
});
test('model-visible result strips connection tokens recursively',()=>{
    const item={items:[{accountId:'peer',connectionToken:'SECRET',nested:{controlHash:'HASH',generation:2n}}]};
    assert.deepEqual(withoutControl(item),{items:[{accountId:'peer',nested:{generation:2n}}]});
});
test('real D1 account-scoped contact, message and ACK use server-side channel tokens',async()=>{
    const h=await start();
    try{
        const env:Env={db:h.db,REGISTRATION_ENABLED:'true',REGISTRATION_MAX_ACCOUNTS:'100',REGISTRATION_PER_IP_PER_HOUR:'10',
            REGISTRATION_GLOBAL_PER_HOUR:'100',CLEANUP_MAX_MESSAGES:'0',CLEANUP_TIME_BUDGET_MS:'0'};
        const a=await h.participant(),b=await h.participant(),c=await h.participant();
        const contact=payload('request');
        const requested=await executePrivateMcp(env,origin,a.accountId,'request_contact',{
            requestId:contact.messageId,requesterAddressId:a.cardId,recipientAccountId:b.accountId,recipientAddressId:b.cardId,payload:contact
        });
        assert.equal(requested.status,200,await requested.clone().text());
        const state=await requested.json();
        const accepted=await executePrivateMcp(env,origin,b.accountId,'decide_contact',{
            requestId:contact.messageId,requesterAccountId:a.accountId,expectedGeneration:String(state.generation),status:'accepted'
        });
        assert.equal(accepted.status,200,await accepted.clone().text());
        const contacts=await executePrivateMcp(env,origin,a.accountId,'list_my_contacts',{});
        assert.equal(contacts.status,200);
        assert.doesNotMatch(await contacts.text(),/connectionToken|controlHash/);
        const ordinary=payload('hello');
        const input={recipientAccountId:b.accountId,contactGeneration:String(state.generation),payload:ordinary};
        const sent=await executePrivateMcp(env,origin,a.accountId,'send_message',input);
        assert.equal(sent.status,200,await sent.clone().text());
        const duplicate=await executePrivateMcp(env,origin,a.accountId,'send_message',input);
        assert.equal(duplicate.status,200,'same ID/content is idempotent');
        const inbox=await executePrivateMcp(env,origin,b.accountId,'list_my_inbox',{});
        assert.equal(inbox.status,200);
        assert.ok((await inbox.json()).items.some((item:{payload:{messageId:string}})=>item.payload?.messageId===ordinary.messageId));
        const foreign=await executePrivateMcp(env,origin,c.accountId,'ack_message',{messageId:ordinary.messageId,senderAccountId:a.accountId});
        assert.equal(foreign.status,404,'another account must never acknowledge B inbox');
        const receipt=await executePrivateMcp(env,origin,b.accountId,'ack_message',{messageId:ordinary.messageId,senderAccountId:a.accountId});
        assert.equal(receipt.status,200);
        assert.equal((await receipt.json()).state,'acknowledged');
    }finally{await h.close();}
});

test('real Worker advertises protected-resource metadata and challenges unauthenticated private tools',async()=>{
    const h=await start({bindings:{
        OAUTH_ISSUER:'https://auth.example',OAUTH_RESOURCE:'https://localhost/mcp',
        OAUTH_INTROSPECTION_URL:'https://auth.example/oauth/introspect',
        OAUTH_CLIENT_ID:'weave-client',OAUTH_CLIENT_SECRET:'local_fake_secret_for_testing_only'
    }});
    try{
        const discovery=await h.mf.dispatchFetch('https://localhost/.well-known/oauth-protected-resource/mcp');
        assert.equal(discovery.status,200,await discovery.clone().text());
        const meta=await discovery.json();
        assert.equal(meta.resource,'https://localhost/mcp');
        assert.deepEqual(meta.authorization_servers,['https://auth.example']);
        const before=await h.db.prepare('SELECT count(*) n FROM mailbox_records').first('n');
        const response=await h.mf.dispatchFetch('https://localhost/mcp',{method:'POST',headers,
            body:JSON.stringify(rpc('send_message',{}))});
        assert.equal(response.status,401);
        assert.match(response.headers.get('WWW-Authenticate')??'',/scope="weave:messages.send"/);
        assert.equal(await h.db.prepare('SELECT count(*) n FROM mailbox_records').first('n'),before);
    }finally{await h.close();}
});
