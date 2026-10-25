import assert from 'node:assert/strict';
import { test } from 'node:test';
import { start } from './harness.ts';
const headers = {'Content-Type':'application/json',Accept:'application/json, text/event-stream'};
async function rpc(h,method,params,extra={}) {
    const response=await h.mf.dispatchFetch('http://localhost/mcp',{
        method:'POST',headers:{...headers,...extra},body:JSON.stringify({jsonrpc:'2.0',id:7,method,params})
    });
    assert.equal(response.status,200,await response.clone().text());return response.json();
}
const call=(h,name,args={})=>rpc(h,'tools/call',{name,arguments:args});
const read=(body)=>JSON.parse(body.result.content[0].text);
const rateCount=(h)=>h.db.prepare("SELECT count(*) n FROM rate_limits WHERE policy='public-read'").first('n');

test('remote MCP initializes and reads the real D1 public directory without account control',async()=>{
    const h=await start();
    try {
        const account=await h.register();
        const visible=await h.address(account,{label:'remote-public',isPublic:true});
        const privateAddress=await h.address(account,{label:'remote-private'});
        const expired=await h.address(account,{label:'remote-expired',isPublic:true,effectiveTo:'2020-01-01T00:00:00Z'});
        const revoked=await h.address(account,{label:'remote-revoked',isPublic:true});
        await h.ok('/v1/addresses/'+revoked.addressId,account.control,'DELETE');
        const init=await rpc(h,'initialize',{protocolVersion:'2025-11-25',capabilities:{},clientInfo:{name:'fixture',version:'1'}});
        assert.equal(init.result.serverInfo.name,'weave-public');
        assert.deepEqual((await rpc(h,'tools/list')).result.tools.map(x=>x.name),['demo_guide','search_registry','lookup_address']);
        assert.equal(await rateCount(h),0,'handshake must not create account/rate state');
        const found=read(await call(h,'search_registry',{q:'remote-',limit:10}));
        assert.deepEqual(found.items.map(x=>x.addressId),[visible.addressId]);
        assert.equal(read(await call(h,'lookup_address',{addressId:visible.addressId})).addressId,visible.addressId);
        for(const item of [privateAddress,expired,revoked]){
            assert.equal((await call(h,'lookup_address',{addressId:item.addressId})).result.isError,true);
        }
        assert.ok(await rateCount(h)>0,'public MCP reads share the canonical directory rate limiter');
        assert.equal((await call(h,'send_message',{})).error.code,-32602);
    } finally { await h.close(); }
});
test('remote MCP reuses public paging and preserves the opaque cursor',async()=>{
    const h=await start();
    try {
        const account=await h.register();
        const first=await h.address(account,{label:'paged',isPublic:true});
        const second=await h.address(account,{label:'paged',isPublic:true});
        const page=read(await call(h,'search_registry',{q:'paged',limit:1}));
        assert.equal(page.items.length,1);assert.equal(typeof page.nextCursor,'string');
        const tail=read(await call(h,'search_registry',{q:'paged',limit:1,afterCursor:page.nextCursor}));
        assert.equal(tail.items.length,1);assert.equal(tail.nextCursor,null);
        assert.deepEqual(new Set([page.items[0].addressId,tail.items[0].addressId]),new Set([first.addressId,second.addressId]));
    } finally { await h.close(); }
});
test('remote public reads remain read-only during recovery',async()=>{
    const h=await start({bindings:{RECOVERY_READ_ONLY:'true'}});
    try {
        assert.deepEqual(read(await call(h,'search_registry')).items,[]);
        assert.equal(await rateCount(h),0,'recovery must not write rate records');
        assert.equal((await call(h,'register_account')).error.code,-32602);
    } finally { await h.close(); }
});
test('maintenance and Origin checks also guard the actual MCP route',async()=>{
    const h=await start();
    try {
        const response=await h.mf.dispatchFetch('http://localhost/mcp',{method:'POST',headers:{...headers,Origin:'https://evil.invalid'},body:JSON.stringify({jsonrpc:'2.0',id:1,method:'tools/list'})});
        assert.equal(response.status,403);assert.equal(await rateCount(h),0);
        assert.equal((await h.mf.dispatchFetch('http://localhost/mcp')).status,405);
    } finally { await h.close(); }
    const maintenance=await start({bindings:{MAINTENANCE_MODE:'true'}});
    try {
        const response=await maintenance.mf.dispatchFetch('http://localhost/mcp',{method:'POST',headers,body:JSON.stringify({jsonrpc:'2.0',id:1,method:'tools/list'})});
        assert.equal(response.status,503);
    } finally { await maintenance.close(); }
});
