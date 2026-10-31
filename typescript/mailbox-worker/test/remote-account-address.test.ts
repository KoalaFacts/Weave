import assert from 'node:assert/strict';
import {test} from 'node:test';
import {build} from 'esbuild';
import {start,payload} from './harness.ts';
import {protectedResourceMetadata} from '../src/remote-oauth.ts';
import type {Env} from '../src/database.ts';

const built=await build({entryPoints:['src/remote-private.ts'],bundle:true,write:false,platform:'node',format:'esm',logLevel:'silent'});
const {executePrivateMcp,privateScope,privateMcpTools}=await import('data:text/javascript;base64,'+Buffer.from(built.outputFiles[0].contents).toString('base64')) as typeof import('../src/remote-private.ts');
const origin='https://demo.test';
const env=(db:Env['db']):Env=>({db,REGISTRATION_ENABLED:'true',REGISTRATION_MAX_ACCOUNTS:'100',
    REGISTRATION_PER_IP_PER_HOUR:'10',REGISTRATION_GLOBAL_PER_HOUR:'100',
    CLEANUP_MAX_MESSAGES:'0',CLEANUP_TIME_BUDGET_MS:'0'});

test('remote onboarding tools declare explicit independent address scopes',async()=>{
    assert.equal(privateScope('list_my_addresses'),'weave:addresses.read');
    assert.equal(privateScope('ensure_my_address'),'weave:addresses.write');
    assert.equal(privateScope('list_my_contact_requests'),'weave:contacts.read');
    const names=new Set(privateMcpTools.map(tool=>tool.name));
    for(const name of ['list_my_addresses','ensure_my_address','list_my_contact_requests'])assert.ok(names.has(name));
    const metadata=await protectedResourceMetadata({issuer:'https://auth.example',resource:origin+'/mcp',
        introspectionUrl:'https://auth.example/oauth/introspect',clientId:'test-client',clientSecret:'synthetic_test_secret'}).json();
    assert.ok(metadata.scopes_supported.includes('weave:addresses.read'));
    assert.ok(metadata.scopes_supported.includes('weave:addresses.write'));
});

test('real D1 account-owned address creation is private, replayable and cross-account isolated',async()=>{
    const h=await start();
    try{
        const e=env(h.db),a=await h.register(),b=await h.register(),addressId=crypto.randomUUID();
        const args={addressId,label:'Private A'};
        const created=await executePrivateMcp(e,origin,a.accountId,'ensure_my_address',args);
        assert.equal(created.status,200,await created.clone().text());
        const createdBody=await created.json();
        assert.deepEqual(createdBody,{addressId,accountId:a.accountId,label:'Private A',isPublic:false,
            effectiveFrom:null,effectiveTo:null,directUri:null});
        const stored=await h.ok('/v1/addresses',a);
        assert.ok(stored.items.some((item:{addressId:string})=>item.addressId===addressId));
        const revision=await h.db.prepare('SELECT token FROM mailbox_revision WHERE singleton=1').first('token');
        const replay=await executePrivateMcp(e,origin,a.accountId,'ensure_my_address',args);
        assert.equal(replay.status,200);
        assert.deepEqual(await replay.json(),createdBody);
        assert.equal(await h.db.prepare('SELECT token FROM mailbox_revision WHERE singleton=1').first('token'),
            revision,'an exact replay must not mutate the database');
        const changed=await executePrivateMcp(e,origin,a.accountId,'ensure_my_address',{...args,label:'Changed'});
        assert.equal(changed.status,409,'same creation ID cannot silently update an address');
        const other=await executePrivateMcp(e,origin,b.accountId,'ensure_my_address',args);
        assert.equal(other.status,404,'foreign creation IDs cannot transfer ownership');
        const bad=await executePrivateMcp(e,origin,a.accountId,'ensure_my_address',{...args,addressId:crypto.randomUUID(),isPublic:true});
        assert.equal(bad.status,400,'public exposure requires a separate explicit operation');
        assert.equal(await h.db.prepare('SELECT token FROM mailbox_revision WHERE singleton=1').first('token'),revision,
            'denied mutations must not change D1 revision');
        const leaked=await h.request('/v1/directory/address?addressId='+addressId);
        assert.equal(leaked.status,404,'the onboarding tool must never implicitly publish an address');
        const own=await executePrivateMcp(e,origin,a.accountId,'list_my_addresses',{limit:1});
        assert.equal(own.status,200);
        assert.ok((await own.json()).items.some((item:{addressId:string})=>item.addressId===addressId));
        const foreign=await executePrivateMcp(e,origin,b.accountId,'list_my_addresses',{});
        assert.equal(foreign.status,200);
        assert.deepEqual((await foreign.json()).items,[]);
    }finally{await h.close();}
});

test('two OAuth-account-owned private addresses support a full contact to ACK workflow',async()=>{
    const h=await start();
    try{
        const e=env(h.db),a=await h.register(),b=await h.register(),c=await h.register();
        const aId=crypto.randomUUID(),bId=crypto.randomUUID();
        for(const [agent,addressId] of [[a,aId],[b,bId]] as const){
            const response=await executePrivateMcp(e,origin,agent.accountId,'ensure_my_address',{addressId,label:'Private introduction'});
            assert.equal(response.status,200,await response.clone().text());
        }
        const intro=payload('opaque introduction');
        const requested=await executePrivateMcp(e,origin,a.accountId,'request_contact',{
            requesterAddressId:aId,recipientAccountId:b.accountId,recipientAddressId:bId,payload:intro
        });
        assert.equal(requested.status,200,await requested.clone().text());
        const state=await requested.json();
        const inboxRequests=await executePrivateMcp(e,origin,b.accountId,'list_my_contact_requests',{});
        assert.equal(inboxRequests.status,200);
        assert.ok((await inboxRequests.json()).items.some((r:{requestId:string,status:string})=>r.requestId===intro.messageId&&r.status==='pending'));
        const forbidden=await executePrivateMcp(e,origin,c.accountId,'list_my_contact_requests',{});
        assert.equal(forbidden.status,200);
        assert.deepEqual((await forbidden.json()).items,[]);
        const accepted=await executePrivateMcp(e,origin,b.accountId,'decide_contact',{
            requestId:intro.messageId,requesterAccountId:a.accountId,expectedGeneration:String(state.generation),status:'accepted'
        });
        assert.equal(accepted.status,200,await accepted.clone().text());
        const message=payload('first end-to-end message');
        const sent=await executePrivateMcp(e,origin,a.accountId,'send_message',{
            recipientAccountId:b.accountId,contactGeneration:String(state.generation),payload:message
        });
        assert.equal(sent.status,200,await sent.clone().text());
        const received=await executePrivateMcp(e,origin,b.accountId,'list_my_inbox',{});
        assert.equal(received.status,200);
        assert.ok((await received.json()).items.some((item:{envelope:{payload:{messageId:string}}})=>item.envelope?.payload?.messageId===message.messageId));
        const receipt=await executePrivateMcp(e,origin,b.accountId,'ack_message',{messageId:message.messageId,senderAccountId:a.accountId});
        assert.equal(receipt.status,200,await receipt.clone().text());
        assert.equal((await receipt.json()).state,'acknowledged');
    }finally{await h.close();}
});
