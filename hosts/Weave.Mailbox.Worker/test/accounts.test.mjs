import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { start, secret, payload } from './harness.mjs';

test('registration creates zero addresses; control requires at least 64 chars without a custom character alphabet', async () => {
    const h=await start();
    try {
        const a=await h.register({},'a b,'.repeat(16));
        assert.deepEqual(Object.keys(a).sort(),['accountId','agentId','control','description','displayName']);
        assert.deepEqual(await h.ok('/v1/addresses',a.control),{items:[],nextCursor:null});
        for(const path of ['/inbox/','/outbox/']) assert.deepEqual(await h.ok(path+a.accountId,a.control),{items:[],nextCursor:null});
        for(const old of [{isPublic:true},{displayName:1},{description:'x'.repeat(1025)},{email:'a@test'}]) assert.equal((await h.request('/v1/accounts',secret(),'POST',old)).status,400);
        assert.equal((await h.request('/v1/accounts','x'.repeat(63),'POST',{})).status,401);
        const profile=await h.register({displayName:'private profile',description:'not in directory'});
        assert.equal(profile.displayName,'private profile');
        assert.deepEqual(await h.ok('/v1/accounts',profile.control,'POST',{displayName:'retry'}),{agentId:profile.agentId,displayName:'private profile',description:'not in directory'});
        const long=await h.register({},'z'.repeat(600)); assert.ok(long.accountId);
        assert.equal((await h.request('/v1/accounts',null,'POST',{})).status,401);
        assert.equal((await h.request('/v1/directory',secret())).status,401);
        assert.equal((await h.request('/v1/account/directory',a.control,'PUT',{isPublic:true})).status,404);
    } finally {await h.close();}
});
test('private addresses stay hidden through all public discovery paths and duplicate registration cannot publish them', async () => {
    const h=await start();
    try {
        const a=await h.register(),hidden=await h.address(a,{label:'secret'});
        assert.equal(hidden.isPublic,false);
        for(const path of ['/v1/directory','/v1/directory?q=secret','/v1/directory?q='+hidden.addressId,'/v1/contacts/cards']) assert.deepEqual(await h.ok(path),{items:[],nextCursor:null});
        for(const path of ['/v1/directory/address?addressId='+hidden.addressId,'/v1/contacts/card?cardId='+hidden.addressId]) assert.equal((await h.request(path)).status,404);
        assert.equal((await h.ok('/v1/contacts/card?cardId='+hidden.addressId,a.control)).ownerMailboxId,a.accountId);
        const pub=await h.ok('/v1/addresses/'+hidden.addressId,a.control,'PUT',{label:'Visible',isPublic:true,directUri:'https://example.test/contact'});
        assert.equal(pub.addressId,hidden.addressId);
        assert.deepEqual(await h.ok('/v1/directory/address?addressId='+pub.addressId),pub);
        await h.ok('/v1/addresses/'+pub.addressId,a.control,'PUT',{isPublic:false});
        assert.deepEqual(await h.ok('/v1/accounts',a.control,'POST',{}),{agentId:a.accountId,displayName:null,description:null});
        assert.deepEqual(await h.ok('/v1/directory'),{items:[],nextCursor:null});
        assert.deepEqual(await h.ok('/v1/contacts/cards'),{items:[],nextCursor:null});
        assert.equal((await h.request('/v1/directory/address?addressId='+pub.addressId)).status,404);
    } finally {await h.close();}
});
test('one account owns multiple server-assigned addresses; every mutation and owner lookup enforces ownership', async () => {
    const h=await start();
    try {
        const a=await h.register(),b=await h.register(),x=await h.address(a),y=await h.address(a,{label:'second'});
        assert.notEqual(x.addressId,y.addressId); assert.equal((await h.ok('/v1/addresses',a.control)).items.length,2);
        assert.deepEqual(await h.ok('/v1/addresses',b.control),{items:[],nextCursor:null});
        for(const method of ['GET','PUT','DELETE']) assert.equal((await h.request('/v1/addresses/'+x.addressId,b.control,method,method==='PUT'?{}:undefined)).status,404);
        for(const path of ['/inbox/','/outbox/']) assert.equal((await h.request(path+a.accountId,b.control)).status,404);
        assert.equal((await h.request('/v1/addresses',a.control,'POST',{addressId:x.addressId})).status,400);
        await h.ok('/v1/addresses/'+x.addressId,a.control,'DELETE');
        assert.equal((await h.request('/v1/addresses/'+x.addressId,a.control)).status,404);
        assert.equal((await h.ok('/v1/addresses',a.control)).items.length,1);
    } finally {await h.close();}
});
test('UTC scheduling hides future and expired addresses and denies their contact requests', async () => {
    const h=await start();
    try {
        const a=await h.register(),b=await h.register();
        const past=new Date(Date.now()-3600000).toISOString(),future=new Date(Date.now()+3600000).toISOString();
        const active=await h.address(a,{isPublic:true,effectiveFrom:past,effectiveTo:future});
        const pending=await h.address(a,{isPublic:true,effectiveFrom:future});
        const expired=await h.address(a,{isPublic:true,effectiveTo:past});
        assert.deepEqual((await h.ok('/v1/directory')).items,[active]);
        assert.equal((await h.ok('/v1/contacts/cards')).items.length,1);
        for(const address of [pending,expired]) {
            assert.equal((await h.request('/v1/directory/address?addressId='+address.addressId)).status,404);
            assert.equal((await h.request('/v1/contacts/card?cardId='+address.addressId)).status,404);
            const p=payload(); assert.equal((await h.request('/v1/contacts/requests',b.control,'POST',{requestId:p.messageId,cardId:address.addressId,methodId:'relay',payload:p})).status,404);
        }
        for(const profile of [{effectiveFrom:future,effectiveTo:future},{effectiveFrom:future,effectiveTo:past},{effectiveFrom:'2026-02-30T00:00:00Z'},
            {effectiveTo:'2026-01-01T00:00:00+00:00'},{isPublic:1},{label:1},{label:'x'.repeat(129)},{directUri:'javascript:alert(1)'},{directUri:'https://user:password@example.test'},{directUri:'relative/path'}])
            assert.equal((await h.request('/v1/addresses',a.control,'POST',profile)).status,400);
    } finally {await h.close();}
});
test('D1 concurrent registration is idempotent, capacity and rate limits persist across restart', async () => {
    const persist=await mkdtemp(join(tmpdir(),'weave-d1-')); let h=await start({persist,bindings:{REGISTRATION_PER_IP_PER_HOUR:'1'}});
    try {
        const control=secret(),[a,b]=await Promise.all([h.register({},control),h.register({},control)]); assert.deepEqual(a,b);
        const address=await h.address(a,{label:'persistent'});
        const stored=await h.db.prepare("SELECT data FROM mailbox_records WHERE kind='account'").all();
        assert.equal(stored.results.length,1); assert.equal(stored.results[0].data.includes(control),false);
        await h.close();h=await start({persist,migrate:false,bindings:{REGISTRATION_PER_IP_PER_HOUR:'1'}});
        assert.deepEqual(await h.ok('/v1/accounts',control,'POST',{}),{agentId:a.accountId,displayName:null,description:null});
        assert.deepEqual((await h.ok('/v1/addresses',control)).items,[address]);
        assert.equal((await h.request('/v1/accounts',secret(),'POST',{})).status,429);
        await h.close();h=await start({persist,migrate:false,bindings:{REGISTRATION_ENABLED:'false'}});
        assert.deepEqual(await h.ok('/v1/accounts',control,'POST',{}),{agentId:a.accountId,displayName:null,description:null});
        assert.equal((await h.request('/v1/accounts',secret(),'POST',{})).status,404);
    } finally {await h.close();await rm(persist,{recursive:true,force:true});}
});
test('directory and public-card cursors do not reveal private row counts', async () => {
    const h=await start();
    try {
        const a=await h.register(),hidden=await h.address(a,{label:'public-label'});
        const publicOne=await h.address(a,{label:'public-label',isPublic:true}); await h.address(a,{label:'public-label',isPublic:true});
        const first=await h.ok('/v1/directory?limit=1&q=public-label'); assert.equal(first.items.length,1); assert.equal(first.nextCursor.includes(hidden.addressId),false);
        const second=await h.ok('/v1/directory?limit=1&q=public-label&afterCursor='+encodeURIComponent(first.nextCursor));
        assert.equal(second.items.length,1); assert.equal(second.nextCursor,null); assert.notEqual(first.items[0].addressId,second.items[0].addressId);
        assert.equal((await h.request('/v1/directory?afterCursor='+encodeURIComponent(first.nextCursor))).status,400);
        const cards=await h.ok('/v1/contacts/cards?limit=1'); assert.equal(cards.items[0].cardId,publicOne.addressId); assert.equal(cards.nextCursor,'public-cards:cHVibGlj:1');
    } finally {await h.close();}
});
