import assert from 'node:assert/strict';
import {test} from 'node:test';
import {start,payload} from './harness.ts';
import {randomUUID} from 'node:crypto';
import {mkdtemp,rm} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {join} from 'node:path';

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
    const metadata={active:true,iss:issuer,aud:resource,sub:'separate-external-subject',
        iat:now-5,exp:now+120,scope:'weave:addresses.read'};
    let validations=0;
    const observed:Array<{target:boolean;method:boolean;basic:boolean;token:boolean}>=[];
    // Capture only booleans for safe failure diagnosis; the final assertions
    // reject every unexpected destination, method, credential and token.
    const outboundService=async(request:Request):Promise<Response>=>{
        const url=new URL(request.url);
        const form=new URLSearchParams(await request.text());
        observed.push({target:url.href===issuer+'/introspect',method:request.method==='POST',
            basic:/^Basic /.test(request.headers.get('Authorization')??''),
            token:form.get('token')===token});
        validations++;
        return new Response(JSON.stringify(metadata),{headers:{'Content-Type':'application/json'}});
    };
    const h=await start({outboundService,bindings:{
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
        assert.equal(before.status,401,'external bearer alone cannot claim an account; outbound='+JSON.stringify(observed));
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
        assert.ok(validations>=5,'the real Worker must freshly introspect every authorized or denied call');
        assert.ok(observed.every(x=>x.target&&x.method&&x.basic&&x.token),'the configured issuer, POST, Basic authentication and bearer must all match');
    }finally{await h.close();}
});

test('two separate external OAuth identities complete real D1 private MCP contact and six-message ACK recovery',async()=>{
    const issuer='https://issuer.example',resource='https://localhost/mcp';
    const introspectionOrigin='https://trusted-token-service.example';
    const introspectionUrl=introspectionOrigin+'/introspect';
    const accessA='opaque-owner-A',accessB='opaque-owner-B';
    const subjectA='issuer-local-A',subjectB='issuer-local-B';
    const allScopes='weave:addresses.read weave:addresses.write weave:contacts.read weave:contacts.write weave:messages.read weave:messages.send weave:messages.ack';
    const subjects=new Map([[accessA,subjectA],[accessB,subjectB]]);
    const observed=new Map<string,number>();
    const outboundService=async(request:Request):Promise<Response>=>{
        assert.equal(request.url,introspectionUrl,'tokens go only to the exact operator-approved HTTPS introspection endpoint');
        assert.equal(request.method,'POST');
        assert.match(request.headers.get('Authorization')??'',/^Basic /);
        assert.equal(request.headers.has('X-Weave-Account-Control'),false,
            'long-lived Weave controls must never reach the AS');
        const token=new URLSearchParams(await request.text()).get('token')??'';
        observed.set(token,(observed.get(token)??0)+1);
        const sub=subjects.get(token);
        const now=Math.floor(Date.now()/1000);
        return new Response(JSON.stringify(sub?{
            active:true,iss:issuer,aud:resource,sub,
            iat:now-5,exp:now+120,scope:allScopes
        }:{active:false}),{headers:{'Content-Type':'application/json'}});
    };
    const persist=await mkdtemp(join(tmpdir(),'weave-external-oauth-mcp-'));
    const bindings={OAUTH_IDENTITY_LINKS_ENABLED:'true',OAUTH_ISSUER:issuer,
        OAUTH_RESOURCE:resource,OAUTH_INTROSPECTION_URL:introspectionUrl,
        OAUTH_INTROSPECTION_TRUSTED_ORIGIN:introspectionOrigin,
        OAUTH_CLIENT_ID:'protocol-client',OAUTH_CLIENT_SECRET:'isolated-test-only-client-secret'};
    let h=await start({persist,outboundService,bindings});
    let sequence=200;
    type Delivery={sender:string;recipient:string;token:string;receiverToken:string;payload:ReturnType<typeof payload>};
    const messages:Delivery[]=[];
    const rawCall=(token:string,name:string,args:Record<string,unknown>={})=>
        h.mf.dispatchFetch(resource,{method:'POST',headers:{
            Authorization:'Bearer '+token,'Content-Type':'application/json',
            Accept:'application/json, text/event-stream','MCP-Protocol-Version':'2025-11-25'
        },body:JSON.stringify({jsonrpc:'2.0',id:sequence++,method:'tools/call',
            params:{name,arguments:args}})});
    const call=async(token:string,name:string,args:Record<string,unknown>={}):Promise<any>=>{
        const response=await rawCall(token,name,args);
        assert.equal(response.status,200,'private tool must reach real workerd: '+name);
        const frame=await response.json() as {result?:{
            isError?:boolean;structuredContent?:unknown;content?:{text:string}[]}};
        assert.ok(frame.result&&!frame.result.isError,'MCP tool failed: '+name);
        return frame.result.structuredContent??JSON.parse(frame.result.content![0].text);
    };
    let disposed=false;
    try{
        const a=await h.register(),b=await h.register();
        assert.notEqual(a.accountId,b.accountId);
        const denied=await rawCall(accessA,'get_my_profile');
        assert.equal(denied.status,401,'external bearer without first-party link is not account control');
        const link=async(owner:{control:string},token:string)=>{
            const response=await h.mf.dispatchFetch('https://localhost/v1/accounts/oauth/links',{
                method:'POST',headers:{Authorization:'Bearer '+token,
                    'X-Weave-Account-Control':owner.control,'Content-Type':'application/json'},body:'{}'});
            assert.equal(response.status,200,'owner+AS token link must succeed');
            return response.json() as Promise<{issuer:string;resource:string;subjectDigest:string}>;
        };
        const linkA=await link(a,accessA),linkB=await link(b,accessB);
        assert.notEqual(linkA.subjectDigest,linkB.subjectDigest);
        assert.deepEqual(await call(accessA,'get_my_profile'),{id:a.accountId});
        assert.deepEqual(await call(accessB,'get_my_profile'),{id:b.accountId});
        const addressA=randomUUID(),addressB=randomUUID();
        await call(accessA,'ensure_my_address',{addressId:addressA,label:'External A'});
        await call(accessB,'ensure_my_address',{addressId:addressB,label:'External B'});
        const addressesA=await call(accessA,'list_my_addresses');
        assert.ok(addressesA.items.some((x:{addressId:string})=>x.addressId===addressA));
        assert.ok(!addressesA.items.some((x:{addressId:string})=>x.addressId===addressB));
        const intro=payload('external-provider-introduction');
        const request=await call(accessA,'request_contact',{
            requestId:intro.messageId,requesterAddressId:addressA,
            recipientAccountId:b.accountId,recipientAddressId:addressB,payload:intro});
        const incoming=await call(accessB,'list_my_contact_requests');
        assert.ok(incoming.items.some((x:{requestId:string;status:string})=>
            x.requestId===intro.messageId&&x.status==='pending'));
        await call(accessB,'decide_contact',{
            requestId:intro.messageId,requesterAccountId:a.accountId,
            expectedGeneration:String(request.generation),status:'accepted'});
        const send=async(index:number)=>{
            const fromA=index%2===0;
            const token=fromA?accessA:accessB,receiverToken=fromA?accessB:accessA;
            const sender=fromA?a.accountId:b.accountId,recipient=fromA?b.accountId:a.accountId;
            const message=payload(index===5?'external-oauth-large-'+ 'x'.repeat(40_000):'external-oauth-message-'+index);
            const result=await call(token,'send_message',{
                recipientAccountId:recipient,contactGeneration:String(request.generation),payload:message});
            assert.equal(result.messageId,message.messageId);
            assert.equal(result.state,'pending');
            messages.push({sender,recipient,token,receiverToken,payload:message});
        };
        const acknowledge=async(item:Delivery)=>{
            const inbox=await call(item.receiverToken,'list_my_inbox');
            const delivered=inbox.items.find((x:{envelope?:{payload?:{messageId:string}}})=>
                x.envelope?.payload?.messageId===item.payload.messageId);
            assert.ok(delivered,'pending message must survive workerd/D1 cold restart');
            assert.equal(delivered.envelope.payload.bytes,item.payload.bytes,
                'the complete 40 KiB message body must be delivered without truncation');
            const result=await call(item.receiverToken,'ack_message',{
                messageId:item.payload.messageId,senderAccountId:item.sender});
            assert.equal(result.state,'acknowledged');
        };
        await send(0);
        await send(1);
        await h.close();disposed=true;
        h=await start({persist,migrate:false,outboundService,bindings});disposed=false;
        await acknowledge(messages[0]);
        await acknowledge(messages[1]);
        for(let i=2;i<6;i++){await send(i);await acknowledge(messages[i]);}
        const replay=await call(messages[0].token,'send_message',{
            recipientAccountId:messages[0].recipient,
            contactGeneration:String(request.generation),payload:messages[0].payload});
        assert.equal(replay.state,'acknowledged','duplicate delivery returns original ACK');
        const outA=await call(accessA,'list_my_outbox'),outB=await call(accessB,'list_my_outbox');
        assert.ok(outA.items.length>=3&&outB.items.length>=3);
        assert.ok((observed.get(accessA)??0)>=7&&(observed.get(accessB)??0)>=7,
            'no cached OAuth decision across independent send/read/ACK operations');
        const revoked=await h.mf.dispatchFetch('https://localhost/v1/accounts/oauth/links/revoke',{
            method:'POST',headers:{'X-Weave-Account-Control':a.control,'Content-Type':'application/json'},
            body:JSON.stringify(linkA)});
        assert.equal(revoked.status,200,'owner must be able to revoke provider subject');
        assert.equal((await rawCall(accessA,'get_my_profile')).status,401);
        assert.deepEqual(await call(accessB,'get_my_profile'),{id:b.accountId},
            'revoking A must not disable B');
    }finally{
        try {if(!disposed)await h.close();}
        finally{await rm(persist,{recursive:true,force:true});}
    }
});
