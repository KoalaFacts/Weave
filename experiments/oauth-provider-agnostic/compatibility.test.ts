/** Synthetic issuers over real loopback HTTP; NOT Cloudflare/Auth0 login or live agents. */
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {createHash,randomUUID} from 'node:crypto';
import {test} from 'node:test';
import {authorizeMcpAccount} from '../../examples/remote-mcp-pair/oauth.ts';
import {authorizeOAuth,type OAuthConfiguration} from '../../hosts/Weave.Mailbox.Worker/src/remote-oauth.ts';

const account='01234567-89ab-4def-8123-456789abcdef';
const scope='weave:messages.read';
async function provider(label:string,token:string,lifetime=900,subject=account,
    discovery:'oauth'|'oidc-inserted'|'oidc-appended'|'oidc-blocked'='oauth',
    resourceDiscovery:'path'|'root'|'multiple'='path'){
    let origin='',active=true;
    let codeRecord:{code:string;challenge:string;redirect:string;client:string}|undefined;
    const counts={authorization:0,exchange:0,introspection:0};
    const server=createServer((req,res)=>{void(async()=>{
        const url=new URL(req.url??'/',origin),issuer=origin+'/'+label,resource=origin+'/mcp';
        const reply=(value:unknown,status=200)=>{res.writeHead(status,{'Content-Type':'application/json','Cache-Control':'no-store'});res.end(JSON.stringify(value));};
        const resourceMetadataPath=resourceDiscovery==='root'
            ?'/.well-known/oauth-protected-resource'
            :'/.well-known/oauth-protected-resource/mcp';
        if(req.method==='GET'&&url.pathname===resourceMetadataPath){
            reply({resource,authorization_servers:resourceDiscovery==='multiple'
                ?['http://127.0.0.1:9/other-issuer',issuer]:[issuer]});return;
        }
        const oauthPath='/.well-known/oauth-authorization-server/'+label;
        if(discovery==='oidc-blocked'&&req.method==='GET'&&url.pathname===oauthPath){
            reply({error:'operator-denied'},401);return;
        }
        const discoveryPath=discovery==='oauth'?oauthPath:
            discovery==='oidc-inserted'? '/.well-known/openid-configuration/'+label:
            '/'+label+'/.well-known/openid-configuration';
        if(req.method==='GET'&&url.pathname===discoveryPath){
            reply({issuer,authorization_endpoint:issuer+'/authorize',token_endpoint:issuer+'/token',
                response_types_supported:['code'],grant_types_supported:['authorization_code'],
                code_challenge_methods_supported:['S256'],authorization_response_iss_parameter_supported:true});return;
        }
        if(req.method==='GET'&&url.pathname==='/'+label+'/authorize'){
            assert.equal(url.searchParams.get('response_type'),'code');
            assert.equal(url.searchParams.get('resource'),resource);
            assert.equal(url.searchParams.get('client_id'),label+'-client');
            assert.equal(url.searchParams.get('code_challenge_method'),'S256');
            assert.equal(url.searchParams.get('scope'),scope);
            assert.equal(url.searchParams.has('code_verifier'),false);
            const callback=new URL(url.searchParams.get('redirect_uri')!);
            assert.equal(callback.origin.startsWith('http://127.0.0.1:'),true);assert.equal(callback.pathname,'/callback');
            const code=randomUUID();codeRecord={code,challenge:url.searchParams.get('code_challenge')!,redirect:callback.href,client:label+'-client'};
            callback.searchParams.set('state',url.searchParams.get('state')!);callback.searchParams.set('code',code);callback.searchParams.set('iss',issuer);
            counts.authorization++;res.writeHead(302,{Location:callback.href});res.end();return;
        }
        if(req.method==='POST'&&['/'+label+'/token','/'+label+'/introspect'].includes(url.pathname)){
            let body='';for await(const chunk of req){body+=String(chunk);assert.ok(body.length<=8192);}
            const form=new URLSearchParams(body);
            if(url.pathname.endsWith('/token')){
                assert.ok(codeRecord);assert.equal(form.get('code'),codeRecord.code);
                assert.equal(form.get('client_id'),codeRecord.client);assert.equal(form.get('redirect_uri'),codeRecord.redirect);
                assert.equal(form.get('grant_type'),'authorization_code');assert.equal(form.get('resource'),resource);
                assert.equal(form.has('client_secret'),false);
                assert.equal(createHash('sha256').update(form.get('code_verifier')!).digest('base64url'),codeRecord.challenge);
                codeRecord=undefined;counts.exchange++;reply({access_token:token,token_type:'Bearer',expires_in:lifetime,scope});return;
            }
            counts.introspection++;
            assert.equal(req.headers.authorization,'Basic '+Buffer.from('fixture-client:synthetic-fixture-secret').toString('base64'));
            const now=Math.floor(Date.now()/1000);
            reply({active:active&&form.get('token')===token,iss:issuer,aud:resource,sub:subject,
                exp:now+lifetime,iat:now,scope});return;
        }
        reply({error:'not_found'},404);
    })().catch(()=>{res.writeHead(500,{'Content-Type':'application/json'});res.end('{"error":"fixture_contract_failed"}');});});
    await new Promise<void>((resolve,reject)=>{server.once('error',reject);server.listen(0,'127.0.0.1',resolve);});
    const address=server.address();assert.ok(address&&typeof address==='object');origin='http://127.0.0.1:'+address.port;
    const resource=origin+'/mcp',issuer=origin+'/'+label;
    const config:OAuthConfiguration={issuer,resource,introspectionUrl:issuer+'/introspect',clientId:'fixture-client',clientSecret:'synthetic-fixture-secret'};
    return {issuer,resource,config,counts,revoke:()=>{active=false;},
        login:(expectedIssuer?:string)=>authorizeMcpAccount(resource,label+'-client',async(url)=>{
            const auth=await fetch(url,{redirect:'manual'});assert.equal(auth.status,302);
            await auth.body?.cancel();const callback=auth.headers.get('location');assert.ok(callback);
            const response=await fetch(callback,{redirect:'error'});assert.equal(response.status,200);await response.body?.cancel();
        },{allowLoopbackForTests:true,scopes:[scope],timeoutMs:5000,
            ...(expectedIssuer===undefined?{}:{expectedIssuer})}),
        close:()=>new Promise<void>(resolve=>{server.close(()=>resolve());server.closeAllConnections();})};
}
for(const [label,token] of [['issuer-a','opaque.alpha_123'],['issuer-b','opaque.beta~456']]){
    test('unchanged client and RFC7662 resource validate independent '+label+' over HTTP',async(t)=>{
        const p=await provider(label,token);let mapped=0;
        try{
            const actual=await p.login();assert.equal(actual,token);
            const request=new Request(p.resource,{headers:{Authorization:'Bearer '+actual}});
            const verify=()=>authorizeOAuth(p.config,request,scope,async(id)=>{mapped++;return id===account;});
            assert.deepEqual(await verify(),{accountId:account});assert.equal(mapped,1);
            p.revoke();const denied=await verify();assert.ok(denied instanceof Response);assert.equal(denied.status,401);
            assert.equal(mapped,1);assert.deepEqual(p.counts,{authorization:1,exchange:1,introspection:2});
            t.diagnostic('Real loopback HTTP discovery, PKCE, resource-bound exchange and per-call revocation; synthetic issuer only.');
        }finally{await p.close();}
    });
}

for(const discovery of ['oidc-inserted','oidc-appended'] as const){
    test('existing remote MCP client must accept a trusted '+discovery+' OIDC-only issuer via PKCE',async()=>{
        const p=await provider('oidc-'+discovery,'opaque.oidc-'+discovery,900,account,discovery);
        try{
            const token=await p.login();
            assert.equal(token,'opaque.oidc-'+discovery);
            const request=new Request(p.resource,{headers:{Authorization:'Bearer '+token}});
            const access=await authorizeOAuth(p.config,request,scope,async(id)=>id===account);
            assert.deepEqual(access,{accountId:account});
            assert.deepEqual(p.counts,{authorization:1,exchange:1,introspection:1});
        }finally{await p.close();}
    });
}

test('RFC 9728 root protected-resource metadata fallback completes the existing PKCE and introspection flow',async()=>{
    const p=await provider('root-prm','opaque.root-prm',900,account,'oauth','root');
    try{
        const token=await p.login();
        assert.equal(token,'opaque.root-prm');
        assert.deepEqual(await authorizeOAuth(p.config,new Request(p.resource,{headers:{Authorization:'Bearer '+token}}),
            scope,async(id)=>id===account),{accountId:account});
        assert.deepEqual(p.counts,{authorization:1,exchange:1,introspection:1});
    }finally{await p.close();}
});

test('multiple authorized issuers require an exact operator selected issuer before network login',async()=>{
    const p=await provider('several-issuers','opaque.multi-issuer',900,account,'oauth','multiple');
    try{
        await assert.rejects(p.login(),/issuer|approval|unambiguous/i);
        await assert.rejects(p.login(p.issuer+'/foreign'),/issuer|approval|unambiguous/i);
        assert.deepEqual(p.counts,{authorization:0,exchange:0,introspection:0});
        const token=await p.login(p.issuer);
        assert.equal(token,'opaque.multi-issuer');
        assert.deepEqual(await authorizeOAuth(p.config,new Request(p.resource,{headers:{Authorization:'Bearer '+token}}),
            scope,async(id)=>id===account),{accountId:account});
        assert.deepEqual(p.counts,{authorization:1,exchange:1,introspection:1});
    }finally{await p.close();}
});

test('authorization-server 401 does not trigger a fallback or open a login page',async()=>{
    const p=await provider('oidc-blocked','opaque.oidc-blocked',900,account,'oidc-blocked');
    try{
        await assert.rejects(p.login(),/metadata or token response unavailable/);
        assert.deepEqual(p.counts,{authorization:0,exchange:0,introspection:0});
    }finally{await p.close();}
});

test('Access-shaped opaque token reaches the client but is not silently treated as RFC7662 or a trusted header',async()=>{
    const p=await provider('gateway-fixture','oauth:synthetic-opaque');
    try{
        const token=await p.login();assert.equal(token,'oauth:synthetic-opaque');
        const result=await authorizeOAuth(p.config,new Request(p.resource,{headers:{Authorization:'Bearer '+token}}),scope,async()=>true);
        assert.ok(result instanceof Response);assert.equal(result.status,401);assert.equal(p.counts.introspection,0);
        const forged=await authorizeOAuth(p.config,new Request(p.resource,{headers:{'Cf-Access-Jwt-Assertion':'synthetic-forgery'}}),scope,async()=>true);
        assert.ok(forged instanceof Response);assert.equal(forged.status,401);
    }finally{await p.close();}
});
test('the current 900-second client profile rejects a longer vendor session without relaxing policy',async()=>{
    const p=await provider('long-session','synthetic-token',3600);
    try{await assert.rejects(p.login,/Invalid OAuth token response/);assert.equal(p.counts.exchange,1);}
    finally{await p.close();}
});
test('external non-UUID subject fails closed rather than being guessed from email or another account',async()=>{
    const p=await provider('external-subject','synthetic-token',900,'external-user-42');
    try{
        const token=await p.login();let lookup=false;
        const result=await authorizeOAuth(p.config,new Request(p.resource,{headers:{Authorization:'Bearer '+token}}),scope,async()=>{lookup=true;return true;});
        assert.ok(result instanceof Response);assert.equal(result.status,401);assert.equal(lookup,false);
        assert.equal(p.counts.introspection,1);
    }finally{await p.close();}
});
