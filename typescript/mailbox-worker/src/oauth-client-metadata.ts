/** Public first-party client identification for the demo's explicit-consent
 * loopback PKCE acceptance runner. CIMD is public metadata, NOT authentication,
 * an account grant, a registered secret, or permission to send messages.
 */
export const demoOAuthClientId = 'https://demo.weave-389.workers.dev/oauth-client-metadata.json';
export function demoOAuthClientMetadata(): Response {
    return new Response(JSON.stringify({
        client_id: demoOAuthClientId,
        client_name: 'Weave demo public loopback MCP acceptance client',
        client_uri: 'https://github.com/KoalaFacts/Weave',
        redirect_uris: ['http://127.0.0.1/callback'],
        grant_types: ['authorization_code'],
        response_types: ['code'],
        token_endpoint_auth_method: 'none'
    }), {headers: {
        'Content-Type': 'application/json',
        'Cache-Control': 'public, max-age=300',
        'X-Content-Type-Options': 'nosniff'
    }});
}
