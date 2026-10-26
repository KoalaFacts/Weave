import assert from 'node:assert/strict';
import {test} from 'node:test';
import {start} from './harness.ts';
import {createFetchMock} from 'miniflare';

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

test('real D1 external-subject links require an owner and revoke immediately without sharing control',async()=>{
    const h=await start({bindings:{OAUTH_IDENTITY_LINKS_ENABLED:'true'}});
    try{
        const {build}=await import('esbuild');
        const buildResult=await build({entryPoints:['src/oauth-identity-links.ts'],bundle:true,write:false,
            platform:'node',format:'esm',logLevel:'silent'});
        const bundled='data:text/javascript;base64,'+Buffer.from(buildResult.outputFiles[0].contents).toString('base64');
        const {linkOAuthSubject,resolveLinkedOAuthSubject,revokeOAuthLink,oauthSubjectDigest}=await import(bundled) as typeof import('../src/oauth-identity-links.ts');
        const a=await h.register(),b=await h.register();
        const first={issuer:'https://issuer.example',resource:'https://localhost/mcp',subject:'provider-only-opaque-subject'};
        assert.equal(await resolveLinkedOAuthSubject(h.db,first),null);
        const linked=await linkOAuthSubject(h.db,a.accountId,first);
        assert.equal(linked.subjectDigest,await oauthSubjectDigest(first));
        assert.doesNotMatch(JSON.stringify(linked),/provider-only-opaque-subject/);
        assert.equal(await resolveLinkedOAuthSubject(h.db,first),a.accountId);
        await assert.rejects(linkOAuthSubject(h.db,b.accountId,first),/conflict/);
        const bList=await h.mf.dispatchFetch('https://localhost/v1/accounts/oauth/links',{headers:{'X-Weave-Account-Control':b.control}});
        assert.equal(bList.status,200);assert.deepEqual((await bList.json()).items,[]);
        const aList=await h.mf.dispatchFetch('https://localhost/v1/accounts/oauth/links',{headers:{'X-Weave-Account-Control':a.control}});
        assert.equal(aList.status,200);
        assert.deepEqual((await aList.json()).items,[{...linked,active:true}]);
        const otherRevoke=await h.mf.dispatchFetch('https://localhost/v1/accounts/oauth/links/revoke',{
            method:'POST',headers:{'Content-Type':'application/json','X-Weave-Account-Control':b.control},
            body:JSON.stringify(linked)});
        assert.equal(otherRevoke.status,404);
        const revoked=await h.mf.dispatchFetch('https://localhost/v1/accounts/oauth/links/revoke',{
            method:'POST',headers:{'Content-Type':'application/json','X-Weave-Account-Control':a.control},
            body:JSON.stringify(linked)});
        assert.equal(revoked.status,200,await revoked.clone().text());
        assert.equal(await resolveLinkedOAuthSubject(h.db,first),null);
        const again=await revokeOAuthLink(h.db,a.accountId,linked);
        assert.equal(again.active,false);
        assert.equal(await h.db.prepare('SELECT count(*) n FROM oauth_identity_links').first('n'),1,
            'revocation retains nonsecret binding history without granting access');
    }finally{await h.close();}
});
test('real D1 map supplies only an explicitly linked RFC 7662 subject to resource authorization',async()=>{
    const h=await start({bindings:{OAUTH_IDENTITY_LINKS_ENABLED:'true'}});
    try{
        const {build}=await import('esbuild');
        const compiled=await build({entryPoints:['src/oauth-identity-links.ts'],bundle:true,write:false,
            platform:'node',format:'esm',logLevel:'silent'});
        const {linkOAuthSubject,resolveLinkedOAuthSubject,revokeOAuthLink}=await import(
            'data:text/javascript;base64,'+Buffer.from(compiled.outputFiles[0].contents).toString('base64')) as typeof import('../src/oauth-identity-links.ts');
        const {authorizeOAuth}=await import('../src/remote-oauth.ts');
        const account=await h.register(),other=await h.register();
        const now=Math.floor(Date.now()/1000);
        const issuer='https://issuer.example',resource='https://localhost/mcp',sub='external-actor-X';
        const cfg={issuer,resource,introspectionUrl:issuer+'/introspect',clientId:'weave-client',clientSecret:'safe-test-secret-secret'};
        const request=new Request(resource,{method:'POST',headers:{Authorization:'Bearer synthetic-test-token'}});
        const info={active:true,iss:issuer,aud:resource,sub,iat:now-5,exp:now+120,scope:'weave:messages.read'};
        let checks=0;
        const introspect= (async(_url:unknown,options:any)=>{checks++;
            assert.equal(options.body.get('token'),'synthetic-test-token');
            assert.equal(options.headers['X-Weave-Account-Control'],undefined);
            return new Response(JSON.stringify(info),{headers:{'Content-Type':'application/json'}});
        }) as typeof fetch;
        const exists=async(value:string)=>
            (await h.db.prepare('SELECT account_id FROM accounts WHERE account_id=?').bind(value).first('account_id'))===value;
        const resolve=(value:any)=>resolveLinkedOAuthSubject(h.db,value);
        const before=await authorizeOAuth(cfg,request,'weave:messages.read',exists,introspect,now*1000,resolve);
        assert.ok(before instanceof Response);assert.equal(before.status,401);
        const key=await linkOAuthSubject(h.db,account.accountId,{issuer,resource,subject:sub});
        assert.deepEqual(await authorizeOAuth(cfg,request,'weave:messages.read',exists,introspect,now*1000,resolve),
            {accountId:account.accountId});
        const missingScope=await authorizeOAuth(cfg,request,'weave:messages.send',exists,introspect,now*1000,resolve);
        assert.ok(missingScope instanceof Response);assert.equal(missingScope.status,403);
        const forged={...info,sub:other.accountId};
        const forgedFetch=(async()=>new Response(JSON.stringify(forged),{headers:{'Content-Type':'application/json'}})) as typeof fetch;
        const spoof=await authorizeOAuth(cfg,request,'weave:messages.read',exists,forgedFetch,now*1000,resolve);
        assert.ok(spoof instanceof Response);assert.equal(spoof.status,401);
        await revokeOAuthLink(h.db,account.accountId,key);
        const after=await authorizeOAuth(cfg,request,'weave:messages.read',exists,introspect,now*1000,resolve);
        assert.ok(after instanceof Response);assert.equal(after.status,401);
        assert.ok(checks>=4,'every call must freshly query the configured AS');
    }finally{await h.close();}
});
test('link POST cannot be driven by first-party control alone or OAuth bearer alone',async()=>{
    const h=await start({bindings:{
        OAUTH_IDENTITY_LINKS_ENABLED:'true',OAUTH_ISSUER:'https://issuer.example',
        OAUTH_RESOURCE:'https://localhost/mcp',OAUTH_INTROSPECTION_URL:'https://issuer.example/introspect',
        OAUTH_CLIENT_ID:'weave-client',OAUTH_CLIENT_SECRET:'synthetic-test-secret-for-issuer'
    }});
    try{
        const a=await h.register();
        const url='https://localhost/v1/accounts/oauth/links';
        const noControl=await h.mf.dispatchFetch(url,{method:'POST',headers:{'Content-Type':'application/json',Authorization:'Bearer synthetic-token'},body:'{}'});
        assert.equal(noControl.status,401);
        const noToken=await h.mf.dispatchFetch(url,{method:'POST',headers:{'Content-Type':'application/json','X-Weave-Account-Control':a.control},body:'{}'});
        assert.equal(noToken.status,401);
        assert.match(noToken.headers.get('WWW-Authenticate')??'',/resource_metadata=/);
        assert.equal(await h.db.prepare('SELECT count(*) n FROM oauth_identity_links').first('n'),0);
    }finally{await h.close();}
});

test('real workerd binds a validated external RFC7662 bearer to its consenting owner and denies after revoke',async()=>{
    const issuer='https://issuer.example',resource='https://localhost/mcp';
    const token='synthetic-public-client-token';
    const now=Math.floor(Date.now()/1000);
    const fetchMock=createFetchMock();
    fetchMock.disableNetConnect();
    const metadata={active:true,iss:issuer,aud:resource,sub:'separate-external-subject',
        iat:now-5,exp:now+120,scope:'weave:addresses.read'};
    fetchMock.get(issuer).intercept({method:'POST',path:'/introspect'}).reply(200,JSON.stringify(metadata),{
        headers:{'Content-Type':'application/json'}
    }).persist();
    const h=await start({fetchMock,bindings:{
        OAUTH_IDENTITY_LINKS_ENABLED:'true',
        OAUTH_ISSUER:issuer,OAUTH_RESOURCE:resource,OAUTH_INTROSPECTION_URL:issuer+'/introspect',
        OAUTH_CLIENT_ID:'test-public-client',OAUTH_CLIENT_SECRET:'synthetic-test-secret-for-introspection'
    }});
    try{
        const owner=await h.register(),other=await h.register();
        const rpc=()=>h.mf.dispatchFetch(resource,{method:'POST',headers:{
            Authorization:'Bearer '+token,'Content-Type':'application/json',Accept:'application/json, text/event-stream'
        },body:JSON.stringify({jsonrpc:'2.0',id:17,method:'tools/call',
            params:{name:'get_my_profile',arguments:{}}})});
        const before=await rpc();
        assert.equal(before.status,401,'external bearer alone cannot claim an account');
        const link=async(control:string)=>h.mf.dispatchFetch('https://localhost/v1/accounts/oauth/links',{
            method:'POST',headers:{'X-Weave-Account-Control':control,Authorization:'Bearer '+token,
                'Content-Type':'application/json'},body:'{}'});
        const linked=await link(owner.control);
        assert.equal(linked.status,200,await linked.clone().text());
        const key=await linked.json();
        assert.deepEqual(Object.keys(key).sort(),['issuer','resource','subjectDigest'].sort());
        assert.doesNotMatch(JSON.stringify(key),/separate-external-subject|synthetic-public-client-token/);
        const profile=await rpc();
        assert.equal(profile.status,200,await profile.clone().text());
        const message=await profile.json();
        assert.equal(JSON.parse(message.result.content[0].text).id,owner.accountId);
        const foreign=await link(other.control);
        assert.equal(foreign.status,409,'same external identity cannot be reassigned across accounts');
        const revoked=await h.mf.dispatchFetch('https://localhost/v1/accounts/oauth/links/revoke',{
            method:'POST',headers:{'X-Weave-Account-Control':owner.control,'Content-Type':'application/json'},
            body:JSON.stringify(key)});
        assert.equal(revoked.status,200,await revoked.clone().text());
        assert.equal((await rpc()).status,401,'revocation must take effect on the next real private MCP call');
    }finally{await h.close();}
});
