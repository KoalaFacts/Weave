/**
 * A1: an app holding an owner grant approves or rejects a pending plan of one of that human's agents, only while the owner
 * chose on Weave's consent page to let it (a live `owner_grant_approvals` row). The decision is the owner page's own
 * (`confirmExternalAction` / `rejectExternalAction`): the exact plan digest, revalidation, expiry and the `dispatching` /
 * `unknown` rules are unchanged, and `decided_by` stays the human. `owner_grant_decisions` records the grant that made it,
 * in the same transaction. An agent's own grant never reaches this: only the owner-grant tools call it.
 */
import { atomic, type Env, type Transaction } from './database.ts';
import { confirmExternalAction, pendingForOwner, rejectExternalAction, type OwnerExternalAction } from './external-actions.ts';
import { writesRefused } from './humans.ts';
import type { OwnerDecision } from './owner-actions.ts';
import { nowUtc } from './time.ts';

export type AppDecisionInput = {humanId: string; grantId: string; actionId: string; planDigest: string | null};
export type AppDecision = OwnerDecision | {ok: false; reason: 'decisions-off'};

export async function appDecideExternalAction(env: Env, input: AppDecisionInput, check: (tx: Transaction) => Promise<void>,
    clock: () => bigint = nowUtc): Promise<AppDecision> {
    if (writesRefused(env)) return {ok: false, reason: 'unavailable'};
    return atomic(env.db, async tx => {
        // The grant is live, is this human's and the human is not frozen (401 otherwise), read in this transaction like every
        // owner-grant write; its permission to decide is the owner's live consent-page choice for this grant.
        await check(tx);
        const allowed = await tx.db.prepare('SELECT 1 n FROM owner_grant_approvals WHERE grant_id=? AND revoked_at IS NULL')
            .bind(input.grantId).first('n');
        if (allowed === null) return {ok: false, reason: 'decisions-off'} as const;
        const human = await tx.db.prepare('SELECT subject FROM humans WHERE human_id=?').bind(input.humanId).first<{subject: string}>();
        if (!human) return {ok: false, reason: 'not-found'} as const;
        const owner = {humanId: input.humanId, subject: human.subject}, at = tx.store.now;
        const decided = input.planDigest !== null
            ? await confirmExternalAction(env, tx, owner, input.actionId, input.planDigest, at)
            : await rejectExternalAction(tx, owner, input.actionId, at);
        if (decided.ok) tx.writes.push(tx.db.prepare('INSERT INTO owner_grant_decisions(action_id,decision,grant_id,decided_at) VALUES(?,?,?,CAST(? AS INTEGER))')
            .bind(input.actionId, input.planDigest !== null ? 'approved' : 'rejected', input.grantId, at.toString()));
        return decided;
    }, clock);
}

/** The pending, unexpired plan of one of the grant's human's agents exactly as Weave's owner page shows it, with the plan
 * digest an approval names; null for anything else (decided, expired, another human's, unknown). Reading needs no permission
 * to decide: the grant can already act as each of those agents. */
export async function appPendingAction(env: Env, humanId: string, actionId: string, check: (tx: Transaction) => Promise<void>,
    clock: () => bigint = nowUtc): Promise<OwnerExternalAction | null> {
    return atomic(env.db, async tx => {
        await check(tx);
        return (await pendingForOwner(tx, humanId, tx.store.now, actionId))[0] ?? null;
    }, clock);
}

/** How many of the human's agents' pending plans one `list_pending_actions` call returns. */
export const pendingActionsListedToApp = 20;

/** The oldest pending, unexpired plans of the grant's human's agents, as Weave's owner page lists them, at most
 * `pendingActionsListedToApp`; `more` says whether others wait behind them (deciding these brings them forward). Reading needs
 * no permission to decide, as for `appPendingAction`. */
export async function appPendingActions(env: Env, humanId: string, check: (tx: Transaction) => Promise<void>,
    clock: () => bigint = nowUtc): Promise<{items: OwnerExternalAction[]; more: boolean}> {
    return atomic(env.db, async tx => {
        await check(tx);
        const rows = await pendingForOwner(tx, humanId, tx.store.now, null, pendingActionsListedToApp + 1);
        return {items: rows.slice(0, pendingActionsListedToApp), more: rows.length > pendingActionsListedToApp};
    }, clock);
}
