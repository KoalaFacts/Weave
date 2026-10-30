import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createContext, runInContext } from 'node:vm';
import { start } from './harness.ts';

async function registry(h) {
    const html = await (await h.request('/registry')).text(), elements = new Map(), calls = [];
    const element = () => ({ textContent: '', value: '', children: [], disabled: false, handlers: {}, append(child) { this.children.push(child); }, replaceChildren(...children) { this.children = children; }, addEventListener(event, fn) { this.handlers[event] = fn; } });
    const get = id => { if (!elements.has(id)) elements.set(id, element()); return elements.get(id); };
    const context = createContext({ document: { getElementById: get, createElement: element }, fetch: (path, options) => { calls.push({ path, options }); return h.request(path); } });
    runInContext(/<script nonce="[^"]+">([\s\S]*?)<\/script>/.exec(html)[1], context);
    const settled = async () => {
        const deadline = Date.now() + 5000;
        while (get('status').textContent === 'Loading…') { assert.ok(Date.now() < deadline, 'Registry request did not settle'); await new Promise(resolve => setTimeout(resolve, 10)); }
    };
    await settled();
    return { get, calls, click: async id => { get(id).handlers.click(); await settled(); } };
}

test('registry browser script requests ten-item pages and follows the server cursor without duplicate rows', async () => {
    const h = await start();
    try {
        const account = await h.register(), expected = [];
        for (let index = 0; index < 11; index++) expected.push(await h.address(account, { label: 'Browse ' + index, isPublic: true }));
        for (let index=0;index<expected.length;index++) await h.db.prepare("UPDATE account_addresses SET created_at=? WHERE address_id=?").bind((BigInt(Date.UTC(2025,0,1,0,0,index))*10000n).toString(),expected[index].addressId).run();
        const page = await registry(h);
        assert.equal(new URL(page.calls[0].path, 'http://localhost').searchParams.get('limit'), '10');
        assert.equal(page.get('results').children.length, 10); assert.equal(page.get('next').disabled, false);
        const first = page.get('results').children.map(row => row.children[1].textContent);
        await page.click('next');
        assert.equal(page.get('results').children.length, 1); assert.equal(page.get('next').disabled, true);
        const all = [...first, page.get('results').children[0].children[1].textContent];
        assert.deepEqual(all, expected.map(item => 'Address ID: ' + item.addressId).reverse());
        assert.ok(page.calls.every(call => new URL(call.path, 'http://localhost').searchParams.get('limit') === '10'));
        assert.ok(page.calls.every(call => call.options.credentials === 'omit' && !call.options.headers));
    } finally { await h.close(); }
});

test('registry renders the actual rate retry delay, clears stale rows and recovers on a later request', async () => {
    const h = await start();
    try {
        const account = await h.register(); await h.address(account, { label: 'Public', isPublic: true });
        const page = await registry(h); assert.equal(page.get('results').children.length, 1);
        await h.db.prepare("UPDATE rate_limits SET count=2000,window_start=CAST(? AS INTEGER)*10000 WHERE policy='public-read' AND bucket_key='global'").bind(Date.now()).run();
        await page.click('find');
        assert.match(page.get('status').textContent, /^Too many registry requests\. Try again in [1-6]?\d seconds\.$/);
        assert.equal(page.get('results').children.length, 0); assert.equal(page.get('next').disabled, true);
        await h.db.prepare("UPDATE rate_limits SET window_start=CAST(? AS INTEGER)*10000 WHERE policy='public-read'").bind(Date.now() - 61000).run();
        await page.click('find');
        assert.equal(page.get('status').textContent, 'Public addresses shown below.'); assert.equal(page.get('results').children.length, 1);
    } finally { await h.close(); }
});

test('registry input validation agrees with the real address contract and correction restores lookup and browsing',async()=>{
    const h=await start();
    try{
        const account=await h.register(),address=await h.address(account,{label:'Real public address',isPublic:true});
        const page=await registry(h),before=JSON.stringify(page.get('results').children),count=page.calls.length;
        for(const invalid of ['not-an-address','ABCDEF12-1234-1234-1234-123456789abc','12345678123412341234123456789abc']){
            assert.equal((await h.request('/v1/directory/address?addressId='+encodeURIComponent(invalid))).status,400);
            page.get('address').value=invalid;await page.click('lookup');
            assert.equal(page.get('status').textContent,'Enter a valid public address ID (lowercase UUID), or use Browse all.');
            assert.equal(page.calls.length,count);assert.equal(page.get('retry').disabled,true);
            assert.equal(JSON.stringify(page.get('results').children),before);assert.equal(page.get('pageNumber').textContent,'Page 1');
        }
        for(const missing of ['00000000-0000-0000-0000-000000000000','12345678-1234-1234-1234-123456789abc','ffffffff-ffff-ffff-ffff-ffffffffffff']){
            assert.equal((await h.request('/v1/directory/address?addressId='+missing)).status,404);
            page.get('address').value=missing;await page.click('lookup');
            assert.equal(page.get('status').textContent,'Public address unavailable.');
            assert.equal(page.get('pageNumber').textContent,'Exact lookup');assert.equal(page.get('retry').disabled,true);
            assert.equal(page.get('results').children.length,0);
        }
        page.get('address').value=address.addressId;await page.click('lookup');
        assert.equal(page.get('results').children[0].children[1].textContent,'Address ID: '+address.addressId);
        assert.equal(page.get('pageNumber').textContent,'Exact lookup');
        await page.click('browse');assert.equal(page.get('address').value,'');assert.equal(page.get('pageNumber').textContent,'Page 1');
        assert.equal(page.get('results').children[0].children[0].textContent,'Real public address');
        assert.ok(page.calls.every(call=>call.options.credentials==='omit'&&!call.options.headers));
    }finally{await h.close();}
});
