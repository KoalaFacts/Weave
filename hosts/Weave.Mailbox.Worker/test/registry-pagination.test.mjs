import assert from 'node:assert/strict';
import { test } from 'node:test';
import { start } from './harness.mjs';

for (const count of [0, 10, 11, 23]) test(`anonymous directory browses ${count} public addresses in stable ten-item pages`, async () => {
    const h = await start();
    try {
        const owner = await h.register({ displayName: 'PRIVATE-PROFILE', description: 'PRIVATE-DESCRIPTION' });
        const visible = [];
        for (let index = 0; index < count; index++) visible.push(await h.address(owner, { label: `Public ${index}`, isPublic: true }));
        for (let index=0;index<visible.length;index++) await h.db.prepare("UPDATE account_addresses SET created_at=? WHERE address_id=?").bind((BigInt(Date.UTC(2025,0,1,0,0,index))*10000n).toString(),visible[index].addressId).run();
        const hidden = [];
        for (const profile of [{ label: 'PRIVATE' }, { label: 'FUTURE', isPublic: true, effectiveFrom: '2099-01-01T00:00:00Z' }, { label: 'EXPIRED', isPublic: true, effectiveTo: '2020-01-01T00:00:00Z' }]) hidden.push(await h.address(owner, profile));
        const removed = await h.address(owner, { label: 'REMOVED', isPublic: true });
        await h.ok('/v1/addresses/' + removed.addressId, owner.control, 'DELETE');
        hidden.push(removed);
        const seen = [], sizes = [];
        let cursor = null;
        do {
            const page = await h.ok('/v1/directory' + (cursor ? '?afterCursor=' + encodeURIComponent(cursor) : ''));
            sizes.push(page.items.length); seen.push(...page.items.map(item => item.addressId));
            for (const item of page.items) assert.deepEqual(Object.keys(item).sort(), ['accountId', 'addressId', 'directUri', 'effectiveFrom', 'effectiveTo', 'isPublic', 'label']);
            assert.ok(!JSON.stringify(page).includes(owner.control));
            assert.ok(!JSON.stringify(page).includes('PRIVATE-'));
            cursor = page.nextCursor;
            if (cursor) { assert.ok(cursor.startsWith('directory-created-v1::')); assert.ok(cursor.endsWith('/'+page.items.at(-1).addressId)); }
            assert.ok(sizes.length <= 3, 'Pagination must terminate.');
        } while (cursor);
        assert.deepEqual(sizes, count === 0 ? [0] : count === 10 ? [10] : count === 11 ? [10, 1] : [10, 10, 3]);
        assert.deepEqual(seen, visible.map(item => item.addressId).reverse());
        assert.equal(new Set(seen).size, count);
        for (const item of hidden) assert.equal((await h.request('/v1/directory/address?addressId=' + item.addressId)).status, 404);
        for (const query of ['limit=11', 'limit=100', 'limit=0', 'limit=-1', 'limit=1&limit=10']) assert.equal((await h.request('/v1/directory?' + query)).status, 400);
    } finally { await h.close(); }
});

test('directory rejects malformed and wrong-scope cursors before returning public rows', async () => {
    const h = await start();
    try {
        const owner = await h.register(), visible = await h.address(owner, { label: 'Visible', isPublic: true });
        for (const cursor of ['directory::' + '-'.repeat(36), 'directory::' + 'a'.repeat(36), 'directory::AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA', 'directory:' + btoa('another search') + ':' + visible.addressId, 'addresses::' + visible.addressId]) {
            assert.equal((await h.request('/v1/directory?afterCursor=' + encodeURIComponent(cursor))).status, 400, cursor);
        }
        assert.equal((await h.request('/v1/directory?afterCursor=directory%3A%3A' + visible.addressId + '&afterCursor=duplicate')).status, 400);
        assert.deepEqual((await h.ok('/v1/directory?afterCursor=' + encodeURIComponent('directory-created-v1::1900-01-01T00:00:00.000Z/' + visible.addressId))).items, []);
    } finally { await h.close(); }
});

test('public rate denial returns the longest blocked window and recovers without charging rejected reads', async () => {
    const h = await start();
    const request = (path = '/v1/directory', ip = '192.0.2.40') => h.mf.dispatchFetch('http://localhost' + path, { headers: { 'CF-Connecting-IP': ip } });
    try {
        assert.equal((await request()).status, 200);
        const now = Date.now();
        await h.db.prepare("UPDATE rate_limits SET count=CASE WHEN bucket_key='global' THEN 2000 ELSE 120 END,window_start=CAST(? AS INTEGER)*10000 WHERE policy='public-read'").bind(now - 10000).run();
        await h.db.prepare("UPDATE rate_limits SET window_start=CAST(? AS INTEGER)*10000 WHERE policy='public-read' AND bucket_key='global'").bind(now - 55000).run();
        const before = (await h.db.prepare("SELECT bucket_key,count,CAST(window_start AS TEXT) window_start FROM rate_limits WHERE policy='public-read' ORDER BY bucket_key").all()).results;
        for (const route of ['/v1/directory', '/v1/directory/address?addressId=00000000-0000-0000-0000-000000000000', '/v1/contacts/cards', '/v1/contacts/card?cardId=missing']) {
            const response = await request(route);
            assert.equal(response.status, 429);
            const retry = Number(response.headers.get('Retry-After'));
            assert.ok(Number.isInteger(retry) && retry >= 45 && retry <= 50, 'Retry-After must cover both blocked windows: ' + retry);
            assert.deepEqual(await response.json(), { status: 429, title: 'Capacity reached', code: 'capacity' });
        }
        assert.deepEqual((await h.db.prepare("SELECT bucket_key,count,CAST(window_start AS TEXT) window_start FROM rate_limits WHERE policy='public-read' ORDER BY bucket_key").all()).results, before);
        const otherSource = await request('/v1/directory', '192.0.2.41');
        assert.equal(otherSource.status, 429);
        assert.ok(Number(otherSource.headers.get('Retry-After')) >= 1 && Number(otherSource.headers.get('Retry-After')) <= 5);
        await h.db.prepare("UPDATE rate_limits SET window_start=CAST(? AS INTEGER)*10000 WHERE policy='public-read'").bind(Date.now() - 61000).run();
        const recovered = await request(); assert.equal(recovered.status, 200); assert.equal(recovered.headers.get('Retry-After'), null);
        const counters = (await h.db.prepare("SELECT count FROM rate_limits WHERE policy='public-read'").all()).results;
        assert.ok(counters.every(row => row.count === 1));
    } finally { await h.close(); }
});

test('concurrent anonymous reads cannot cross the per-source threshold', async () => {
    const h = await start();
    try {
        const request = () => h.request('/v1/directory');
        await request();
        await h.db.prepare("UPDATE rate_limits SET count=119 WHERE policy='public-read' AND bucket_key!='global'").run();
        const responses = await Promise.all([request(), request()]);
        assert.deepEqual(responses.map(response => response.status).sort(), [200, 429]);
        assert.ok(Number(responses.find(response => response.status === 429).headers.get('Retry-After')) > 0);
    } finally { await h.close(); }
});
