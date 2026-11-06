/**
 * Providers that execute governed external actions. A provider keeps its platform payloads and API details; the
 * external-actions module owns approval, credential revision checks and the at-most-once dispatch record.
 */
import type { Env, Transaction } from './database.ts';
import { youtubeUpload } from './youtube-provider.ts';
import { postiz } from './postiz-provider.ts';
import { bluesky } from './bluesky-provider.ts';
import { telegram } from './telegram-provider.ts';
import { discord } from './discord-provider.ts';
import { x } from './x-provider.ts';
import { publishingProvider, type PublishingAdapter } from './publishing.ts';

/** `secret` is the opened credential secret, null for a provider that needs none. It never leaves the provider. */
export type ProviderCall = {accountId: string; operation: string; content: string; targetAccount: string; actionId: string; secret: string | null};
/** What a provider checks when a plan is proposed; `targetAccount` is the credential's provider-verified account. */
export type ProposedPlan = {accountId: string; operation: string; content: string; targetAccount: string};
/** A post's text limits: `unit` is how `max` counts; `maxWithMedia` replaces `max` when media is attached; `maxBytes` caps the UTF-8 size. */
export type TextRules = {max: number; unit: 'characters' | 'graphemes' | 'weighted characters'; maxWithMedia: number | null; maxBytes: number | null};
/**
 * `rejected` only when the provider confirmed nothing happened. Throw when the outcome is uncertain. `trackFrom` (epoch
 * ms) marks a completed effect that the provider finishes later, such as a queued or scheduled post: its publication
 * state is asked for from that time on.
 */
export type ProviderResult = {status: 'completed'; receipt: string; trackFrom?: number} | {status: 'rejected'; reason: string};
/** A later state of a tracked effect. `error` is the platform's own text: untrusted, bounded and shown as data. */
export type PublicationState = {state: 'queued' | 'published' | 'failed'; url: string | null; error: string | null};
export type ExternalProvider = {
    name: string;
    /** Changes whenever approval-relevant dispatch semantics change; approvals bound to another revision are refused. */
    contractRevision: string;
    operations: readonly string[];
    /** What `content` must be for this credential's account, for the agent composing a plan. */
    describeContent(targetAccount: string): string;
    /** The post text limits `checkContent` enforces, as data for apps; absent when the content is not a post. */
    textRules?: TextRules;
    /** Provider-specific content rules checked when the plan is proposed; the reason it is invalid, or null. */
    checkContent?(tx: Transaction, plan: ProposedPlan): Promise<string | null>;
    execute(env: Env, call: ProviderCall): Promise<ProviderResult>;
    /**
     * The current state of tracked effects by receipt, for one credential secret. A receipt the provider no longer
     * lists is absent from the result. Throws when the provider cannot be asked now.
     */
    publicationStates?(secret: string, items: {receipt: string; trackFrom: number}[]): Promise<Map<string, PublicationState>>;
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
    const adapter = publishingAdapter(env, name);
    if (adapter) return publishingProvider(adapter);
    return null;
}

/** Every publishing platform the router can reach. Adding one is adding its adapter here. */
export const publishingAdapters: readonly PublishingAdapter[] = [postiz, bluesky, telegram, discord, x];
/** The adapter for a provider name, when this deployment can seal its secrets. */
export function publishingAdapter(env: Env, name: string): PublishingAdapter | null {
    return env.EXTERNAL_CREDENTIAL_KEY ? publishingAdapters.find(adapter => adapter.name === name) ?? null : null;
}
