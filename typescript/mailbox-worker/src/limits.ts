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

/** How much one cleanup round may do. */
export type CleanupPolicy = {
    /** Distinct messages one round may select; 0 turns business cleanup off. */
    readonly maximum: number;
    /** Admission deadline for starting a round or a new batch; 0 turns business cleanup off. */
    readonly milliseconds: number;
};
/** Business cleanup is off, as the demo has always run it (owner, 2026-11-25: rarely changed values are code).
 * Rate-counter, lease and guard housekeeping is separate and stays on. Tests pass their own policy to `sweep`. */
export const cleanup: CleanupPolicy = Object.freeze({maximum: 0, milliseconds: 0});
