/** An optional sign-in allowlist of GitHub numeric subjects. Unset, anyone with a GitHub account may sign in; set,
 * only the listed accounts may. A malformed list is a configuration error, never read as "everyone".
 */
const numericSubject = /^[1-9][0-9]{0,15}$/;

/** null: no allowlist, anyone may sign in; undefined: a malformed allowlist. */
export function allowedGithubSubjects(configured: unknown): ReadonlySet<string> | null | undefined {
  if (configured === undefined || configured === '') return null;
  if (typeof configured !== 'string' || configured.length > 180) return undefined;
  const subjects = configured.split(',');
  if (subjects.length > 10 || subjects.some(id => !numericSubject.test(id) || !Number.isSafeInteger(Number(id))) ||
      new Set(subjects).size !== subjects.length) return undefined;
  return new Set(subjects);
}

/** The public demo is not an Everyone registration policy. It requires 2-10 expressly selected GitHub identities.
 * Other synthetic/local issuers retain their existing testing behavior.
 */
export const publicTrialIssuer = 'https://weave-auth-demo.weave-389.workers.dev';
export function trialSelectedSubjects(issuer: string, configured: unknown): ReadonlySet<string> | null | undefined {
  const subjects = allowedGithubSubjects(configured);
  const isPublicDemo = issuer === publicTrialIssuer || issuer === publicTrialIssuer + '/';
  return isPublicDemo && (!subjects || subjects.size < 2) ? undefined : subjects;
}
