import assert from 'node:assert/strict';
import {test} from 'node:test';
import {renderDemoConfig} from '../scripts/prepare-demo.ts';

const env = {
    CLOUDFLARE_ACCOUNT_ID: 'a'.repeat(32),
    WEAVE_AUTH_KV_NAMESPACE_ID: 'b'.repeat(32),
    OAUTH_ISSUER: 'https://weave-auth-demo.weave-389.workers.dev',
    OAUTH_RESOURCE: 'https://demo.weave-389.workers.dev/mcp',
    OAUTH_ACCOUNT_PROOF_KEY: 'a-private-signing-key-must-never-appear'
};

test('creates an exact approved demo authorization manifest without secrets', () => {
    const config = renderDemoConfig(env);
    assert.equal(config.name, 'weave-auth-demo');
    assert.equal(config.main, 'src/index.ts');
    assert.equal(config.workers_dev, true);
    assert.equal(config.account_id, env.CLOUDFLARE_ACCOUNT_ID);
    assert.deepEqual(config.vars, {
        OAUTH_ISSUER: env.OAUTH_ISSUER,
        OAUTH_RESOURCE: env.OAUTH_RESOURCE
    });
    assert.deepEqual(config.kv_namespaces, [
        {binding: 'OAUTH_KV', id: env.WEAVE_AUTH_KV_NAMESPACE_ID}
    ]);
    assert.deepEqual(config.durable_objects, {
        bindings: [{name: 'CONSENT_NONCES', class_name: 'ConsentNonce'}]
    });
    assert.deepEqual(config.migrations, [
        {tag: 'v1', new_sqlite_classes: ['ConsentNonce']}
    ]);
    assert.equal(JSON.stringify(config).includes(env.OAUTH_ACCOUNT_PROOF_KEY), false);
    assert.equal('services' in config, false);
    assert.equal('routes' in config, false);
});

test('refuses wrong account/resource/issuer or missing approved KV namespace', () => {
    const mutations: Record<string,string|undefined>[] = [
        {CLOUDFLARE_ACCOUNT_ID: undefined},
        {CLOUDFLARE_ACCOUNT_ID: 'a'.repeat(31)},
        {WEAVE_AUTH_KV_NAMESPACE_ID: undefined},
        {WEAVE_AUTH_KV_NAMESPACE_ID: 'b'.repeat(31)},
        {OAUTH_RESOURCE: 'https://demo.weave-389.workers.dev/mcp?access=true'},
        {OAUTH_RESOURCE: 'https://evil.example/mcp'},
        {OAUTH_ISSUER: 'https://wrong.weave-389.workers.dev'},
        {OAUTH_ISSUER: 'https://weave-auth-demo.weave-389.workers.dev/'},
        {OAUTH_ISSUER: 'https://weave-auth-demo.weave-389.workers.dev@evil.example'},
        {OAUTH_ISSUER: 'http://weave-auth-demo.weave-389.workers.dev'}
    ];
    for (const change of mutations)
        assert.throws(() => renderDemoConfig({...env,...change}),
            /approved|existing|identifier|issuer|resource|namespace/i, JSON.stringify(change));
});
