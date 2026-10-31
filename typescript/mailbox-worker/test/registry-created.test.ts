import assert from 'node:assert/strict';
import { test } from 'node:test';
import { start } from './harness.ts';
async function at(h,owner,time,profile={}){
    const a=await h.address(owner,{isPublic:true,...profile});
    await h.db.prepare("UPDATE account_addresses SET created_at=? WHERE address_id=?").bind((BigInt(Date.parse(time))*10000n).toString(),a.addressId).run();return a;
}
const page=(h,cursor,limit=2,q='')=>h.ok('/v1/directory?limit='+limit+'&q='+encodeURIComponent(q)+(cursor?'&afterCursor='+encodeURIComponent(cursor):''));
const ids=p=>p.items.map(x=>x.addressId);
test('directory creation order is newest first with a deterministic ID tie-break and original time survives edits',async()=>{
    const h=await start();try{
        const owner=await h.register();
        const older=await at(h,owner,'2025-01-01T00:00:00.000Z',{label:'older'});
        const a=await at(h,owner,'2025-01-02T00:00:00.000Z',{label:'same time A'});
        const b=await at(h,owner,'2025-01-02T00:00:00.000Z',{label:'same time B'});
        const expected=[a.addressId,b.addressId].sort().reverse().concat(older.addressId);
        const first=await page(h);assert.deepEqual(ids(first),expected.slice(0,2));
        const second=await page(h,first.nextCursor);assert.deepEqual(ids(second),expected.slice(2));assert.equal(second.nextCursor,null);
        await h.ok('/v1/addresses/'+older.addressId,owner,'PUT',{isPublic:false,label:'edited'});
        await h.ok('/v1/addresses/'+older.addressId,owner,'PUT',{isPublic:true,label:'edited'});
        assert.deepEqual(ids(await page(h,null,10)),expected);
    }finally{await h.close();}
});
test('directory keyset continues after a deleted boundary while a new address appears only on refreshing the front',async()=>{
    const h=await start();try{
        const owner=await h.register(),items=[];
        for(let i=1;i<=5;i++)items.push(await at(h,owner,`2025-01-0${i}T00:00:00.000Z`));
        const first=await page(h);assert.deepEqual(ids(first),items.slice(3).reverse().map(a=>a.addressId));
        const newest=await at(h,owner,'2025-01-06T00:00:00.000Z');
        await h.ok('/v1/addresses/'+items[3].addressId,owner,'DELETE');
        const second=await page(h,first.nextCursor);assert.deepEqual(ids(second),[items[2].addressId,items[1].addressId]);
        await h.ok('/v1/addresses/'+items[0].addressId,owner,'PUT',{isPublic:false});
        assert.deepEqual(ids(await page(h,second.nextCursor)),[]);
        assert.equal(ids(await page(h))[0],newest.addressId);
        assert.equal((await h.request('/v1/directory/address?addressId='+items[0].addressId)).status,404);
    }finally{await h.close();}
});
test('directory creation cursors are query-bound and reject old, malformed or invalid-time boundaries',async()=>{
    const h=await start();try{
        const owner=await h.register();
        for(let i=1;i<=3;i++)await at(h,owner,`2025-01-0${i}T00:00:00.000Z`,{label:'Public 梅'});
        const first=await page(h,null,2,'Public 梅');assert.ok(first.nextCursor);
        assert.equal((await h.request('/v1/directory?afterCursor='+encodeURIComponent(first.nextCursor))).status,400);
        assert.equal((await page(h,first.nextCursor,2,'Public 梅')).items.length,1);
        const id=first.items.at(-1).addressId,prefix='directory-created-v1::';
        for(const token of ['directory::'+id,prefix+'invalid/'+id,prefix+'2025-02-31T00:00:00.000Z/'+id,prefix+'2025-01-01T00:00:00.000Z/NOT-A-UUID',prefix+'2025-01-01T00:00:00.000Z/'+id+'/extra']){
            assert.equal((await h.request('/v1/directory?afterCursor='+encodeURIComponent(token))).status,400,token);
        }
    }finally{await h.close();}
});
