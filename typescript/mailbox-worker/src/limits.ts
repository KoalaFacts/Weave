/** Registration and ownership limits. They rarely change, so they are code, not Worker settings (owner, 2026-11-25).
 * Tests that need smaller limits pass their own `Limits` to the functions that take one. */
export type Limits = {
    /** Self-registered (ownerless) agents in total; human-owned agents never count. */
    readonly registrationMaxAccounts: number;
    /** Registrations per hour across every source, self-registered and human-owned alike. */
    readonly registrationGlobalPerHour: number;
    /** Registrations per hour from one source IP, self-registered and human-owned alike. */
    readonly registrationPerIpPerHour: number;
    /** Agents one human may own. */
    readonly ownedAgentsPerHuman: number;
};
export const limits: Limits = Object.freeze({
    registrationMaxAccounts: 10000,
    registrationGlobalPerHour: 1000,
    registrationPerIpPerHour: 100,
    ownedAgentsPerHuman: 10000,
});
