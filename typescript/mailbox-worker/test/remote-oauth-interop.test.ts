import assert from 'node:assert/strict';
import {once} from 'node:events';
import {createServer} from 'node:http';
import {connect, type Socket, type AddressInfo} from 'node:net';
import {setTimeout as delay} from 'node:timers/promises';
import {test} from 'node:test';
import {authorizeMcpAccount} from '../../../examples/remote-mcp-pair/oauth.ts';

interface FixtureOptions {
    tokenType?:string;
    omitGrantTypes?:boolean;
    unsupportedGrant?:boolean;
}
async function fixture(options:FixtureOptions={}){
    let tokenCalls=0;
    const server=createServer(async(req,res)=>{
        const address=server.address() as AddressInfo;
        const base='http://127.0.0.1:'+address.port;
        const url=new URL(req.url??'/',base);
        res.setHeader('Content-Type','application/json');
        if(url.pathname==='/.well-known/oauth-protected-resource/mcp'){
            res.end(JSON.stringify({resource:base+'/mcp',authorization_servers:[base]}));return;
        }
        if(url.pathname==='/.well-known/oauth-authorization-server'){
            res.end(JSON.stringify({
                issuer:base,authorization_endpoint:base+'/authorize',token_endpoint:base+'/token',
                response_types_supported:['code'],
                ...options.omitGrantTypes?{}:{grant_types_supported:options.unsupportedGrant?['client_credentials']:['authorization_code']},
                code_challenge_methods_supported:['S256']
            }));return;
        }
        if(url.pathname==='/token'){
            tokenCalls++;
            res.end(JSON.stringify({access_token:'safe-synthetic-token',token_type:options.tokenType??'Bearer',expires_in:300}));return;
        }
        res.statusCode=404;res.end('{}');
    });
    await new Promise<void>((resolve,reject)=>{server.once('error',reject);server.listen(0,'127.0.0.1',resolve);});
    const base='http://127.0.0.1:'+(server.address() as AddressInfo).port;
    return {
        resource:base+'/mcp',
        get tokenCalls(){return tokenCalls;},
        close:()=>new Promise<void>(resolve=>server.close(()=>resolve()))
    };
}
const login={allowLoopbackForTests:true,timeoutMs:1000,scopes:['weave:messages.send']};
const approve=async(link:string)=>{
    const authorization=new URL(link);
    const redirect=authorization.searchParams.get('redirect_uri');
    assert.ok(redirect,'authorization code must have a registered redirect');
    const callback=new URL(redirect);
    callback.searchParams.set('code','test-code');
    callback.searchParams.set('state',authorization.searchParams.get('state')??'');
    const response=await fetch(callback);
    assert.equal(response.status,200);
};
for(const tokenType of ['bearer','bEaReR']){
    test('OAuth accepts case-insensitive bearer token type '+tokenType,async()=>{
        const h=await fixture({tokenType});
        try{
            assert.equal(await authorizeMcpAccount(h.resource,'test-public-client',approve,login),'safe-synthetic-token');
            assert.equal(h.tokenCalls,1);
        }finally{await h.close();}
    });
}
test('OAuth accepts RFC8414 missing grant_types_supported default for authorization_code',async()=>{
    const h=await fixture({omitGrantTypes:true});
    try{
        assert.equal(await authorizeMcpAccount(h.resource,'test-public-client',approve,login),'safe-synthetic-token');
        assert.equal(h.tokenCalls,1);
    }finally{await h.close();}
});
test('OAuth still rejects explicit unsupported grant types before consent',async()=>{
    const h=await fixture({unsupportedGrant:true});
    let opened=false;
    try{
        await assert.rejects(authorizeMcpAccount(h.resource,'test-public-client',()=>{opened=true;},login),/PKCE|authorization-code/);
        assert.equal(opened,false);
        assert.equal(h.tokenCalls,0);
    }finally{await h.close();}
});
test('OAuth rejects non-bearer token types after code exchange',async()=>{
    const h=await fixture({tokenType:'MAC'});
    try{
        await assert.rejects(authorizeMcpAccount(h.resource,'test-public-client',approve,login),/token response/);
        assert.equal(h.tokenCalls,1);
    }finally{await h.close();}
});
test('OAuth callback timeout closes incomplete request sockets without hanging',async()=>{
    const h=await fixture();
    let incomplete:Socket|undefined;
    let outcome='pending';
    try{
        const pending=authorizeMcpAccount(h.resource,'test-public-client',async link=>{
            const redirect=new URL(new URL(link).searchParams.get('redirect_uri')??'');
            incomplete=connect(Number(redirect.port),redirect.hostname);
            // Closing the callback server intentionally resets this stalled test socket.
            incomplete.on('error',()=>{});
            await once(incomplete,'connect');
            incomplete.write('GET /callback HTTP/1.1\r\nHost: 127.0.0.1\r\n');
        },{...login,timeoutMs:100}).then(
            ()=>{outcome='unexpected-success';},
            error=>{outcome=/callback timed out/i.test(String(error))?'timeout':'unexpected-error';}
        );
        await delay(550);
        const resultAtDeadline=outcome;
        incomplete?.destroy();
        await pending;
        assert.equal(resultAtDeadline,'timeout','callback deadline must not wait for a stalled partial HTTP header');
        assert.equal(h.tokenCalls,0);
    }finally{
        incomplete?.destroy();
        await h.close();
    }
});
