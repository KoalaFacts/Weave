import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

type JSONObject = Record<string, unknown>;
type Binding = JSONObject & {name?: unknown;type?: unknown;text?: unknown;service?: unknown};
const accountPattern = /^[a-f0-9]{32}$/i;
const versionPattern = /^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i;
const origin = 'https://demo.weave-389.workers.dev';
const resource = origin + '/mcp';

function object(value: unknown): JSONObject {
    return value && typeof value === 'object' && !Array.isArray(value) ? value as JSONObject : {};
}
async function boundedJson(response: Response): Promise<JSONObject> {
    const reader = response.body?.getReader();
    if (!reader) throw Error('Cloudflare metadata response has no body.');
    const chunks: Uint8Array[] = [];
    let size = 0;
    try {
        for (;;) {
            const {done, value} = await reader.read();
            if (done) break;
            size += value.length;
            if (size > 262144) throw Error('Cloudflare metadata response exceeded size limit.');
            chunks.push(value);
        }
    } finally {
        await reader.cancel().catch(() => {});
        reader.releaseLock();
    }
    try { return object(JSON.parse(Buffer.concat(chunks).toString('utf8'))); }
    catch { throw Error('Cloudflare metadata returned invalid JSON.'); }
}

/** Operator-only read-only diagnosis. No write API, D1 mutation, credential forwarding or provisioning. */
export async function inspectLiveOAuth({env = process.env, fetchImpl = fetch}: {
    env?: NodeJS.ProcessEnv;fetchImpl?: typeof fetch
} = {}) {
    const account = env.CLOUDFLARE_ACCOUNT_ID;
    const token = env.CLOUDFLARE_API_TOKEN;
    if (!accountPattern.test(account ?? '') || !token) throw Error('Read-only inspection requires the existing Cloudflare account ID and API token.');
    const api = 'https://api.cloudflare.com/client/v4/accounts/' + account;
    async function apiGet(path: string, label: string): Promise<unknown> {
        let response: Response;
        try {
            response = await fetchImpl(api + path, {
                method: 'GET', redirect: 'error', signal: AbortSignal.timeout(10000),
                headers: {Authorization: 'Bearer ' + token, Accept: 'application/json'}
            });
        } catch { throw Error(label + ' read failed; no retry or mutation attempted.'); }
        if (!response.ok) {
            await response.body?.cancel().catch(() => {});
            throw Error(label + ' returned HTTP ' + response.status + '; no retry or mutation attempted.');
        }
        let envelope: JSONObject;
        try { envelope = await boundedJson(response); }
        catch { throw Error(label + ' metadata read failed; no retry or mutation attempted.'); }
        if (envelope.success !== true || !envelope.result) throw Error(label + ' returned invalid metadata; no mutation attempted.');
        return envelope.result;
    }
    const subdomain = object(await apiGet('/workers/subdomain', 'Account subdomain'));
    if (subdomain.subdomain !== 'weave-389') throw Error('Cloudflare account does not own the authorized demo subdomain.');
    const settings = object(await apiGet('/workers/scripts/demo/settings', 'Demo Worker settings'));
    if (!Array.isArray(settings.bindings)) throw Error('Demo Worker settings are missing binding metadata.');
    const bindings = settings.bindings.map(object) as Binding[];
    const names = ['AUTH_SERVER', 'OAUTH_ISSUER', 'OAUTH_RESOURCE', 'OAUTH_ACCOUNT_PROOF_KEY',
        'OAUTH_IDENTITY_LINKS_ENABLED', 'OAUTH_INTROSPECTION_URL', 'OAUTH_CLIENT_SECRET'];
    const problems: string[] = [];
    for (const name of names) if (bindings.filter(x => x.name === name).length > 1) problems.push('Duplicate ' + name + ' binding');
    const get = (name: string): Binding | undefined => bindings.find(x => x.name === name);
    const service = get('AUTH_SERVER');
    const external = get('OAUTH_INTROSPECTION_URL');
    const externalNames = ['OAUTH_INTROSPECTION_URL', 'OAUTH_CLIENT_ID',
        'OAUTH_CLIENT_SECRET', 'OAUTH_INTROSPECTION_TRUSTED_ORIGIN'];
    const externalConfigured = externalNames.some(name => !!get(name));
    if (service && (service.type !== 'service' || typeof service.service !== 'string')) problems.push('Invalid AUTH_SERVER binding type');
    if (service && externalConfigured) problems.push('Mixed Service Binding and external introspection modes');
    // Mirror oauthConfiguration(): an external RFC 7662 mode is only usable
    // with all five required bindings, even if an introspection URL exists.
    // Only inspect binding presence; never serialize client credentials.
    const requiredExternal = ['OAUTH_ISSUER', 'OAUTH_RESOURCE', 'OAUTH_INTROSPECTION_URL',
        'OAUTH_CLIENT_ID', 'OAUTH_CLIENT_SECRET'];
    const anyExternal = requiredExternal.some(name => !!get(name)) || !!get('OAUTH_INTROSPECTION_TRUSTED_ORIGIN');
    if (!service && anyExternal && requiredExternal.some(name => !get(name)))
        problems.push('Incomplete external introspection settings');
    const issuer = get('OAUTH_ISSUER');
    const resourceBinding = get('OAUTH_RESOURCE');
    if (issuer && (issuer.type !== 'plain_text' || typeof issuer.text !== 'string')) problems.push('Invalid OAUTH_ISSUER binding type');
    if (resourceBinding && (resourceBinding.type !== 'plain_text' || resourceBinding.text !== resource)) problems.push('Unexpected OAUTH_RESOURCE binding');
    const proof = get('OAUTH_ACCOUNT_PROOF_KEY');
    if (proof && proof.type !== 'secret_text') problems.push('Account proof key must be a secret binding');
    const linkFlag = get('OAUTH_IDENTITY_LINKS_ENABLED');
    if (linkFlag && (linkFlag.type !== 'plain_text' || !['true', 'false'].includes(String(linkFlag.text)))) problems.push('Invalid OAuth identity link feature flag');
    const deployments = object(await apiGet('/workers/scripts/demo/deployments?per_page=1', 'Active Worker deployment'));
    if (!Array.isArray(deployments.deployments) || deployments.deployments.length !== 1)
        throw Error('Expected exactly one active Worker deployment record; no mutation attempted.');
    const active = object(deployments.deployments[0]);
    if (typeof active.id !== 'string' || !versionPattern.test(active.id) ||
        !Array.isArray(active.versions) || active.versions.length < 1 || active.versions.length > 10)
        throw Error('Active Worker deployment metadata is invalid.');
    const versions = active.versions.map(value => {
        const item = object(value);
        if (typeof item.version_id !== 'string' || !versionPattern.test(item.version_id) ||
            typeof item.percentage !== 'number' || !Number.isFinite(item.percentage) ||
            item.percentage <= 0 || item.percentage > 100)
            throw Error('Active Worker version metadata is invalid.');
        return {versionId: item.version_id, percentage: item.percentage};
    });
    // Cloudflare deployments allocate exactly 100% of traffic among their versions.
    const allocation = versions.reduce((sum, item) => sum + item.percentage, 0);
    if (Math.abs(allocation - 100) > 0.000001)
        throw Error('Active Worker deployment traffic allocation is incomplete.');
    const observed: Record<string, {status: number; bearerChallenge: boolean; redirect: boolean}> = {};
    for (const path of ['/','/.well-known/oauth-protected-resource/mcp','/.well-known/oauth-protected-resource','/mcp']) {
        let response: Response;
        try {
            // Never forward the Cloudflare API token, Weave controls, OAuth bearers or cookies.
            response = await fetchImpl(origin + (path.startsWith('/') ? path : '/' + path), {
                method: 'GET', redirect: 'manual', signal: AbortSignal.timeout(10000),
                headers: {Accept: 'application/json'}, cache: 'no-store'
            });
        } catch { throw Error('Public demo probe failed at a fixed path; no credentials were forwarded.'); }
        observed[path] = {
            status: response.status,
            bearerChallenge: /^Bearer(?:\s|$)/i.test(response.headers.get('www-authenticate') ?? ''),
            redirect: response.status >= 300 && response.status < 400
        };
        await response.body?.cancel().catch(() => {});
    }
    const metadata = [observed['/.well-known/oauth-protected-resource/mcp'],
        observed['/.well-known/oauth-protected-resource']];
    const report = {
        checkedAt: new Date().toISOString(),
        deployment: {id: active.id, versions, createdAt: typeof active.created_on === 'string' &&
            /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?(?:Z|[+-]\d\d:\d\d)$/.test(active.created_on) ? active.created_on : null},
        configuration: {
            mode: problems.length ? 'invalid' : service ? 'service-binding' : external ? 'external-rfc7662' : 'not-configured',
            issuerPresent: !!issuer,
            resourceMatchesDemo: resourceBinding?.text === resource && resourceBinding.type === 'plain_text',
            proofKeyPresent: !!proof,
            identityLinksEnabled: linkFlag?.type === 'plain_text' && linkFlag.text === 'true',
            problems
        },
        anonymous: observed,
        metadataBlockedWithoutBearerChallenge: metadata.every(x => x.status !== 200) && metadata.some(x => x.status === 401 && !x.bearerChallenge),
        interpretation: 'Version and settings are Cloudflare API reads; anonymous HTTP statuses cannot distinguish edge Access/WAF from deployed application behavior. No tokens or bindings are exposed.',
        readOnly: true
    };
    const serialized = JSON.stringify(report);
    if ([account, token].some(secret => typeof secret === 'string' && serialized.includes(secret))) throw Error('Credential reflection prevented diagnostic output.');
    return report;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
    try {
        if (process.argv.length !== 2) throw Error('Read-only live OAuth inspection accepts no arguments.');
        console.log(JSON.stringify(await inspectLiveOAuth(), null, 2));
    } catch (error) {
        console.error((error as Error).message);
        process.exitCode = 1;
    }
}
