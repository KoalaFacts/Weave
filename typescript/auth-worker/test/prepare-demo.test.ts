import assert from 'node:assert/strict';
import {test} from 'node:test';
import {renderDemoConfig} from '../scripts/prepare-demo.ts';

const env = {
    CLOUDFLARE_ACCOUNT_ID: 'a'.repeat(32),
    WEAVE_AUTH_KV_NAMESPACE_ID: 'b'.repeat(32),
    OAUTH_ISSUER: 'https://weave-auth-demo.weave-389.workers.dev',
    OAUTH_RESOURCE: 'https://demo.weave-389.workers.dev/mcp',
    GITHUB_CLIENT_ID: 'Iv23liPublicClientId',
    WEAVE_TRIAL_GITHUB_SUBJECTS: '8101,8102',
    GITHUB_CLIENT_SECRET: 'a-private-github-secret-must-never-appear',
    HUMAN_SESSION_KEY: 'a-private-session-key-must-never-appear'
};

test('creates an exact approved demo authorization manifest without secrets', () => {
    const config = renderDemoConfig(env);
    assert.equal(config.name, 'weave-auth-demo');
    assert.equal(config.main, 'src/index.ts');
    assert.equal(config.workers_dev, true);
    assert.equal(config.account_id, env.CLOUDFLARE_ACCOUNT_ID);
    assert.deepEqual(config.vars, {
        OAUTH_ISSUER: env.OAUTH_ISSUER,
        OAUTH_RESOURCE: env.OAUTH_RESOURCE,
        GITHUB_CLIENT_ID: env.GITHUB_CLIENT_ID
    });
    assert.deepEqual(config.services, [
        {binding: 'MAILBOX', service: 'demo', entrypoint: 'HumanDirectory'}
    ]);
    assert.deepEqual(config.kv_namespaces, [
        {binding: 'OAUTH_KV', id: env.WEAVE_AUTH_KV_NAMESPACE_ID}
    ]);
    assert.deepEqual(config.durable_objects, {
        bindings: [{name: 'CONSENT_NONCES', class_name: 'ConsentNonce'}]
    });
    assert.deepEqual(config.migrations, [
        {tag: 'v1', new_sqlite_classes: ['ConsentNonce']}
    ]);
    assert.equal(JSON.stringify(config).includes(env.GITHUB_CLIENT_SECRET), false);
    assert.equal(JSON.stringify(config).includes(env.HUMAN_SESSION_KEY), false);
    assert.equal(JSON.stringify(config).includes(env.WEAVE_TRIAL_GITHUB_SUBJECTS), false,
        'trial identities must be installed as an AS-only secret, never written into a generated manifest');
    assert.equal('routes' in config, false);
});

test('refuses wrong account/resource/issuer, missing approved KV namespace or GitHub client ID', () => {
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
        {OAUTH_ISSUER: 'http://weave-auth-demo.weave-389.workers.dev'},
        {GITHUB_CLIENT_ID: undefined},
        {GITHUB_CLIENT_ID: 'not a client id'},
        {WEAVE_TRIAL_GITHUB_SUBJECTS: '8101,8101'},
        {WEAVE_TRIAL_GITHUB_SUBJECTS: 'Everyone'},
        {WEAVE_TRIAL_GITHUB_SUBJECTS: '8101, 8102'},
        {WEAVE_TRIAL_GITHUB_SUBJECTS: '0,8102'},
        {WEAVE_TRIAL_GITHUB_SUBJECTS: '9007199254740993,8102'}
    ];
    for (const change of mutations)
        assert.throws(() => renderDemoConfig({...env,...change}),
            /approved|existing|identifier|issuer|resource|namespace|allowlist/i, JSON.stringify(change));
    // A production issuer without an owner-selected cohort must fail closed before any cloud operation.
    for (const allowlist of [undefined, '', '8101'])
        assert.throws(() => renderDemoConfig({...env, WEAVE_TRIAL_GITHUB_SUBJECTS: allowlist}),
            /allowlist|selected|numeric/i, 'a missing or single-subject trial list cannot enable public sign-in: '+String(allowlist));
});
