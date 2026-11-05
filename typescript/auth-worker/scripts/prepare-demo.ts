/** Prepare a nonsecret Wrangler configuration for an ALREADY APPROVED demo
 * OAuth Worker. This does not provision, deploy or modify either cloud service.
 */
import {writeFile} from 'node:fs/promises';
import {trialSelectedSubjects} from '../src/trial-allowlist.ts';
import {dirname, resolve} from 'node:path';
import {fileURLToPath, pathToFileURL} from 'node:url';

export const issuer = 'https://weave-auth-demo.weave-389.workers.dev';
export const resource = 'https://demo.weave-389.workers.dev/mcp';
const hexId = /^[0-9a-f]{32}$/i;
// Public GitHub OAuth app client ID (not a secret); same shape the Worker accepts.
const githubClientId = /^[A-Za-z0-9._-]{1,128}$/;

export function renderDemoConfig(env: NodeJS.ProcessEnv): Record<string, unknown> {
    if (!hexId.test(env.CLOUDFLARE_ACCOUNT_ID ?? ''))
        throw Error('Existing approved Cloudflare account identifier is required.');
    if (!hexId.test(env.WEAVE_AUTH_KV_NAMESPACE_ID ?? ''))
        throw Error('Existing approved OAuth KV namespace identifier is required.');
    if (env.OAUTH_ISSUER !== issuer)
        throw Error('OAuth issuer does not match the approved demo Workers.dev address.');
    if (env.OAUTH_RESOURCE !== resource)
        throw Error('OAuth resource does not match the approved demo MCP endpoint.');
    if (!githubClientId.test(env.GITHUB_CLIENT_ID ?? ''))
        throw Error('Existing approved GitHub OAuth app client identifier is required.');
    if (trialSelectedSubjects(issuer, env.WEAVE_TRIAL_GITHUB_SUBJECTS) === undefined)
        throw Error('Two or more exact owner-approved GitHub numeric subjects are required for the trial.');
    return demoManifest(env.CLOUDFLARE_ACCOUNT_ID!, env.WEAVE_AUTH_KV_NAMESPACE_ID!, env.GITHUB_CLIENT_ID!);
}

/** The demo OAuth Worker's nonsecret configuration, for its first publication and for code updates alike. */
export function demoManifest(accountId: string, kvNamespaceId: string, githubClientId: string): Record<string, unknown> {
    return {
        name: 'weave-auth-demo',
        main: 'src/index.ts',
        account_id: accountId,
        compatibility_date: '2026-10-23',
        compatibility_flags: ['global_fetch_strictly_public', 'nodejs_compat'],
        workers_dev: true,
        // GITHUB_CLIENT_SECRET and HUMAN_SESSION_KEY are Worker secrets installed
        // separately by the operator; they are never written to this manifest.
        vars: {OAUTH_ISSUER: issuer, OAUTH_RESOURCE: resource, GITHUB_CLIENT_ID: githubClientId},
        // Private RPC to the demo mailbox Worker's human directory (sign-in, agent picker).
        services: [{binding: 'MAILBOX', service: 'demo', entrypoint: 'HumanDirectory'}],
        kv_namespaces: [{binding: 'OAUTH_KV', id: kvNamespaceId}],
        durable_objects: {bindings: [{name: 'CONSENT_NONCES', class_name: 'ConsentNonce'}]},
        migrations: [{tag: 'v1', new_sqlite_classes: ['ConsentNonce']}]
    };
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
    try {
        if (process.argv.length !== 2) throw Error('No arguments are accepted.');
        const config = renderDemoConfig(process.env);
        const filename = resolve(dirname(fileURLToPath(import.meta.url)), '..', 'wrangler.demo.generated.jsonc');
        // Exclusive creation prevents replacing an already-reviewed configuration.
        await writeFile(filename, JSON.stringify(config, null, 2) + '\n', {flag: 'wx', mode: 0o600});
        console.log('Prepared nonsecret demo OAuth Wrangler configuration; no cloud changes or secrets installed.');
    } catch {
        // No exception text that could echo environment values is emitted.
        console.error('OAuth demo configuration preflight failed; no cloud changes attempted.');
        process.exitCode = 1;
    }
}
