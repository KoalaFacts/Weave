import assert from 'node:assert/strict';
import {get} from 'node:http';
import {test} from 'node:test';
import {authorizeMcpAccount, selectOAuthClientId, demoOAuthClientId} from '../../examples/remote-mcp-pair/oauth.ts';

const resource='https://demo.weave-389.workers.dev/mcp';
const issuer='https://issuer.test.example';

test('only the canonical demo with an explicitly CIMD-capable issuer selects the first-party public client',()=>{
    assert.equal(selectOAuthClientId(resource,'',true),demoOAuthClientId);
    assert.equal(selectOAuthClientId(resource,'',false),'');
    assert.equal(selectOAuthClientId(resource,'','true'),'');
    assert.equal(selectOAuthClientId('https://other.example/mcp','',true),'');
    assert.equal(selectOAuthClientId(resource,'pre-registered-client',true),'pre-registered-client');
});

test('real loopback PKCE client uses canonical CIMD ID without DCR and checks response issuer',async()=>{
    const original=globalThis.fetch;
    const calls:string[]=[];
    let tokenCalls=0;
    globalThis.fetch=async (input,init)=>{
        const url=new URL(input instanceof URL?input.href:typeof input==='string'?input:input.url);
        calls.push(url.href);
        if(url.href==='https://demo.weave-389.workers.dev/.well-known/oauth-protected-resource/mcp')
            return Response.json({resource,authorization_servers:[issuer]});
        if(url.href===issuer+'/.well-known/oauth-authorization-server')
            return Response.json({issuer,client_id_metadata_document_supported:true,
                response_types_supported:['code'],grant_types_supported:['authorization_code'],
                code_challenge_methods_supported:['S256'],authorization_response_iss_parameter_supported:true,
                authorization_endpoint:issuer+'/authorize',token_endpoint:issuer+'/oauth/token'});
        if(url.href===issuer+'/oauth/token'){
            assert.equal(init?.method,'POST');
            const body=new URLSearchParams(init?.body as string);
            assert.equal(body.get('client_id'),demoOAuthClientId);
            assert.equal(body.get('resource'),resource);
            assert.ok((body.get('code_verifier')??'').length>=43);
            assert.match(body.get('redirect_uri')??'',/^http:\/\/127\.0\.0\.1:\d+\/callback$/);
            tokenCalls++;
            return Response.json({access_token:'synthetic-access',token_type:'Bearer',expires_in:600});
        }
        throw Error('Unexpected outbound URL in CIMD PKCE test');
    };
    try{
        const token=await authorizeMcpAccount(resource,'',async authorizationUrl=>{
            const auth=new URL(authorizationUrl);
            assert.equal(auth.origin,issuer);
            assert.equal(auth.searchParams.get('client_id'),demoOAuthClientId);
            assert.equal(auth.searchParams.get('code_challenge_method'),'S256');
            assert.equal(auth.searchParams.get('resource'),resource);
            const state=auth.searchParams.get('state');
            assert.ok(state);
            const callback=new URL(auth.searchParams.get('redirect_uri')!);
            callback.searchParams.set('state',state);
            callback.searchParams.set('code','synthetic-code');
            callback.searchParams.set('iss',issuer);
            const status=await new Promise<number>((resolve,reject)=>{
                const request=get(callback,response=>{
                    response.resume();
                    response.on('end',()=>resolve(response.statusCode??0));
                    response.on('error',reject);
                });
                request.on('error',reject);
            });
            assert.equal(status,200);
        },{timeoutMs:5000});
        assert.equal(token,'synthetic-access');
        assert.equal(tokenCalls,1);
        assert.ok(!calls.some(url=>url.includes('register')),'DCR must not be invoked');
    }finally{globalThis.fetch=original;}
});
