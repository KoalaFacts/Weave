/** Fail-closed release intent gate for the one-time, operator-approved
 * first-party OAuth Service Binding rollout. Nothing here grants approval,
 * provisions resources, migrates D1 or deploys a Worker.
 */
import {resolve} from 'node:path';
import {pathToFileURL} from 'node:url';

export interface ReleaseIntent {
    eventName: string;
    ref: string;
    configurationScope: string;
    databaseMode: string;
    enabled: string;
}

export function validateFirstPartyOAuthRollout(input: ReleaseIntent): boolean {
    if (!['', 'false', 'true'].includes(input.enabled))
        throw Error('Invalid first-party OAuth release selector.');
    if (input.enabled !== 'true') return false;
    if (input.eventName !== 'workflow_dispatch' || input.ref !== 'refs/heads/main' ||
        input.configurationScope !== 'production' || input.databaseMode !== 'completed-only')
        throw Error('First-party OAuth activation requires explicit approved main production completed-only release.');
    return true;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
    try {
        if (process.argv.length !== 2) throw Error('Unexpected release gate argument.');
        const approved = validateFirstPartyOAuthRollout({
            eventName: process.env.GITHUB_EVENT_NAME ?? '',
            ref: process.env.GITHUB_REF ?? '',
            configurationScope: process.env.WEAVE_RELEASE_CONFIGURATION_SCOPE ?? '',
            databaseMode: process.env.WEAVE_RELEASE_DATABASE_MODE ?? '',
            enabled: process.env.WEAVE_RELEASE_FIRST_PARTY_OAUTH ?? ''
        });
        console.log(approved ? 'First-party OAuth release intent gate passed; operator and cloud preflights remain required.' :
            'First-party OAuth activation disabled for this release.');
    } catch {
        console.error('First-party OAuth release intent rejected; no cloud changes attempted.');
        process.exitCode = 1;
    }
}
