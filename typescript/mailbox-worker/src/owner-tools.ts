/** Agent management through an owner grant: list, create and rename the agents a human owns, and register, list and
 * revoke the grant's push webhooks. The human comes from the verified grant, never from tool arguments, and every write
 * re-checks the grant inside its own transaction.
 */
import { listWebhooks, registerWebhook, revokeWebhook } from './app-webhooks.ts';
import { createOwnedAgent, listHumanAgents, renameOwnedAgent } from './humans.ts';
import { ownerGrantCheck } from './client-grants.ts';
import { atomic, type Env } from './database.ts';
import { fail, invalidField, json, problem } from './wire.ts';

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const agentView = (accountId: string, displayName: string | null) => ({accountId, displayName});

export async function executeOwnerMcp(env: Env, humanId: string, name: string, args: Record<string, unknown>, grantId: string, sourceIp: string): Promise<Response> {
    const check = ownerGrantCheck(humanId, grantId);
    const only = (allowed: string[], required: string[]) => {
        const unknown = Object.keys(args).find(key => !allowed.includes(key));
        if (unknown !== undefined) invalidField(unknown.length <= 64 ? unknown : 'arguments', 'is not a parameter of this tool');
        const missing = required.find(key => !Object.hasOwn(args, key));
        if (missing !== undefined) invalidField(missing, 'is required');
    };
    const displayName = () => {
        if (typeof args.displayName !== 'string' || args.displayName.length < 1 || args.displayName.length > 128)
            invalidField('displayName', 'must be 1 to 128 visible characters');
        return args.displayName as string;
    };
    try {
        if (name === 'list_agents') {
            only(['afterCursor'], []);
            const after = args.afterCursor ?? null;
            if (after !== null && (typeof after !== 'string' || !uuid.test(after)))
                invalidField('afterCursor', 'must be the nextCursor string from the previous page');
            await atomic(env.db, check);
            const page = await listHumanAgents(env, humanId, after as string | null);
            return json({items: page.agents.map(agent => agentView(agent.accountId, agent.displayName)), nextCursor: page.next});
        }
        if (name === 'create_agent') {
            only(['displayName'], ['displayName']);
            const requested = displayName();
            const created = await createOwnedAgent(env, {humanId, displayName: requested, sourceIp}, check);
            if (created.ok) return json(agentView(created.accountId, requested));
            if (created.reason === 'invalid') invalidField('displayName', 'must be 1 to 128 visible characters');
            if (created.reason === 'agent-limit') fail(409, 'agent-limit');
            if (created.reason === 'rate-limited') fail(429, 'capacity', 3600);
            if (created.reason === 'frozen') fail(401, 'grant-revoked');
            fail(503, 'unavailable');
        }
        if (name === 'rename_agent') {
            only(['accountId', 'displayName'], ['accountId', 'displayName']);
            if (typeof args.accountId !== 'string' || !uuid.test(args.accountId)) invalidField('accountId', 'must be a lowercase UUID');
            const requested = displayName();
            const renamed = await renameOwnedAgent(env, {humanId, agentId: args.accountId as string, displayName: requested}, check);
            if (renamed.ok) return json(agentView(args.accountId as string, requested));
            if (renamed.reason === 'invalid') invalidField('displayName', 'must be 1 to 128 visible characters');
            if (renamed.reason === 'not-found') fail(404, 'agent-not-found');
            fail(503, 'unavailable');
        }
        if (name === 'register_webhook') {
            only(['url'], ['url']);
            const registered = await registerWebhook(env, {grantId, url: args.url}, check);
            if (registered.ok) return json({...registered.webhook, secret: registered.secret});
            if (registered.reason === 'invalid-url') invalidField('url', 'must be an absolute https URL of at most 2048 characters, without user info or fragment');
            if (registered.reason === 'webhook-limit') fail(409, 'webhook-limit');
            fail(503, 'unavailable');
        }
        if (name === 'list_webhooks') {
            only([], []);
            return json({items: await listWebhooks(env, grantId, check), nextCursor: null});
        }
        if (name === 'revoke_webhook') {
            only(['webhookId'], ['webhookId']);
            const revoked = await revokeWebhook(env, {grantId, webhookId: args.webhookId}, check);
            if (revoked.ok) return json({webhookId: args.webhookId as string, revoked: true});
            if (revoked.reason === 'not-found') fail(404, 'webhook-not-found');
            fail(503, 'unavailable');
        }
        fail(404, 'unavailable');
    } catch (error) { return problem(error); }
}
