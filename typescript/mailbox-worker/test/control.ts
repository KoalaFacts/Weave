// Bearer-control access kept only for the tests whose subject is the control itself; deleted with the control.
import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import type { start } from './harness.ts';

type Harness = Awaited<ReturnType<typeof start>>;
export const secret = () => randomBytes(32).toString('hex');
type Control = string | { control: string } | null | undefined;
/** The harness with the pre-key control interface: request/ok take a control (or an account carrying one); header selects the canonical or original control header. */
export function controlHarness(h: Harness, header = 'X-Weave-Account-Control') {
    const request = (path: string, as?: Control, method = 'GET', value?: unknown) => { const control = typeof as === 'string' ? as : as?.control; return h.mf.dispatchFetch('http://localhost' + path, {
        method, headers: { ...(control ? { [header]: control } : {}), 'Content-Type': 'application/json' },
        ...(value === undefined ? {} : { body: typeof value === 'string' ? value : JSON.stringify(value) }) }); };
    const ok = async (path: string, control?: Control, method = 'GET', value?: unknown): Promise<any> => {
        const r = await request(path, control, method, value); assert.equal(r.status, 200, await r.clone().text()); return r.json();
    };
    const register = async (profile = {}, control = secret()) => { const response = await ok('/v1/accounts', control, 'POST', profile); return { ...response, accountId: response.agentId, control }; };
    const address = (account: { control: string }, profile = {}) => ok('/v1/addresses', account.control, 'POST', profile);
    const participant = async () => { const account = await register(); const card = await address(account); return { ...account, cardId: card.addressId }; };
    return { mf: h.mf, db: h.db, close: h.close, request, ok, register, address, participant };
}
