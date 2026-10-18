import assert from 'node:assert/strict';
import { test } from 'node:test';
import { start, secret } from './harness.ts';
import { mcpClient, value } from './mcp-client.ts';

test('MCP lists paginated owner address history only when includeRevoked is requested',async()=>{
    const h=await start(),control=secret(),client=mcpClient(String(await h.mf.ready),control);
    try{
        await client.initialize();
        const owner=await h.register({},control),peer=await h.register();
        const addresses=[await h.address(owner,{isPublic:true}),await h.address(owner,{isPublic:true})]
            .sort((a,b)=>a.addressId.localeCompare(b.addressId));
        const [revoked,active]=addresses,foreign=await h.address(peer);
        await h.ok('/v1/addresses/'+revoked.addressId,control,'DELETE');
        await h.ok('/v1/addresses/'+foreign.addressId,peer.control,'DELETE');
        const before=(await h.db.prepare('SELECT * FROM account_addresses ORDER BY address_id').all()).results;
        const expected=await h.ok('/v1/addresses?includeRevoked=true&limit=1',control);
        assert.equal(expected.items[0].addressId,revoked.addressId);assert.ok(expected.items[0].revokedAt);
        for(const args of [{},{includeRevoked:false}])
            assert.deepEqual(value(await client.call('list_addresses',args)).items,[active]);

        const result=await client.call('list_addresses',{includeRevoked:true,limit:1});
        assert.equal(result.isError,false,'The MCP client must be able to request existing HTTP address history.');
        const first=value(result);assert.deepEqual(first,expected);assert.ok(first.nextCursor);
        const second=value(await client.call('list_addresses',{includeRevoked:true,limit:1,afterCursor:first.nextCursor}));
        assert.equal(second.nextCursor,null);assert.deepEqual(second.items.map(x=>x.addressId),[active.addressId]);
        assert.equal(second.items[0].revokedAt,null);assert.ok(second.items[0].createdAt);
        for(const item of [...first.items,...second.items])assert.equal(item.accountId,owner.accountId);
        assert.deepEqual(value(await client.call('search_registry')).items.map(x=>x.addressId),[active.addressId]);
        assert.deepEqual((await h.db.prepare('SELECT * FROM account_addresses ORDER BY address_id').all()).results,before);
        const advertised=(await client.request('tools/list')).result.tools.find(x=>x.name==='list_addresses');
        assert.equal(advertised.inputSchema.properties.includeRevoked.type,'boolean');assert.equal(advertised.annotations.readOnlyHint,true);
        assert.ok(!client.logs.includes(control));
    }finally{await client.close();await h.close();}
});

test('MCP address history preserves authentication and boolean argument boundaries',async()=>{
    const h=await start(),control=secret(),client=mcpClient(String(await h.mf.ready),control),anonymous=mcpClient(String(await h.mf.ready)),wrong=mcpClient(String(await h.mf.ready),secret());
    try{
        await client.initialize();await anonymous.initialize();await wrong.initialize();await h.register({},control);
        for(const [reader,reason] of [[anonymous,/must be supplied by the operator/],[wrong,/Demo HTTP 401/]]){
            const result=await reader.call('list_addresses',{includeRevoked:true});
            assert.equal(result.isError,true);assert.match(result.content[0].text,reason);
        }
        for(const args of [{includeRevoked:'true'},{includeRevoked:1},{includeRevoked:null},{includeRevoked:true,accountId:'another-account'}]){
            const result=await client.call('list_addresses',args);
            assert.equal(result.isError,true);assert.match(result.content[0].text,/Invalid tool arguments.*No request was sent/);
        }
    }finally{await client.close();await anonymous.close();await wrong.close();await h.close();}
});
