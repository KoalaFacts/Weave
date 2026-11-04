/** Restrict the fixed public Weave OAuth trial to owner-selected GitHub numeric subjects.
 * No list or malformed list is a configuration error, not an "Everyone" fallback.
 * Local synthetic issuers are not the approved public-trial deployment.
 */
export const publicTrialIssuer = 'https://weave-auth-demo.weave-389.workers.dev';
const numericSubject = /^[1-9][0-9]{0,15}$/;

/** null: not the Weave trial issuer; undefined: invalid/missing trial selection. */
export function trialSelectedSubjects(issuer: string, configured: unknown): ReadonlySet<string> | null | undefined {
  if (!URL.canParse(issuer) || new URL(issuer).origin !== publicTrialIssuer) return null;
  if (typeof configured !== 'string' || configured.length > 180) return undefined;
  const subjects = configured.split(',');
  if (subjects.length < 2 || subjects.length > 10 ||
      subjects.some(id => !numericSubject.test(id) || !Number.isSafeInteger(Number(id))) ||
      new Set(subjects).size !== subjects.length) return undefined;
  return new Set(subjects);
}
