// Observations of this process only; no host vault or recovery claim is inferred.
export class ConnectionState {
    networkStatus = 'not_checked';
    authenticationStatus = 'not_checked';
    registrationStatus = 'not_attempted';
    lastAuthenticatedRead: {path:string; status:string; httpStatus?:number} | null = null;
    accessDenied = false;

    response(method:string, path:string, status:number, publicRead:boolean) {
        this.networkStatus = status === 403 ? 'access_denied' : 'reachable';
        if (status === 403) this.accessDenied = true;
        if (!publicRead && (status === 401 || status === 403)) this.authenticationStatus = 'rejected';
        if (!publicRead && method === 'GET') {
            const result = status === 401 || status === 403 ? 'rejected' : status >= 200 && status < 300 ? 'authenticated' : 'service_error';
            this.authenticationStatus = result;
            this.lastAuthenticatedRead = {path, status:result, httpStatus:status};
        }
        if (method === 'POST' && path === '/v1/accounts')
            this.registrationStatus = status >= 200 && status < 300 ? 'response_received' : status >= 500 ? 'outcome_unknown' : 'failed';
    }

    failed(method:string, path:string, publicRead:boolean) {
        this.networkStatus = this.accessDenied ? 'access_denied' : 'request_failed';
        if (!publicRead && method === 'GET') {
            this.authenticationStatus = 'not_verified';
            this.lastAuthenticatedRead = {path, status:'request_failed'};
        }
        if (method === 'POST' && path === '/v1/accounts') this.registrationStatus = 'outcome_unknown';
    }

    snapshot(credentialConfigured:boolean) {
        const blockers = [
            {code:'secure_vault_not_verified', nextAction:'Verify durable storage with the actual host vault. Do not generate or register until verified.'},
            {code:'secure_secret_injection_not_verified', nextAction:'Verify protected injection with the host. A process environment alone is not evidence of safe injection.'},
            {code:'recovery_not_verified', nextAction:'Verify a fresh vault binding and matching Account ID before declaring setup complete.'}
        ];
        if (!credentialConfigured) blockers.unshift({code:'credential_not_configured', nextAction:'Use an already saved identity through the host-approved secret injection path; never paste a secret into tool arguments.'});
        if (this.networkStatus === 'not_checked') blockers.push({code:'network_not_checked', nextAction:'No network probe was made. Use an authorized read against the trusted origin only after checking host prerequisites.'});
        if (this.accessDenied) blockers.unshift({code:'access_denied', nextAction:'Do not retry or switch routes. Resolve the access denial with the owner before a separately authorized new session.'});
        else if (this.networkStatus === 'request_failed') blockers.unshift({code:'request_failed', nextAction:'Check the trusted origin and network. Preserve unknown write outcomes; do not register a replacement identity.'});
        if (this.authenticationStatus === 'rejected') blockers.push({code:'authentication_rejected', nextAction:'Reconcile the original credential binding and origin. Do not infer account deletion or automatically register.'});
        if (this.authenticationStatus === 'service_error') blockers.push({code:'service_error', nextAction:'Inspect the service response status; it does not prove the saved identity was lost.'});
        const sessionRead = credentialConfigured && this.networkStatus === 'reachable' && this.authenticationStatus === 'authenticated' && !this.accessDenied;
        return {
            assessmentScope:'current_mcp_session', state:'needs_owner_action', setupComplete:false,
            automaticSetupSupported:false, credentialConfigured,
            networkStatus:this.networkStatus, authenticationStatus:this.authenticationStatus,
            lastAuthenticatedRead:this.lastAuthenticatedRead, registrationStatus:this.registrationStatus,
            vaultPersistenceStatus:'not_verified', secureSecretInjectionStatus:'not_verified', recoveryVerificationStatus:'not_verified',
            receiving:{mode:sessionRead ? 'session_only' : 'unsupported', backgroundSupported:false,
                reason:sessionRead ? 'Read inbox explicitly during this session; the adapter has no host wake trigger.' : 'An authenticated read has not established current session access, or access was denied.',
                backgroundBlocker:'host_receive_trigger_unavailable'},
            blockers
        };
    }
}
