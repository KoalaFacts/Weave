import assert from 'node:assert/strict';
import {test} from 'node:test';
import {inspectOAuthDiscovery} from './discovery.ts';

const resource='https://mailbox.example/mcp',issuer='https://identity.example/tenant';
const metadata={issuer,authorization_endpoint:issuer+'/authorize',token_endpoint:issuer+'/token',
    response_types_supported:['code'],grant_types_supported:['authorization_code'],
    code_challenge_methods_supported:['S256'],authorization_response_iss_parameter_supported:true};
function fake(routes:Record<string,unknown>,seen:string[]=[]):typeof fetch {
    return (async(input,init)=>{
        const url=String(input);seen.push(url);
        assert.equal(init?.redirect,'error');assert.equal(init?.credentials,'omit');
        assert.equal(init?.method,'GET');assert.equal(new Headers(init?.headers).has('authorization'),false);
        const value=routes[url];
        if(value instanceof Response)return value;
        return value===undefined?new Response('',{status:404}):Response.json(value);
    }) as typeof fetch;
}
const prm=()=>({resource,authorization_servers:[issuer]});
const rm='https://mailbox.example/.well-known/oauth-protected-resource/mcp';
const am='https://identity.example/.well-known/oauth-authorization-server/tenant';
test('standard discovery uses explicit issuer trust and RFC8414 tenant paths',async()=>{
    const seen:string[]=[];
    const result=await inspectOAuthDiscovery(resource,issuer,fake({[rm]:prm(),[am]:metadata},seen));
    assert.equal(result.outcome,'metadata-compatible');assert.equal(result.authenticated,false);
    assert.equal(result.pkceS256,true);assert.equal(result.authorizationResponseIssuer,true);
    assert.deepEqual(seen,[rm,am]);
});
test('metadata at the origin is supported after the resource-path location is absent',async()=>{
    const root='https://mailbox.example/.well-known/oauth-protected-resource';
    const seen:string[]=[];
    const result=await inspectOAuthDiscovery(resource,issuer,fake({[root]:prm(),[am]:metadata},seen));
    assert.equal(result.outcome,'metadata-compatible');assert.deepEqual(seen,[rm,root,am]);
});
test('an advertised issuer is never fetched without explicit operator trust',async()=>{
    const seen:string[]=[];
    const result=await inspectOAuthDiscovery(resource,undefined,fake({[rm]:prm()},seen));
    assert.equal(result.outcome,'issuer-approval-required');assert.deepEqual(seen,[rm]);
});
test('wrong resource or issuer is rejected without contacting an injected host',async()=>{
    for(const doc of [{...prm(),resource:'https://other.example/mcp'},
        {...prm(),authorization_servers:['https://untrusted.example']}]){
        const seen:string[]=[];
        const result=await inspectOAuthDiscovery(resource,issuer,fake({[rm]:doc},seen));
        assert.equal(result.outcome,'metadata-rejected');assert.deepEqual(seen,[rm]);
    }
});
test('missing metadata is reported as absent, not as successful authentication',async()=>{
    const result=await inspectOAuthDiscovery(resource,undefined,fake({}));
    assert.equal(result.outcome,'resource-metadata-absent');assert.equal(result.authenticated,false);
});
test('failed, redirected and unbounded responses are never accepted or echoed',async()=>{
    const poison='SECRET_MUST_NOT_APPEAR';
    for(const response of [new Response(poison,{status:302,headers:{location:'https://untrusted.example'}}),
        new Response(poison,{status:503}),
        new Response('{"private":"'+poison+'x'.repeat(9000)+'"}',{headers:{'Content-Type':'application/json'}}),
        new Response(poison,{headers:{'Content-Type':'text/html'}})]){
        const result=await inspectOAuthDiscovery(resource,issuer,fake({[rm]:response}));
        assert.equal(result.outcome,'resource-metadata-unavailable');
        assert.doesNotMatch(JSON.stringify(result),new RegExp(poison));
    }
});
test('missing PKCE and unbound issuer are incompatibilities, not reasons to relax validation',async()=>{
    for(const doc of [{...metadata,code_challenge_methods_supported:['plain']},{...metadata,issuer:'https://other.example'}]){
        const result=await inspectOAuthDiscovery(resource,issuer,fake({[rm]:prm(),[am]:doc}));
        assert.equal(result.outcome,'metadata-rejected');
    }
});
test('unsafe operator targets are rejected before any request',async()=>{
    for(const target of ['http://remote.example/mcp','https://user:password@mailbox.example/mcp',resource+'?token=secret']){
        let called=false;
        await assert.rejects(()=>inspectOAuthDiscovery(target,issuer,(async()=>{called=true;throw Error('unexpected');}) as typeof fetch));
        assert.equal(called,false);
    }
});

test('OIDC fallback follows both standard tenant discovery locations',async()=>{
    const inserted='https://identity.example/.well-known/openid-configuration/tenant';
    const appended=issuer+'/.well-known/openid-configuration';
    const seen:string[]=[];
    const result=await inspectOAuthDiscovery(resource,issuer,fake({[rm]:prm(),[appended]:metadata},seen));
    assert.equal(result.outcome,'metadata-compatible');assert.deepEqual(seen,[rm,am,inserted,appended]);
});
test('operator may select one trusted issuer from several without contacting the others',async()=>{
    const seen:string[]=[];
    const result=await inspectOAuthDiscovery(resource,issuer,fake({[rm]:{resource,authorization_servers:['https://other.example',issuer]},[am]:metadata},seen));
    assert.equal(result.outcome,'metadata-compatible');assert.deepEqual(seen,[rm,am]);
});
test('authorization metadata endpoints may retain existing query parameters',async()=>{
    const result=await inspectOAuthDiscovery(resource,issuer,fake({[rm]:prm(),[am]:{...metadata,authorization_endpoint:issuer+'/authorize?existing=value'}}));
    assert.equal(result.outcome,'metadata-compatible');
});


test('401 challenge may point to alternate same-origin resource metadata without vendor-specific code',async()=>{
    const access='https://mailbox.example/.well-known/cloudflare-access-protected-resource/';
    const challenge=new Response('',{status:401,headers:{'WWW-Authenticate':`Bearer realm="OAuth", resource_metadata="${access}"`}});
    const seen:string[]=[];
    const result=await inspectOAuthDiscovery(resource,issuer,fake({[rm]:challenge,[resource]:challenge,[access]:prm(),[am]:metadata},seen));
    assert.equal(result.outcome,'metadata-compatible');
    assert.equal(result.authenticated,false);
    assert.deepEqual(seen,[rm,access,am]);
});

test('401 resource metadata challenge never follows foreign origins or duplicate/unsafe locations',async()=>{
    const good='https://mailbox.example/.well-known/access-protected-resource/';
    const headers=[
        `Bearer resource_metadata="https://foreign.example/.well-known/anything"`,
        `Bearer resource_metadata="${good}", resource_metadata="${good}"`,
        `Bearer resource_metadata="https://mailbox.example@foreign.example/.well-known/x"`,
        `Bearer resource_metadata="${good}?token=secret"`,
        `Bearer resource_metadata="https://mailbox.example/unsafe"`,
    ];
    for(const value of headers){
        const seen:string[]=[];
        const challenge=new Response('',{status:401,headers:{'WWW-Authenticate':value}});
        const result=await inspectOAuthDiscovery(resource,issuer,fake({[rm]:challenge,[resource]:challenge},seen));
        assert.equal(result.outcome,'resource-metadata-unavailable',value);
        assert.equal(result.authenticated,false);
        assert.deepEqual(seen,[rm,resource],value);
    }
});

test('401 without an OAuth resource metadata challenge remains unavailable, not a successful login',async()=>{
    const seen:string[]=[];
    const challenge=new Response('{"error":"invalid_token"}',{status:401,headers:{'Content-Type':'application/json'}});
    const result=await inspectOAuthDiscovery(resource,issuer,fake({[rm]:challenge,[resource]:challenge},seen));
    assert.equal(result.outcome,'resource-metadata-unavailable');
    assert.equal(result.resourceMetadataStatus,401);
    assert.deepEqual(seen,[rm,resource]);
});


test('resource endpoint challenge is followed if standard well-known metadata is gated',async()=>{
    const custom='https://mailbox.example/.well-known/alternate-resource-metadata/';
    const protectedPath=new Response('',{status:401});
    const resourceResponse=new Response('',{status:401,headers:{'WWW-Authenticate':`Bearer realm="OAuth", resource_metadata="${custom}"`}});
    const seen:string[]=[];
    const result=await inspectOAuthDiscovery(resource,issuer,fake({[rm]:protectedPath,[resource]:resourceResponse,[custom]:prm(),[am]:metadata},seen));
    assert.equal(result.outcome,'metadata-compatible');
    assert.deepEqual(seen,[rm,resource,custom,am]);
});

// RED regression: a 401 at the public well-known URL is a live deployment
// blocker. Diagnose fixed same-origin GET routes without forwarding secrets.
test('gated OAuth discovery records root routing and challenge scheme without exposing headers',async()=>{
    const seen:string[]=[];
    const root='https://mailbox.example/.well-known/oauth-protected-resource';
    const home='https://mailbox.example/';
    const secret='SENSITIVE_REALM_MUST_NEVER_LEAK';
    const result=await (inspectOAuthDiscovery as Function)(resource,issuer,fake({
        [rm]:new Response('',{status:401,headers:{'WWW-Authenticate':`Basic realm="${secret}"`}}),
        [resource]:new Response('',{status:405,headers:{Allow:'POST'}}),
        [root]:new Response('',{status:401}),
        [home]:new Response('<html>public homepage</html>',{headers:{'Content-Type':'text/html'}})
    },seen),{diagnoseGateway:true});
    assert.equal(result.outcome,'resource-metadata-unavailable');
    assert.equal(result.authenticated,false);
    assert.deepEqual(result.routeDiagnostics,{rootMetadataStatus:401,resourceGetStatus:405,homepageStatus:200,challengeScheme:'other'});
    assert.doesNotMatch(JSON.stringify(result),/SENSITIVE_REALM_MUST_NEVER_LEAK/);
    assert.deepEqual(seen,[rm,resource,root,home]);
});

test('optional gated-route diagnostics never contact extra endpoints on valid OAuth metadata',async()=>{
    const seen:string[]=[];
    const result=await (inspectOAuthDiscovery as Function)(resource,issuer,fake({[rm]:prm(),[am]:metadata},seen),{diagnoseGateway:true});
    assert.equal(result.outcome,'metadata-compatible');
    assert.equal(result.routeDiagnostics,undefined);
    assert.deepEqual(seen,[rm,am]);
});
