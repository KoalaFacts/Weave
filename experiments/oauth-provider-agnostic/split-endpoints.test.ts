/** RFC 8414 permits endpoints advertised by an exact trusted issuer on different HTTPS origins.
 * This uses two independent loopback HTTP origins; it does not assert a live external login.
 */
import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';
import {createServer,type IncomingMessage,type ServerResponse} from 'node:http';
import {test} from 'node:test';
import {authorizeMcpAccount} from '../../examples/remote-mcp-pair/oauth.ts';

test('explicitly allowlisted split-origin authorization and token endpoints complete real PKCE exchange',async()=>{
    let issuerOrigin='',operationsOrigin='',issued=0,exchanged=0;
    const clientId='pre-registered-public-client';
    const scope='weave:messages.read';
    let issuedCode='',challenge='',redirect='',expectedResource='';
    const reply=(res:ServerResponse,status:number,body:unknown)=>{
        res.writeHead(status,{'Content-Type':'application/json','Cache-Control':'no-store'});
        res.end(JSON.stringify(body));
    };
    const provider=createServer((req:IncomingMessage,res:ServerResponse)=>{
        const path=new URL(req.url??'/',issuerOrigin).pathname;
        const issuer=issuerOrigin+'/tenant';
        if(req.method==='GET'&&path==='/.well-known/oauth-protected-resource/mcp'){
            reply(res,200,{resource:issuerOrigin+'/mcp',authorization_servers:[issuer]});return;
        }
        if(req.method==='GET'&&path==='/.well-known/oauth-authorization-server/tenant'){
            reply(res,200,{issuer,authorization_endpoint:operationsOrigin+'/authorize',
                token_endpoint:operationsOrigin+'/token',response_types_supported:['code'],
                grant_types_supported:['authorization_code'],code_challenge_methods_supported:['S256']});return;
        }
        reply(res,404,{error:'not_found'});
    });
    const operations=createServer((req:IncomingMessage,res:ServerResponse)=>{void(async()=>{
        const url=new URL(req.url??'/',operationsOrigin);
        if(req.method==='GET'&&url.pathname==='/authorize'){
            assert.equal(url.searchParams.get('client_id'),clientId);
            assert.equal(url.searchParams.get('resource'),expectedResource);
            assert.equal(url.searchParams.get('scope'),scope);
            assert.equal(url.searchParams.get('code_challenge_method'),'S256');
            assert.equal(url.searchParams.has('code_verifier'),false);
            assert.equal(url.searchParams.get('response_type'),'code');
            redirect=url.searchParams.get('redirect_uri')!;
            assert.ok(redirect.startsWith('http://127.0.0.1:'));
            challenge=url.searchParams.get('code_challenge')!;
            issuedCode='split-origin-opaque-code';
            const callback=new URL(redirect);
            callback.searchParams.set('state',url.searchParams.get('state')!);
            callback.searchParams.set('code',issuedCode);
            issued++;
            res.writeHead(302,{Location:callback.href});res.end();return;
        }
        if(req.method==='POST'&&url.pathname==='/token'){
            let body='';
            for await(const part of req){body+=String(part);assert.ok(body.length<=8192);}
            const params=new URLSearchParams(body);
            assert.equal(params.get('grant_type'),'authorization_code');
            assert.equal(params.get('code'),issuedCode);
            assert.equal(params.get('client_id'),clientId);
            assert.equal(params.get('redirect_uri'),redirect);
            assert.equal(params.get('resource'),expectedResource);
            assert.equal(createHash('sha256').update(params.get('code_verifier')!).digest('base64url'),challenge);
            exchanged++;
            reply(res,200,{access_token:'opaque-split-origin-token',token_type:'Bearer',expires_in:600});return;
        }
        reply(res,404,{error:'not_found'});
    })().catch(()=>reply(res,500,{error:'fixture_failed'}));});
    const listen=(server:typeof provider)=>new Promise<string>((resolve,reject)=>{
        server.once('error',reject);
        server.listen(0,'127.0.0.1',()=>{
            const address=server.address();
            if(!address||typeof address==='string'){reject(Error('Loopback server did not bind'));return;}
            resolve('http://127.0.0.1:'+address.port);
        });
    });
    const close=(server:typeof provider)=>new Promise<void>(resolve=>{server.close(()=>resolve());server.closeAllConnections();});
    try{
        issuerOrigin=await listen(provider);
        operationsOrigin=await listen(operations);
        expectedResource=issuerOrigin+'/mcp';
        let opened=0;
        const browser=async(url:string)=>{
            opened++;
            assert.equal(new URL(url).origin,operationsOrigin);
            const auth=await fetch(url,{redirect:'manual'});
            assert.equal(auth.status,302);
            await auth.body?.cancel();
            const callback=auth.headers.get('location');assert.ok(callback);
            const completed=await fetch(callback,{redirect:'error'});
            assert.equal(completed.status,200);
            await completed.body?.cancel();
        };
        await assert.rejects(
            ()=>authorizeMcpAccount(expectedResource,clientId,browser,{allowLoopbackForTests:true,
                scopes:[scope],timeoutMs:5000}),
            /outside trusted issuer origin/,
            'cross-origin endpoints must remain blocked without explicit operator approval');
        assert.equal(opened,0);assert.equal(issued,0);assert.equal(exchanged,0);
        const options={allowLoopbackForTests:true,scopes:[scope],timeoutMs:5000,
            trustedEndpointOrigins:[operationsOrigin]};
        const token=await authorizeMcpAccount(expectedResource,clientId,browser,options);
        assert.equal(token,'opaque-split-origin-token');
        assert.equal(opened,1);assert.equal(issued,1);assert.equal(exchanged,1);
    }finally{await Promise.all([close(provider),close(operations)]);}
});
