/** RFC 7591 dynamic registration with two independent synthetic HTTP issuers.
 * This tests only the protocol, not a deployed Access/Auth0 service.
 */
import assert from 'node:assert/strict';
import {createHash,randomUUID} from 'node:crypto';
import {createServer} from 'node:http';
import {test} from 'node:test';
import {authorizeMcpAccount} from '../../examples/remote-mcp-pair/oauth.ts';

async function fixture(label:string,config:{registration?:'valid'|'secret'|'wrong-redirect'|'bad-id'|'foreign'|'none'}={}){
    let origin='',registrations=0,authorizations=0,exchanges=0;
    let saved:{client:string;redirect:string;challenge:string;code:string}|undefined;
    const respond=(res:import('node:http').ServerResponse,doc:unknown,status=200)=>{
        res.writeHead(status,{'Content-Type':'application/json','Cache-Control':'no-store'});
        res.end(JSON.stringify(doc));
    };
    const server=createServer((req,res)=>{void (async()=>{
        const url=new URL(req.url??'/',origin),issuer=origin+'/'+label,resource=origin+'/mcp';
        if(req.method==='GET'&&url.pathname==='/.well-known/oauth-protected-resource/mcp'){
            respond(res,{resource,authorization_servers:[issuer]});return;
        }
        if(req.method==='GET'&&url.pathname==='/.well-known/oauth-authorization-server/'+label){
            respond(res,{issuer,authorization_endpoint:issuer+'/authorize',token_endpoint:issuer+'/token',
                ...(config.registration==='none'?{}:{registration_endpoint:config.registration==='foreign'?'http://127.0.0.2:1234/register':issuer+'/register'}),
                response_types_supported:['code'],grant_types_supported:['authorization_code'],
                code_challenge_methods_supported:['S256'],authorization_response_iss_parameter_supported:true});return;
        }
        if(req.method==='POST'&&url.pathname==='/'+label+'/register'){
            registrations++;
            let raw='';for await (const chunk of req){raw+=String(chunk);assert.ok(raw.length<=8192);}
            const registration=JSON.parse(raw) as Record<string,unknown>;
            assert.equal(req.headers['content-type'],'application/json');
            assert.deepEqual(registration.grant_types,['authorization_code']);
            assert.deepEqual(registration.response_types,['code']);
            assert.equal(registration.token_endpoint_auth_method,'none');
            assert.equal(registration.scope,'weave:messages.read');
            assert.ok(Array.isArray(registration.redirect_uris));
            assert.equal(registration.redirect_uris.length,1);
            const redirect=registration.redirect_uris[0] as string;
            assert.match(redirect,/^http:\/\/127\.0\.0\.1:[0-9]+\/callback$/);
            respond(res,{client_id:config.registration==='bad-id'?'bad\nclient':label+'-registered',
                token_endpoint_auth_method:'none',
                redirect_uris:[config.registration==='wrong-redirect'?origin+'/wrong':redirect],
                ...(config.registration==='secret'?{client_secret:'must-not-accept'}:{})},201);return;
        }
        if(req.method==='GET'&&url.pathname==='/'+label+'/authorize'){
            authorizations++;
            assert.equal(url.searchParams.get('client_id'),
                config.registration==='none'?label+'-existing':label+'-registered');
            assert.equal(url.searchParams.get('scope'),'weave:messages.read');
            assert.equal(url.searchParams.get('resource'),resource);
            assert.equal(url.searchParams.get('code_challenge_method'),'S256');
            assert.equal(url.searchParams.has('code_verifier'),false);
            const redirect=url.searchParams.get('redirect_uri');assert.ok(redirect);
            saved={client:url.searchParams.get('client_id')!,redirect,challenge:url.searchParams.get('code_challenge')!,code:randomUUID()};
            const callback=new URL(redirect);
            callback.searchParams.set('code',saved.code);
            callback.searchParams.set('state',url.searchParams.get('state')!);
            callback.searchParams.set('iss',issuer);
            res.writeHead(302,{Location:callback.href});res.end();return;
        }
        if(req.method==='POST'&&url.pathname==='/'+label+'/token'){
            exchanges++;
            assert.ok(saved);
            let raw='';for await(const chunk of req){raw+=String(chunk);assert.ok(raw.length<=8192);}
            const form=new URLSearchParams(raw);
            assert.equal(form.get('client_id'),saved.client);
            assert.equal(form.get('redirect_uri'),saved.redirect);
            assert.equal(form.get('resource'),resource);
            assert.equal(form.get('code'),saved.code);
            assert.equal(createHash('sha256').update(form.get('code_verifier')!).digest('base64url'),saved.challenge);
            assert.equal(req.headers.authorization,undefined);
            respond(res,{access_token:'opaque.'+label,token_type:'Bearer',expires_in:900});return;
        }
        respond(res,{error:'not_found'},404);
    })().catch(()=>{
        if(!res.headersSent)res.writeHead(500,{'Content-Type':'application/json'});
        res.end('{"error":"fixture_rejected"}');
    });});
    await new Promise<void>((resolve,reject)=>{server.once('error',reject);server.listen(0,'127.0.0.1',resolve);});
    const address=server.address();assert.ok(address&&typeof address==='object');
    origin='http://127.0.0.1:'+address.port;
    return {
        resource:origin+'/mcp',
        get counts(){return {registrations,authorizations,exchanges};},
        async login(clientId:string){
            return authorizeMcpAccount(origin+'/mcp',clientId,async link=>{
                const auth=await fetch(link,{redirect:'manual'});assert.equal(auth.status,302);
                const callback=auth.headers.get('location');assert.ok(callback);await auth.body?.cancel();
                const result=await fetch(callback,{redirect:'error'});assert.equal(result.status,200);await result.body?.cancel();
            },{allowLoopbackForTests:true,scopes:['weave:messages.read'],timeoutMs:5000});
        },
        close:()=>new Promise<void>(resolve=>{server.close(()=>resolve());server.closeAllConnections();})
    };
}
test('RFC7591 registers a public loopback client at the trusted issuer before PKCE consent',async()=>{
    const p=await fixture('issuer-dcr');
    try{
        assert.equal(await p.login(''),'opaque.issuer-dcr');
        assert.deepEqual(p.counts,{registrations:1,authorizations:1,exchanges:1});
    }finally{await p.close();}
});
test('an already registered public client skips dynamic registration',async()=>{
    const p=await fixture('issuer-registered',{registration:'none'});
    try{
        assert.equal(await p.login('issuer-registered-existing'),'opaque.issuer-registered');
        assert.deepEqual(p.counts,{registrations:0,authorizations:1,exchanges:1});
    }finally{await p.close();}
});
test('no advertised registration endpoint fails closed before consent or token exchange',async()=>{
    const p=await fixture('no-registration',{registration:'none'});
    try{
        await assert.rejects(p.login(''),/registration endpoint/i);
        assert.deepEqual(p.counts,{registrations:0,authorizations:0,exchanges:0});
    }finally{await p.close();}
});
test('foreign registration origin is rejected without contacting it',async()=>{
    const p=await fixture('foreign-registration',{registration:'foreign'});
    try{
        await assert.rejects(p.login(''),/outside trusted issuer origin/);
        assert.deepEqual(p.counts,{registrations:0,authorizations:0,exchanges:0});
    }finally{await p.close();}
});
for(const mode of ['secret','wrong-redirect','bad-id'] as const){
    test('malformed RFC7591 '+mode+' response never starts consent',async()=>{
        const p=await fixture('bad-'+mode,{registration:mode});
        try{
            await assert.rejects(p.login(''),/registration/i);
            assert.deepEqual(p.counts,{registrations:1,authorizations:0,exchanges:0});
        }finally{await p.close();}
    });
}
