import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {test} from 'node:test';
import type {AddressInfo} from 'node:net';
import {authorizeMcpAccount} from '../../../examples/remote-mcp-pair/oauth.ts';

type IssuerResponse='match'|'mismatch'|'missing'|'duplicate';
async function fixture(advertisesIssuer:boolean){
    let tokenCalls=0;
    const server=createServer(async(req,res)=>{
        const base='http://127.0.0.1:'+(server.address() as AddressInfo).port;
        const url=new URL(req.url??'/',base);
        res.setHeader('Content-Type','application/json');
        if(url.pathname==='/.well-known/oauth-protected-resource/mcp'){
            res.end(JSON.stringify({resource:base+'/mcp',authorization_servers:[base]}));return;
        }
        if(url.pathname==='/.well-known/oauth-authorization-server'){
            res.end(JSON.stringify({issuer:base,authorization_endpoint:base+'/authorize',token_endpoint:base+'/token',
                authorization_response_iss_parameter_supported:advertisesIssuer,
                response_types_supported:['code'],grant_types_supported:['authorization_code'],
                code_challenge_methods_supported:['S256']}));return;
        }
        if(url.pathname==='/token'){
            tokenCalls++;
            res.end(JSON.stringify({access_token:'synthetic-issuer-verified-token',token_type:'Bearer',expires_in:120}));return;
        }
        res.writeHead(404);res.end('{}');
    });
    await new Promise<void>((resolve,reject)=>{server.once('error',reject);server.listen(0,'127.0.0.1',resolve);});
    return {resource:'http://127.0.0.1:'+(server.address() as AddressInfo).port+'/mcp',
        get tokenCalls(){return tokenCalls;},
        close:()=>new Promise<void>(resolve=>server.close(()=>resolve()))};
}
async function exercise(advertises:boolean,kind:IssuerResponse,accept:boolean){
    const h=await fixture(advertises);
    let callbackStatus=0;
    try{
        const attempt=authorizeMcpAccount(h.resource,'test-public-client',async link=>{
            const authorization=new URL(link);
            const callback=new URL(authorization.searchParams.get('redirect_uri')!);
            callback.searchParams.set('code','synthetic-code');
            callback.searchParams.set('state',authorization.searchParams.get('state')!);
            const issuer=new URL(h.resource).origin;
            if(kind==='match'||kind==='duplicate')callback.searchParams.append('iss',issuer);
            if(kind==='mismatch')callback.searchParams.append('iss','https://attacker.example');
            if(kind==='duplicate')callback.searchParams.append('iss',issuer);
            callbackStatus=(await fetch(callback)).status;
        },{allowLoopbackForTests:true,scopes:['weave:messages.send'],timeoutMs:1000});
        if(accept)assert.equal(await attempt,'synthetic-issuer-verified-token');
        else await assert.rejects(attempt,/issuer|callback|authorization/i);
        assert.equal(callbackStatus,accept?200:400,'callback must reject issuer mix-up before exchanging a code');
        assert.equal(h.tokenCalls,accept?1:0,'untrusted issuer must never reach token exchange');
    }finally{await h.close();}
}

test('Cloudflare RFC9207 issuer-identifying response is accepted for the exact trusted issuer',async()=>{
    await exercise(true,'match',true);
});
test('Cloudflare RFC9207 issuer-identifying response must not be omitted',async()=>{
    await exercise(true,'missing',false);
});
test('OAuth response with foreign issuer must fail before exchanging the authorization code',async()=>{
    await exercise(true,'mismatch',false);
});
test('OAuth response with duplicate issuer parameters must fail closed',async()=>{
    await exercise(true,'duplicate',false);
});
test('Legacy issuer metadata without RFC9207 support retains safe state and PKCE authorization',async()=>{
    await exercise(false,'missing',true);
});
test('Even legacy metadata must reject a present mismatched issuer',async()=>{
    await exercise(false,'mismatch',false);
});
