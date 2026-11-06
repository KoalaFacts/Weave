/**
 * The publishing router. Weave knows no platform: an agent proposes one post in a single format, the owner approves it,
 * and the router hands it to the adapter of the credential's provider. An adapter declares how an owner connects it,
 * the limits its platform puts on a post and how to send one; validation, the description agents read, media checks,
 * approval and the at-most-once dispatch record are the router's.
 */
import { atomic, type Env, type Transaction } from './database.ts';
import type { ExternalProvider, ProviderResult, PublicationState } from './external-providers.ts';
import { readMediaRecord } from './external-media.ts';

/** One file of a post, pinned to the exact uploaded bytes. `contentType` is the stored type, filled in by the router. */
export type PostFile = {mediaId: string; bytes: number; sha256: string; alt: string | null; contentType: string};
/** The one post format: `{text, media, settings?, publishAt?}`, as Postiz takes it. */
export type Post = {text: string; media: PostFile[]; settings: Record<string, unknown>; publishAt: string | null};

/** What one platform accepts. The router refuses anything else before the owner is asked. */
export type PostRules = {
    /** `weigh` counts the text when the platform weighs characters unequally; its unit is then 'weighted characters'. */
    text: {max: number; maxBytes?: number; maxWithMedia?: number} & ({unit: 'characters' | 'graphemes'; weigh?: never} | {unit: 'weighted characters'; weigh: (text: string) => number});
    /** Accepted media types and the largest file of each, in bytes; `maxAlt` when the platform takes less alt text than the post format (0: none). */
    media: {max: number; types: Readonly<Record<string, number>>; maxTotalBytes?: number; maxAlt?: number};
    /** How far ahead publishAt may be, in ms; null when the platform posts immediately only. */
    schedule: number | null;
    /** Settings this account's posts must carry: fixed values and required names. Absent: the post takes no settings. */
    settings?: (targetAccount: string) => {fixed: Readonly<Record<string, string>>; required: readonly string[]; documented: string};
    /** A note for agents, for example which text becomes clickable. */
    note?: string;
};

/** A field an owner fills to connect: at least one is secret, and the adapter keeps only the secret ones. */
export type ConnectField = {name: string; label: string; secret: boolean; pattern?: string; placeholder?: string};
/** An account the secret acts as: `ref` completes the credential reference `<provider>:<ref>`. */
export type ConnectedAccount = {ref: string; targetAccount: string; targetDisplay: string};
export type Connector = {
    label: string;
    fields: readonly ConnectField[];
    /** Shown under the form, for example where to create the secret. */
    help?: string;
    /** True when one secret covers all of the agent's accounts on this provider, so accounts it no longer lists are revoked. */
    replaces: boolean;
    /** The accounts these values act as, verified with the platform; null when the platform or the values are refused. */
    verify(values: Readonly<Record<string, string>>): Promise<ConnectedAccount[] | null>;
};

/**
 * What an adapter receives to send one approved post. `secret` is its one secret field's value, or a JSON object of the
 * values by field name when it has several; it never leaves the adapter.
 */
export type PublishCall = {env: Env; accountId: string; actionId: string; targetAccount: string; secret: string; post: Post};
export type PublishingAdapter = {
    name: string;
    /** Changes whenever approval-relevant dispatch semantics change; approvals bound to another revision are refused. */
    contractRevision: string;
    rules: PostRules;
    connector: Connector;
    /** Sends the post. Return rejected only when nothing was published; throw when the outcome is uncertain. */
    publish(call: PublishCall): Promise<ProviderResult>;
    publicationStates?(secret: string, items: {receipt: string; trackFrom: number}[]): Promise<Map<string, PublicationState>>;
};

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/, sha = /^[0-9a-f]{64}$/;
const instant = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{3})?Z$/;
const maxAlt = 2000;
type Parsed = Omit<Post, 'media'> & {media: Omit<PostFile, 'contentType'>[]};

/** The shape of any post, before platform rules; null when it is not exactly `{text, media, settings?, publishAt?}`. */
export function parsePost(content: string): Parsed | null {
    let value: unknown;
    try { value = JSON.parse(content); } catch { return null; }
    const post = value as Record<string, unknown>;
    if (!post || typeof post !== 'object' || Array.isArray(post)) return null;
    if (Object.keys(post).some(key => !['text', 'media', 'settings', 'publishAt'].includes(key))) return null;
    const {text, media, settings = {}, publishAt = null} = post;
    if (typeof text !== 'string' || !Array.isArray(media) || !settings || typeof settings !== 'object' || Array.isArray(settings)) return null;
    if (publishAt !== null && (typeof publishAt !== 'string' || !instant.test(publishAt) || Number.isNaN(Date.parse(publishAt)))) return null;
    const files: Parsed['media'] = [];
    for (const file of media as Record<string, unknown>[]) {
        if (!file || typeof file !== 'object' || Array.isArray(file) || Object.keys(file).some(key => !['mediaId', 'bytes', 'sha256', 'alt'].includes(key))) return null;
        const {mediaId, bytes, sha256, alt = null} = file;
        if (typeof mediaId !== 'string' || !uuid.test(mediaId) || typeof sha256 !== 'string' || !sha.test(sha256)
            || typeof bytes !== 'number' || !Number.isSafeInteger(bytes) || bytes < 1 || (alt !== null && (typeof alt !== 'string' || alt.length > maxAlt))) return null;
        files.push({mediaId, bytes, sha256, alt: alt as string | null});
    }
    return {text, media: files, settings: settings as Record<string, unknown>, publishAt: publishAt as string | null};
}

const graphemes = (text: string) => [...new Intl.Segmenter(undefined, {granularity: 'grapheme'}).segment(text)].length;

/** Why `post` breaks the rules, or null. With `now`, a publishAt must also be ahead within the platform's horizon. */
function ruleBreak(post: Parsed, rules: PostRules, targetAccount: string, now: number | null): string | null {
    const {text, media} = rules;
    const length = text.weigh ? text.weigh(post.text) : text.unit === 'graphemes' ? graphemes(post.text) : post.text.length;
    const maxText = post.media.length && text.maxWithMedia !== undefined ? text.maxWithMedia : text.max;
    if (length > maxText) return `text must be at most ${maxText} ${text.unit}${post.media.length && text.maxWithMedia !== undefined ? ' with media' : ''}`;
    if (text.maxBytes !== undefined && new TextEncoder().encode(post.text).byteLength > text.maxBytes) return `text must be at most ${text.maxBytes} bytes`;
    if (post.text.trim() === '' && post.media.length === 0) return 'text or media is required';
    if (post.media.length > media.max) return `at most ${media.max} media`;
    // An approved description is sent as it is, never shortened, so a longer one is refused before approval.
    if (media.maxAlt !== undefined && post.media.some(file => file.alt !== null && file.alt.length > media.maxAlt!))
        return media.maxAlt === 0 ? 'alt is not supported here; omit it' : `alt must be at most ${media.maxAlt} characters here`;
    if (media.maxTotalBytes !== undefined && post.media.reduce((total, file) => total + file.bytes, 0) > media.maxTotalBytes)
        return `media must be at most ${media.maxTotalBytes} bytes in all`;
    if (post.publishAt !== null) {
        if (rules.schedule === null) return 'publishAt is not supported here; omit it to post on execution';
        const at = Date.parse(post.publishAt);
        if (now !== null && !(at > now && at <= now + rules.schedule)) return `publishAt must be a future UTC time at most ${Math.round(rules.schedule / 86_400_000)} days ahead`;
    }
    const expected = rules.settings?.(targetAccount);
    if (!expected) return Object.keys(post.settings).length ? 'settings are not taken here; omit them' : null;
    for (const [name, value] of Object.entries(expected.fixed)) if (post.settings[name] !== value) return `settings.${name} must be "${value}" for this account`;
    const missing = expected.required.filter(name => !Object.hasOwn(post.settings, name));
    return missing.length ? 'settings must include ' + missing.join(', ') : null;
}

const typeNames = (types: Readonly<Record<string, number>>) =>
    Object.entries(types).map(([type, max]) => `${type} up to ${max} bytes`).join(', ');

/** The description agents read for one account: the post format and this platform's limits. */
export function describePost(rules: PostRules, targetAccount: string): string {
    const expected = rules.settings?.(targetAccount);
    const settings = expected ? `"settings": {${[...Object.entries(expected.fixed).map(([name, value]) => `"${name}": "${value}"`), ...expected.required.map(name => `"${name}"`)].join(', ')}} ${expected.documented}, ` : '';
    const schedule = rules.schedule === null ? '' : `"publishAt": optional future UTC time such as 2026-01-31T09:00:00Z, at most ${Math.round(rules.schedule / 86_400_000)} days ahead, `;
    const caption = rules.text.maxWithMedia === undefined ? '' : ` (${rules.text.maxWithMedia} with media)`;
    return `JSON {"text": at most ${rules.text.max} ${rules.text.unit}${caption}, "media": up to ${rules.media.max} of your uploaded files as {"mediaId", "bytes", "sha256", "alt"?} `
        + `(${typeNames(rules.media.types)}${rules.media.maxTotalBytes === undefined ? '' : `, ${rules.media.maxTotalBytes} bytes in all`}; alt describes an image for people who cannot see it`
        + `${rules.media.maxAlt === undefined ? '' : rules.media.maxAlt === 0 ? ', but this platform takes none' : `, at most ${rules.media.maxAlt} characters`}), `
        + settings + schedule + '}. Text or media is required.' + (rules.note ? ' ' + rules.note : '');
}

/** The stored files a post names, each matching its pin and accepted by the rules; a reason string otherwise. */
async function storedFiles(tx: Transaction, accountId: string, post: Parsed, rules: PostRules): Promise<PostFile[] | string> {
    const files: PostFile[] = [];
    for (const file of post.media) {
        const media = await readMediaRecord(tx, accountId, file.mediaId);
        // Each file is pinned to the exact uploaded bytes, so the owner approves these files and no others.
        if (!media || media.bytes !== file.bytes || media.sha256 !== file.sha256) return 'media must name your uploaded files with their exact bytes and sha256';
        const max = rules.media.types[media.contentType];
        if (max === undefined) return 'media must be ' + Object.keys(rules.media.types).join(', ');
        if (media.bytes > max) return `${media.contentType} files must be at most ${max} bytes here`;
        files.push({...file, contentType: media.contentType});
    }
    return files;
}

/** The governed external-action provider of one publishing adapter: the operation publish_post. */
export function publishingProvider(adapter: PublishingAdapter): ExternalProvider {
    return {
        name: adapter.name,
        contractRevision: adapter.contractRevision,
        operations: ['publish_post'],
        describeContent: targetAccount => describePost(adapter.rules, targetAccount),
        async checkContent(tx, {accountId, content, targetAccount}) {
            const post = parsePost(content);
            if (!post) return 'must be JSON {text, media: [{mediaId, bytes, sha256, alt?}], settings?, publishAt?}';
            const broken = ruleBreak(post, adapter.rules, targetAccount, Date.now());
            if (broken) return broken;
            const files = await storedFiles(tx, accountId, post, adapter.rules);
            return typeof files === 'string' ? files : null;
        },
        async execute(env, call) {
            const parsed = parsePost(call.content);
            if (!parsed || call.secret === null || ruleBreak(parsed, adapter.rules, call.targetAccount, null)) return {status: 'rejected', reason: 'plan-unreadable'};
            // An approved schedule that has already passed is not silently turned into an immediate post.
            if (parsed.publishAt !== null && Date.parse(parsed.publishAt) <= Date.now()) return {status: 'rejected', reason: 'schedule-passed'};
            const files = await atomic(env.db, tx => storedFiles(tx, call.accountId, parsed, adapter.rules));
            if (typeof files === 'string') return {status: 'rejected', reason: 'media-unavailable'};
            return adapter.publish({env, accountId: call.accountId, actionId: call.actionId, targetAccount: call.targetAccount, secret: call.secret,
                post: {...parsed, media: files}});
        },
        ...(adapter.publicationStates ? {publicationStates: adapter.publicationStates} : {})
    };
}

/** The connect form an owner page offers for one adapter. */
export type OwnerConnector = {provider: string; label: string; fields: ConnectField[]; help: string | null};
export const ownerConnector = (adapter: PublishingAdapter): OwnerConnector =>
    ({provider: adapter.name, label: adapter.connector.label, fields: adapter.connector.fields.map(field => ({...field})), help: adapter.connector.help ?? null});
