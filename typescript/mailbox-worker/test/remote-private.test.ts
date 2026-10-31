import assert from 'node:assert/strict';
import {test} from 'node:test';
import {remoteMcp,type PrivateAccess} from '../src/remote-mcp.ts';
import {authorizeOAuth,oauthConfiguration} from '../src/remote-oauth.ts';
import {build} from 'esbuild';
// The Worker source uses bundler-resolution imports. Evaluate precisely that
// bundled production module instead of relying on Node's extensionless ESM resolution.
const built=await build({entryPoints:['src/remote-private.ts'],bundle:true,write:false,platform:'node',format:'esm',logLevel:'silent'});
const bundle='data:text/javascript;base64,'+Buffer.from(built.outputFiles[0].contents).toString('base64');
const {privateScope,privateMcpTools,withoutSecrets,executePrivateMcp}=await import(bundle) as typeof import('../src/remote-private.ts');
import {start} from './harness.ts';
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
    const item={items:[{accountId:'peer',connectionToken:'SECRET',nested:{connectionToken:'NESTED',generation:2n}}]};
    assert.deepEqual(withoutSecrets(item),{items:[{accountId:'peer',nested:{generation:2n}}]});
});
test('real D1 account-scoped contact, message and ACK use server-side channel tokens',async()=>{
    const h=await start();
    try{
        const env:Env={db:h.db,REGISTRATION_ENABLED:'true',REGISTRATION_MAX_ACCOUNTS:'100',REGISTRATION_PER_IP_PER_HOUR:'10',
            REGISTRATION_GLOBAL_PER_HOUR:'100',CLEANUP_MAX_MESSAGES:'0',CLEANUP_TIME_BUDGET_MS:'0'};
        const a=await h.participant(),b=await h.participant(),c=await h.participant();
        const requested=await executePrivateMcp(env,origin,a.accountId,'request_contact',{
            requesterAddressId:a.cardId,recipientAccountId:b.accountId,recipientAddressId:b.cardId,message:{parts:[{text:'request'}]}
        });
        assert.equal(requested.status,200,await requested.clone().text());
        const state=await requested.json();
        const accepted=await executePrivateMcp(env,origin,b.accountId,'decide_contact',{
            requestId:state.request.requestId,requesterAccountId:a.accountId,expectedGeneration:String(state.generation),status:'accepted'
        });
        assert.equal(accepted.status,200,await accepted.clone().text());
        const contacts=await executePrivateMcp(env,origin,a.accountId,'list_my_contacts',{});
        assert.equal(contacts.status,200);
        assert.doesNotMatch(await contacts.text(),/connectionToken/);
        const input={recipientAccountId:b.accountId,contactGeneration:String(state.generation),message:{parts:[{text:'hello'}]},idempotencyKey:'hello-1'};
        const sent=await executePrivateMcp(env,origin,a.accountId,'send_message',input);
        assert.equal(sent.status,200,await sent.clone().text());
        const ordinary=await sent.json() as {messageId:string};
        const duplicate=await executePrivateMcp(env,origin,a.accountId,'send_message',input);
        assert.equal(duplicate.status,200,'same key and input is idempotent');
        assert.equal((await duplicate.json()).messageId,ordinary.messageId);
        const inbox=await executePrivateMcp(env,origin,b.accountId,'list_my_inbox',{});
        assert.equal(inbox.status,200);
        assert.ok((await inbox.json()).items.some((item:{message?:{messageId:string}})=>item.message?.messageId===ordinary.messageId));
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

test('malformed private request does not sweep or write maintenance state before validation',async()=>{
    const h=await start();
    try{
        const a=await h.participant(),b=await h.participant();
        const env:Env={db:h.db,REGISTRATION_ENABLED:'true',REGISTRATION_MAX_ACCOUNTS:'100',
            REGISTRATION_PER_IP_PER_HOUR:'10',REGISTRATION_GLOBAL_PER_HOUR:'100',
            CLEANUP_MAX_MESSAGES:'1',CLEANUP_TIME_BUDGET_MS:'2000'};
        const revision=await h.db.prepare('SELECT token FROM mailbox_revision WHERE singleton=1').first('token');
        const initial=await h.db.prepare("SELECT count(*) n FROM maintenance_state WHERE name='sweep'").first('n');
        const response=await executePrivateMcp(env,origin,a.accountId,'request_contact',{
            requesterAddressId:a.cardId,recipientAccountId:b.accountId,
            recipientAddressId:b.cardId,message:{notAMessage:true}
        });
        assert.equal(response.status,400,'invalid message must be rejected');
        assert.equal(await h.db.prepare('SELECT token FROM mailbox_revision WHERE singleton=1').first('token'),
            revision,'invalid message must not commit cleanup or any other write');
        assert.equal(await h.db.prepare("SELECT count(*) n FROM maintenance_state WHERE name='sweep'").first('n'),initial);
    }finally{await h.close();}
});


test('Cloudflare-style private token validation reaches real D1 owner-scoped MCP operations without a shared identity',async()=>{
    const h=await start();
    try {
        const env:Env={db:h.db,REGISTRATION_ENABLED:'true',REGISTRATION_MAX_ACCOUNTS:'100',
            REGISTRATION_PER_IP_PER_HOUR:'10',REGISTRATION_GLOBAL_PER_HOUR:'100',
            CLEANUP_MAX_MESSAGES:'0',CLEANUP_TIME_BUDGET_MS:'0'};
        const a=await h.participant(),b=await h.participant();
        const tokenA=a.accountId+':grantA:secretA',tokenB=b.accountId+':grantB:secretB';
        // Each token's mirrored consent grant; the composition below reads it on every call.
        const humanId=crypto.randomUUID(),grants=new Map([[crypto.randomUUID(),a.accountId],[crypto.randomUUID(),b.accountId]]);
        const grantOf=(owner:string)=>[...grants].find(([,account])=>account===owner)![0];
        let revokedA=false,validated=0;
        const cfg=oauthConfiguration({
            OAUTH_ISSUER:'https://auth.example',OAUTH_RESOURCE:origin+'/mcp',
            AUTH_SERVER:{validateToken:async(resource:string,token:string)=>{
                assert.equal(resource,origin+'/mcp');validated++;
                const owner=token===tokenA?a.accountId:token===tokenB?b.accountId:null;
                if(!owner||owner===a.accountId&&revokedA)return null;
                return {audience:resource,userId:owner,props:{accountId:owner,humanId,grantId:grantOf(owner)},
                    expiresAt:Math.floor(Date.now()/1000)+120,
                    scope:['weave:addresses.read'],clientId:'verified-client'};
            }}
        },origin+'/mcp')!;
        const invoke=async(token:string,name:string)=>{
            const callerRequest=new Request(origin+'/mcp',{method:'POST',
                headers:{...headers,Authorization:'Bearer '+token},
                body:JSON.stringify(rpc(name))});
            return remoteMcp(callerRequest,unused,{tools:privateMcpTools,scope:privateScope,
                authorize:async scope=>{
                    const access=await authorizeOAuth(cfg,callerRequest,scope,async grantId=>{
                        const account=grants.get(grantId);
                        return account===undefined?null:{accountId:account,humanId,clientId:'verified-client'};
                    });
                    return access instanceof Response?access:access.accountId;
                },
                invoke:(owner,name,args)=>executePrivateMcp(env,origin,owner,name,args)
            });
        };
        const profileA=await invoke(tokenA,'get_my_profile');
        const profileB=await invoke(tokenB,'get_my_profile');
        assert.equal(profileA.status,200);
        assert.equal(profileB.status,200);
        assert.equal((await profileA.json()).result.structuredContent.id,a.accountId);
        assert.equal((await profileB.json()).result.structuredContent.id,b.accountId);
        const addressesA=await invoke(tokenA,'list_my_addresses');
        const addressesB=await invoke(tokenB,'list_my_addresses');
        assert.equal(addressesA.status,200);assert.equal(addressesB.status,200);
        const entriesA=JSON.parse((await addressesA.json()).result.content[0].text).items;
        const entriesB=JSON.parse((await addressesB.json()).result.content[0].text).items;
        assert.ok(entriesA.some((item:{addressId:string})=>item.addressId===a.cardId));
        assert.ok(entriesB.some((item:{addressId:string})=>item.addressId===b.cardId));
        assert.ok(!entriesA.some((item:{addressId:string})=>item.addressId===b.cardId));
        assert.ok(!entriesB.some((item:{addressId:string})=>item.addressId===a.cardId));
        revokedA=true;
        const denied=await invoke(tokenA,'get_my_profile');
        assert.equal(denied.status,401,'revoked token must not reuse a cached owner');
        assert.ok(validated>=5,'each protected call must independently validate the token');
    } finally {await h.close();}
});

test('private write tool schemas take an A2A Message and an optional idempotency key, never server-set IDs or times',()=>{
    const contact=privateMcpTools.find(tool=>tool.name==='request_contact')!.inputSchema as any;
    assert.equal(Object.hasOwn(contact.properties,'requestId'),false,'the request ID is the server-built messageId, not a parameter');
    assert.equal(Object.hasOwn(contact.properties,'payload'),false);
    for(const name of ['request_contact','send_message','decide_contact']){
        const schema=privateMcpTools.find(tool=>tool.name===name)!.inputSchema as any;
        const message=name==='decide_contact'?schema.properties.reply.anyOf[0]:schema.properties.message;
        assert.deepEqual(Object.keys(message.properties).sort(),['contextId','parts'],name+' never accepts messageId or role');
        assert.equal(message.properties.parts.maxItems,8);
        assert.deepEqual(Object.keys(message.properties.parts.items.properties).sort(),['data','mediaType','text']);
        const key=new RegExp(schema.properties.idempotencyKey.pattern);
        assert.ok(key.test('retry:1.a_b-c'));assert.equal(key.test('has space'),false);
        assert.equal(schema.required.includes('idempotencyKey'),false);
    }
    const send=privateMcpTools.find(tool=>tool.name==='send_message')!.inputSchema as any;
    assert.deepEqual(send.required,['recipientAccountId','message'],'contactGeneration is optional');
});

test('request_contact names the rejected field, writes nothing, and uses the server-built messageId as the request ID',async()=>{
    const h=await start();
    try{
        const a=await h.participant(),b=await h.participant();
        const env:Env={db:h.db,REGISTRATION_ENABLED:'true',REGISTRATION_MAX_ACCOUNTS:'100',
            REGISTRATION_PER_IP_PER_HOUR:'10',REGISTRATION_GLOBAL_PER_HOUR:'100',
            CLEANUP_MAX_MESSAGES:'0',CLEANUP_TIME_BUDGET_MS:'0'};
        const base={requesterAddressId:a.cardId,recipientAccountId:b.accountId,recipientAddressId:b.cardId};
        const rejected=async(args:Record<string,unknown>,field:string)=>{
            const response=await executePrivateMcp(env,origin,a.accountId,'request_contact',{...base,...args});
            assert.equal(response.status,400,field);
            const body=await response.json();
            assert.equal(body.field,field);
            assert.equal(typeof body.reason,'string');
            return body.reason as string;
        };
        const message={parts:[{text:'field-hints'}]};
        assert.match(await rejected({requestId:'01a187ea-9ff1-78ef-9bde-0aaf2ece3297',message},'requestId'),/not a parameter/);
        assert.match(await rejected({message:{...message,messageId:'01a187ea-9ff1-78ef-9bde-0aaf2ece3297'}},'message'),/server sets messageId/);
        await rejected({message:{parts:[]}},'message.parts');
        await rejected({message:{parts:[{text:'x',data:{}}]}},'message.parts[0]');
        await rejected({message,idempotencyKey:''},'idempotencyKey');
        await rejected({message,extra:true},'extra');
        const {recipientAddressId:_omitted,...missing}=base;
        const absent=await executePrivateMcp(env,origin,a.accountId,'request_contact',{...missing,message});
        assert.equal((await absent.json()).field,'recipientAddressId');
        const before=await executePrivateMcp(env,origin,a.accountId,'list_my_contact_requests',{});
        assert.deepEqual((await before.json()).items,[],'rejected calls wrote no request');
        const accepted=await executePrivateMcp(env,origin,a.accountId,'request_contact',{...base,message});
        assert.equal(accepted.status,200,await accepted.clone().text());
        const requestId=(await accepted.json()).request.requestId;
        const listed=await executePrivateMcp(env,origin,a.accountId,'list_my_contact_requests',{});
        assert.ok((await listed.json()).items.some((item:{requestId:string;requestMessageId?:string})=>item.requestId===requestId),'the request ID is the introduction messageId');
    }finally{await h.close();}
});

test('remote MCP turns a field rejection into a correctable tool error',async()=>{
    const access:PrivateAccess={tools:privateMcpTools,scope:privateScope,authorize:async()=>accountId,
        invoke:async()=>new Response(JSON.stringify({status:400,title:'Invalid request',code:'invalid',
            field:'requestId',reason:'must be a UUIDv7'}),{status:400,headers:{'Content-Type':'application/json'}})};
    const response=await remoteMcp(new Request(origin+'/mcp',{method:'POST',
        headers:{...headers,Authorization:'Bearer synthetic'},body:JSON.stringify(rpc('request_contact',{}))}),unused,access);
    const frame=await response.json();
    assert.equal(frame.result.isError,true);
    assert.deepEqual(frame.result.structuredContent,{status:400,field:'requestId',reason:'must be a UUIDv7'});
    assert.match(frame.result.content[0].text,/Invalid argument requestId: must be a UUIDv7\. Nothing was written/);
});
