import test from 'node:test';
import assert from 'node:assert/strict';
import {inspectTokenManagement} from './token-management.ts';

const account = 'a'.repeat(32), id = 'b'.repeat(32), group = 'c'.repeat(32);
const env = {CLOUDFLARE_ACCOUNT_ID: account, CLOUDFLARE_API_TOKEN: 'synthetic-existing-token'};
const appsEdit = 'd'.repeat(32), appsWrite = 'e'.repeat(32), organizationRead = 'f'.repeat(32);
const accessNames = [
    'Access: Apps and Policies Edit', 'Access: Apps and Policies Write',
    'Access: Organizations, Identity Providers, and Groups Read'
];
const json = (result: unknown) => Response.json({success: true, result});
function fixture(kind = 'user', denied = false, write = true) {
    const root = kind === 'user' ? '/user/tokens' : '/accounts/' + account + '/tokens';
    const calls: string[] = [];
    const fetchImpl: typeof fetch = async (input, init = {}) => {
        const url = new URL(String(input));
        assert.equal(url.origin, 'https://api.cloudflare.com');
        assert.equal(init.method, 'GET'); assert.equal(init.redirect, 'manual');
        assert.equal(init.body, undefined); assert.ok(init.signal);
        assert.equal(new Headers(init.headers).get('Authorization'), 'Bearer ' + env.CLOUDFLARE_API_TOKEN);
        const path = url.pathname.slice('/client/v4'.length) + url.search; calls.push(path);
        if (path === '/accounts/' + account + '/workers/subdomain') return json({subdomain: 'weave-389'});
        if (path.endsWith('/verify')) return path === root + '/verify' ? json({id, status: 'active'}) : new Response(null, {status: 403});
        if (path === root + '/' + id) return denied ? new Response(env.CLOUDFLARE_API_TOKEN, {status: 403}) : json({
            id, status: 'active', name: 'private-token-name', policies: [{effect: 'allow', permission_groups: [{id: group}],
                resources: {[kind === 'user' ? 'com.cloudflare.api.user.*' : 'com.cloudflare.api.account.' + account]: '*'}}]
        });
        if (path === root + '/permission_groups?name=' + encodeURIComponent(kind === 'user' ? 'API Tokens Write' : 'Account API Tokens Write')) return json([{id: group,
            name: (kind === 'user' ? 'API Tokens ' : 'Account API Tokens ') + (write ? 'Write' : 'Read'),
            scopes: [kind === 'user' ? 'com.cloudflare.api.user' : 'com.cloudflare.api.account']}]);
        if (path.startsWith(root + '/permission_groups?name=')) {
            const name = new URL('https://api.cloudflare.com' + path).searchParams.get('name');
            const ids: Record<string, string> = {
                [accessNames[0]]: appsEdit, [accessNames[1]]: appsWrite, [accessNames[2]]: organizationRead
            };
            return json(name && ids[name] ? [{id: ids[name], name, scopes: ['com.cloudflare.api.account']}] : []);
        }
        throw Error('Unexpected request');
    };
    return {root, calls, fetchImpl};
}
for (const kind of ['user', 'account']) test(kind + ' token metadata is inspected through exact GET-only endpoints without printing identifiers', async () => {
    const f = fixture(kind);
    const report = await inspectTokenManagement({env, fetchImpl: f.fetchImpl});
    assert.equal(report.outcome, 'readable'); assert.equal(report.tokenKind, kind);
    assert.equal(report.managementWriteDeclared, true); assert.equal(report.writePermissionVerified, false);
    assert.equal(report.readOnly, true);
    assert.deepEqual(f.calls, ['/accounts/' + account + '/workers/subdomain', '/user/tokens/verify',
        ...(kind === 'account' ? [f.root + '/verify'] : []), f.root + '/' + id,
        f.root + '/permission_groups?name=' + encodeURIComponent(kind === 'user' ? 'API Tokens Write' : 'Account API Tokens Write'),
        ...accessNames.map(name => f.root + '/permission_groups?name=' + encodeURIComponent(name))]);
    for (const sensitive of [account, id, env.CLOUDFLARE_API_TOKEN, 'private-token-name'])
        assert.equal(JSON.stringify(report).includes(sensitive), false);
});
test('token details 403 is reported as its own permission boundary, not as Access app permission evidence', async () => {
    const f = fixture('user', true);
    const result = await inspectTokenManagement({env, fetchImpl: f.fetchImpl});
    assert.equal(result.outcome, 'blocked'); assert.equal(result.stage, 'token-details');
    assert.equal(result.httpStatus, 403); assert.equal(result.readOnly, true); assert.equal(f.calls.length, 3);
    assert.equal(JSON.stringify(result).includes(env.CLOUDFLARE_API_TOKEN), false);
});
test('read permission does not become declared write permission', async () => {
    const f = fixture('user', false, false);
    const result = await inspectTokenManagement({env, fetchImpl: f.fetchImpl});
    assert.equal(result.outcome, 'readable'); assert.equal(result.managementWriteDeclared, false);
    assert.equal(result.writePermissionVerified, false); assert.equal(f.calls.length, 7);
});
test('absent existing credential makes zero requests and never selects another secret', async () => {
    let calls = 0;
    const result = await inspectTokenManagement({env: {CLOUDFLARE_ACCOUNT_ID: account},
        fetchImpl: async () => {calls++; throw Error('Unexpected');}});
    assert.equal(result.outcome, 'blocked'); assert.equal(calls, 0);
});
test('redirects, failures, malformed, oversized and secret-bearing stream errors are sanitized without retries', async () => {
    for (const response of [() => new Response(null, {status: 302}), () => new Response(env.CLOUDFLARE_API_TOKEN, {status: 429}),
        () => new Response(env.CLOUDFLARE_API_TOKEN), () => new Response('x'.repeat(300000)),
        () => new Response(new ReadableStream({start(c) {c.error(Error(env.CLOUDFLARE_API_TOKEN));}}))]) {
        let calls = 0;
        const result = await inspectTokenManagement({env, fetchImpl: async () => {calls++; return response();}});
        assert.equal(result.outcome, 'blocked'); assert.equal(calls, 1);
        assert.equal(JSON.stringify(result).includes(env.CLOUDFLARE_API_TOKEN), false);
    }
});
test('wrong Weave account cannot reach token metadata', async () => {
    let calls = 0;
    const result = await inspectTokenManagement({env, fetchImpl: async () => {calls++; return json({subdomain: 'other'});}});
    assert.equal(result.outcome, 'blocked'); assert.equal(result.stage, 'account-identity'); assert.equal(calls, 1);
});


for (const kind of ['user', 'account']) test(kind + ' filters a paginated token permission catalogue to avoid a false missing-write result', async () => {
    const root = kind === 'user' ? '/user/tokens' : '/accounts/' + account + '/tokens';
    const name = kind === 'user' ? 'API Tokens Write' : 'Account API Tokens Write';
    const calls: string[] = [];
    const fetchImpl: typeof fetch = async (input, init = {}) => {
        const url = new URL(String(input));
        const path = url.pathname.slice('/client/v4'.length) + url.search;
        calls.push(path);
        assert.equal(init.method, 'GET');
        assert.equal(init.redirect, 'manual');
        assert.equal(new Headers(init.headers).get('Authorization'), 'Bearer ' + env.CLOUDFLARE_API_TOKEN);
        if (path === '/accounts/' + account + '/workers/subdomain') return json({subdomain:'weave-389'});
        if (path === '/user/tokens/verify' && kind === 'account') return new Response(null, {status:403});
        if (path === root + '/verify') return json({id, status:'active'});
        if (path === root + '/' + id) return json({id, status:'active',
            policies:[{effect:'allow', permission_groups:[{id:group}]}]});
        if (path === root + '/permission_groups') return json(Array.from({length:20}, (_, i) => ({
            id:String(i).padStart(32,'0'), name:'Unrelated '+i, scopes:['com.cloudflare.api.user']
        })));
        if (path === root + '/permission_groups?name=' + encodeURIComponent(name)) return json([{
            id:group, name, scopes:[kind === 'user' ? 'com.cloudflare.api.user' : 'com.cloudflare.api.account']
        }]);
        if (path.startsWith(root + '/permission_groups?name=')) return json([{
            id: path.includes(encodeURIComponent(accessNames[2])) ? organizationRead : appsWrite,
            name: path.includes(encodeURIComponent(accessNames[2])) ? accessNames[2] : accessNames[1],
            scopes: ['com.cloudflare.api.account']
        }]);
        throw Error('Unexpected token inspection request ' + path);
    };
    const result = await inspectTokenManagement({env, fetchImpl});
    assert.equal(result.outcome, 'readable');
    assert.equal(result.managementWriteDeclared, true);
    assert.equal(result.writePermissionVerified, false);
    assert.ok(calls.includes(root + '/permission_groups?name=' + encodeURIComponent(name)));
    assert.equal(calls.includes(root + '/permission_groups'), false);
});

test('the existing account token reports its Access policy for Weave, not the owner broad ChatGPT permission', async () => {
    for (const [effect, resource, appsExpected, orgExpected] of [
        ['allow', 'com.cloudflare.api.account.' + account, 'allow', 'allow'],
        ['deny', 'com.cloudflare.api.account.' + account, 'deny', 'deny'],
        ['allow', 'com.cloudflare.api.account.' + '9'.repeat(32), 'absent', 'absent'],
        ['allow', 'com.cloudflare.api.account.*', 'allow', 'allow']
    ] as const) {
        const f = fixture('account');
        const fetchImpl: typeof fetch = async (input, init = {}) => {
            if (new URL(String(input)).pathname.endsWith('/tokens/' + id)) return json({
                id, status: 'active', name: 'unlogged-token',
                policies: [{effect, resources: {[resource]: '*'},
                    permission_groups: [{id: appsWrite}, {id: organizationRead}]}]
            });
            return f.fetchImpl(input, init);
        };
        const report = await inspectTokenManagement({env, fetchImpl});
        assert.equal(report.outcome, 'readable');
        assert.equal(report.accessAppsPolicy, appsExpected);
        assert.equal(report.accessOrganizationPolicy, orgExpected);
        assert.equal(report.writePermissionVerified, false);
        for (const secret of [account, id, env.CLOUDFLARE_API_TOKEN, 'unlogged-token'])
            assert.equal(JSON.stringify(report).includes(secret), false);
    }
});

test('unknown Access metadata fails closed rather than claiming an authorization', async () => {
    const f = fixture('account');
    const fetchImpl: typeof fetch = async (input, init = {}) => {
        const url = new URL(String(input));
        if (url.searchParams.get('name')?.startsWith('Access:')) return json([]);
        if (url.pathname.endsWith('/tokens/' + id)) return json({
            id, status: 'active', policies: [{effect: 'allow',
                permission_groups: [{id: appsEdit}, {id: organizationRead}]}]
        });
        return f.fetchImpl(input, init);
    };
    const report = await inspectTokenManagement({env, fetchImpl});
    assert.equal(report.outcome, 'readable');
    assert.equal(report.accessAppsPolicy, 'unknown');
    assert.equal(report.accessOrganizationPolicy, 'unknown');
    assert.equal(report.writePermissionVerified, false);
});

test('explicit account deny wins over Allow and does not leak policies', async () => {
    const f = fixture('account');
    const fetchImpl: typeof fetch = async (input, init = {}) => {
        if (new URL(String(input)).pathname.endsWith('/tokens/' + id)) return json({
            id, status: 'active', policies: [
                {effect: 'allow', resources: {['com.cloudflare.api.account.' + account]: '*'},
                    permission_groups: [{id: appsEdit}, {id: organizationRead}]},
                {effect: 'deny', resources: {['com.cloudflare.api.account.' + account]: '*'},
                    permission_groups: [{id: appsEdit}]}
            ]
        });
        return f.fetchImpl(input, init);
    };
    const report = await inspectTokenManagement({env, fetchImpl});
    assert.equal(report.accessAppsPolicy, 'deny');
    assert.equal(report.accessOrganizationPolicy, 'allow');
    assert.equal(report.readOnly, true);
});
