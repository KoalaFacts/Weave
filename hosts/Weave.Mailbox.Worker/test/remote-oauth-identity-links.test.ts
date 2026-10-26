import assert from 'node:assert/strict';
import {test} from 'node:test';
import {start} from './harness.ts';

test('owner can see only its linked external OAuth subjects, with unauthenticated access denied',async()=>{
    const h=await start({bindings:{
        OAUTH_ISSUER:'https://issuer.example',
        OAUTH_RESOURCE:'https://localhost/mcp',
        OAUTH_INTROSPECTION_URL:'https://issuer.example/oauth/introspect',
        OAUTH_CLIENT_ID:'public-weave-client',
        OAUTH_CLIENT_SECRET:'synthetic-test-secret-not-for-production',
        OAUTH_IDENTITY_LINKS_ENABLED:'true'
    }});
    try {
        const a=await h.register();
        const denied=await h.mf.dispatchFetch('https://localhost/v1/accounts/oauth/links');
        assert.equal(denied.status,401,'links must require a first-party account control');
        const owned=await h.mf.dispatchFetch('https://localhost/v1/accounts/oauth/links',{
            headers:{'X-Weave-Account-Control':a.control}
        });
        assert.equal(owned.status,200,await owned.clone().text());
        assert.deepEqual((await owned.json()).items,[]);
    }finally{await h.close();}
});
