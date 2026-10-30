import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createServer } from 'node:http';
import { test } from 'node:test';
import { authorizeMcpAccount } from '../../../examples/remote-mcp-pair/oauth.ts';

async function fixture(options:{wrongIssuer?:boolean;noPkce?:boolean;badToken?:boolean}={}) {
    let expectedChallenge='',tokenCalls=0,callbacks=0;
    const server=createServer(async(req,res)=>{
        const u=new URL(req.url??'/', 'http://127.0.0.1');
        res.setHeader('Content-Type','application/json');
        if(u.pathname==='/.well-known/oauth-protected-resource/mcp'){
            res.end(JSON.stringify({resource:`http://127.0.0.1:${(server.address() as any).port}/mcp`,authorization_servers:[`http://127.0.0.1:${(server.address() as any).port}`]}));return;
        }
        if(u.pathname==='/.well-known/oauth-authorization-server'){
            const base=`http://127.0.0.1:${(server.address() as any).port}`;
            res.end(JSON.stringify({issuer:options.wrongIssuer?'http://evil.invalid':base,authorization_endpoint:base+'/authorize',token_endpoint:base+'/token',
                response_types_supported:['code'],grant_types_supported:['authorization_code'],code_challenge_methods_supported:options.noPkce?['plain']:['S256']}));return;
        }
        if(u.pathname==='/token'){
            tokenCalls++;
            const chunks:Uint8Array[]=[];for await(const x of req)chunks.push(x);
            const args=new URLSearchParams(Buffer.concat(chunks).toString());
            assert.equal(args.get('grant_type'),'authorization_code');
            assert.equal(args.get('client_id'),'test-public-client');
            assert.equal(args.get('code'),'synthetic-code');
            assert.equal(args.get('resource'),`http://127.0.0.1:${(server.address() as any).port}/mcp`);
            assert.equal(args.get('code_challenge'),null);
            assert.equal(createHash('sha256').update(args.get('code_verifier')??'').digest('base64url'),expectedChallenge);
            assert.match(args.get('redirect_uri')??'',/^http:\/\/127\.0\.0\.1:\d+\/callback$/);
            res.end(JSON.stringify(options.badToken?{token_type:'Bearer',expires_in:300}:{access_token:'synthetic-access-token',token_type:'Bearer',expires_in:300}));return;
        }
        res.statusCode=404;res.end('{}');
    });
    await new Promise<void>(resolve=>server.listen(0,'127.0.0.1',resolve));
    const resource=`http://127.0.0.1:${(server.address() as any).port}/mcp`;
    return {resource,get calls(){return tokenCalls},get callbacks(){return callbacks},onLink:async(link:string,wrongState=false)=>{
        const u=new URL(link);callbacks++;
        assert.equal(u.pathname,'/authorize');
        assert.equal(u.searchParams.get('response_type'),'code');
        assert.equal(u.searchParams.get('resource'),resource);
        assert.equal(u.searchParams.get('client_id'),'test-public-client');
        assert.equal(u.searchParams.get('code_challenge_method'),'S256');
        assert.equal(u.searchParams.get('scope'),'weave:addresses.read weave:messages.send');
        expectedChallenge=u.searchParams.get('code_challenge')??'';
        assert.match(expectedChallenge,/^[A-Za-z0-9_-]{43}$/);
        const redirect=u.searchParams.get('redirect_uri');assert.ok(redirect);
        const callback=new URL(redirect);
        callback.searchParams.set('code','synthetic-code');
        callback.searchParams.set('state',wrongState?'tampered':u.searchParams.get('state')??'');
        const answer=await fetch(callback);
        assert.equal(answer.headers.get('cache-control'),'no-store');
        assert.ok(wrongState?answer.status===400:answer.status===200);
    },close:()=>new Promise<void>(resolve=>server.close(()=>resolve()))};
}

test('OAuth authorization-code client uses metadata, exact resource, S256 PKCE and local callback without exposing tokens',async()=>{
    const h=await fixture();
    try{
        const result=await authorizeMcpAccount(h.resource,'test-public-client',url=>h.onLink(url),{allowLoopbackForTests:true,
            scopes:['weave:addresses.read','weave:messages.send'],timeoutMs:1000});
        assert.equal(result,'synthetic-access-token');assert.equal(h.calls,1);assert.equal(h.callbacks,1);
    }finally{await h.close();}
});

test('OAuth callback rejects mismatched state before token exchange',async()=>{
    const h=await fixture();
    try{
        await assert.rejects(authorizeMcpAccount(h.resource,'test-public-client',url=>h.onLink(url,true),{allowLoopbackForTests:true,
            scopes:['weave:addresses.read','weave:messages.send'],timeoutMs:1000}),/state|callback/i);
        assert.equal(h.calls,0);
    }finally{await h.close();}
});

for(const settings of [{wrongIssuer:true},{noPkce:true}]){
    test('OAuth fails closed before opening consent for untrusted issuer or missing PKCE S256',async()=>{
        const h=await fixture(settings);
        try{
            await assert.rejects(authorizeMcpAccount(h.resource,'test-public-client',url=>h.onLink(url),{allowLoopbackForTests:true,
                scopes:['weave:addresses.read','weave:messages.send'],timeoutMs:1000}));
            assert.equal(h.calls,0);assert.equal(h.callbacks,0);
        }finally{await h.close();}
    });
}

test('OAuth rejects malformed token response without leaking it',async()=>{
    const h=await fixture({badToken:true});
    try{
        await assert.rejects(authorizeMcpAccount(h.resource,'test-public-client',url=>h.onLink(url),{allowLoopbackForTests:true,
            scopes:['weave:addresses.read','weave:messages.send'],timeoutMs:1000}),/token response/i);
        assert.equal(h.calls,1);
    }finally{await h.close();}
});

test('OAuth refuses insecure non-loopback endpoints without attempting metadata requests',async()=>{
    await assert.rejects(authorizeMcpAccount('http://example.invalid/mcp','test-public-client',async()=>{}),/HTTPS/i);
});
