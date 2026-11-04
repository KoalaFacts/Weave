/**
 * Providers that execute governed external actions. A provider keeps its platform payloads and API details; the
 * external-actions module owns approval, credential revision checks and the at-most-once dispatch record.
 */
import type { Env, Transaction } from './database.ts';
import { youtubeUpload } from './youtube-provider.ts';
import { postizPublish } from './postiz-provider.ts';

/** `secret` is the opened credential secret, null for a provider that needs none. It never leaves the provider. */
export type ProviderCall = {accountId: string; operation: string; content: string; targetAccount: string; actionId: string; secret: string | null};
/** What a provider checks when a plan is proposed; `targetAccount` is the credential's provider-verified account. */
export type ProposedPlan = {accountId: string; operation: string; content: string; targetAccount: string};
/** `rejected` only when the provider confirmed nothing happened. Throw when the outcome is uncertain. */
export type ProviderResult = {status: 'completed'; receipt: string} | {status: 'rejected'; reason: string};
export type ExternalProvider = {
    name: string;
    /** Changes whenever approval-relevant dispatch semantics change; approvals bound to another revision are refused. */
    contractRevision: string;
    operations: readonly string[];
    /** What `content` must be for this credential's account, for the agent composing a plan. */
    describeContent(targetAccount: string): string;
    /** Provider-specific content rules checked when the plan is proposed; the reason it is invalid, or null. */
    checkContent?(tx: Transaction, plan: ProposedPlan): Promise<string | null>;
    execute(env: Env, call: ProviderCall): Promise<ProviderResult>;
};

const maxReceipt = 256, maxReason = 256, timeoutMs = 15_000;

/**
 * Test-only HTTP provider. It exists only when EXTERNAL_ACTION_TEST_ENDPOINT is configured, which no deployed
 * configuration sets. 200 with {externalId} completes, 422 with {reason} is a confirmed rejection, anything else is uncertain.
 */
const testHttp: ExternalProvider = {
    name: 'test-http',
    contractRevision: 'test-http-v1',
    operations: ['publish_text'],
    describeContent: () => 'Plain text.',
    async execute(env, call) {
        const {operation, content, targetAccount, actionId} = call;
        const response = await fetch(env.EXTERNAL_ACTION_TEST_ENDPOINT!, {method: 'POST', redirect: 'manual', signal: AbortSignal.timeout(timeoutMs),
            headers: {'Content-Type': 'application/json'}, body: JSON.stringify({operation, content, targetAccount, actionId})});
        const body = await response.json() as Record<string, unknown>;
        if (response.status === 200 && typeof body.externalId === 'string' && body.externalId.length > 0 && body.externalId.length <= maxReceipt)
            return {status: 'completed', receipt: body.externalId};
        if (response.status === 422 && typeof body.reason === 'string')
            return {status: 'rejected', reason: body.reason.slice(0, maxReason)};
        throw Error('Unconfirmed provider response.');
    }
};

export function provider(env: Env, name: string): ExternalProvider | null {
    if (name === testHttp.name && env.EXTERNAL_ACTION_TEST_ENDPOINT) return testHttp;
    if (name === youtubeUpload.name && youtubeUpload.available(env)) return youtubeUpload;
    if (name === postizPublish.name && postizPublish.available(env)) return postizPublish;
    return null;
}
