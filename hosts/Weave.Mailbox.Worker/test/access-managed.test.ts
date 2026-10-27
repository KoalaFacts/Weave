import assert from 'node:assert/strict';
import {test} from 'node:test';
import {generateKeyPairSync,createSign} from 'node:crypto';
import {managedAccessConfiguration,verifyManagedAccessAssertion} from '../src/access-managed.ts';
import {start} from './harness.ts';

const now=1793100000000;
const issuer='https://weave-trial.cloudflareaccess.com';
const resource='https://localhost/access-trial/mcp';
const audience='0987654321abcdef0987654321abcdef0987654321abcdef0987654321abcdef';
const cfg={issuer,resource,audience};
const {publicKey,privateKey}=generateKeyPairSync('rsa',{modulusLength:2048});
const jwk={...publicKey.export({format:'jwk'}),alg:'RS256',use:'sig',kid:'rotation-a'};
const encode=(value:unknown)=>Buffer.from(JSON.stringify(value)).toString('base64url');
const token=(values:Record<string,unknown>={},header:Record<string,unknown>={},time=now)=>{
    const claims={iss:issuer,aud:[audience],sub:'external-subject-A',iat:Math.floor(time/1000)-10,
        exp:Math.floor(time/1000)+300,...values};
    const input=encode({typ:'JWT',alg:'RS256',kid:'rotation-a',...header})+'.'+encode(claims);
    return input+'.'+createSign('RSA-SHA256').update(input).sign(privateKey).toString('base64url');
};
const certificates=(async(url:RequestInfo|URL,options?:RequestInit)=>{
    assert.equal(String(url),issuer+'/cdn-cgi/access/certs');
    assert.equal(options?.redirect,'manual');
    assert.equal(options?.cache,'no-store');
    assert.equal(options?.headers && (options.headers as Record<string,string>).Authorization,undefined);
    return new Response(JSON.stringify({keys:[jwk]}),{headers:{'Content-Type':'application/json'}});
}) as typeof fetch;
const request=(jwt:string)=>new Request(resource,{headers:{'Cf-Access-Jwt-Assertion':jwt}});
const verify=(jwt:string,fetcher:typeof fetch=certificates,clock=now)=>
    verifyManagedAccessAssertion(cfg,request(jwt),fetcher,clock);

test('Access trial is opt-in, exact issuer and resource only; public MCP remains independent',()=>{
    assert.equal(managedAccessConfiguration({},resource),null);
    assert.throws(()=>managedAccessConfiguration({ACCESS_TRIAL_ENABLED:'true'},resource));
    assert.throws(()=>managedAccessConfiguration({ACCESS_TRIAL_ENABLED:'true',ACCESS_TRIAL_ISSUER:'https://evil.invalid',
        ACCESS_TRIAL_RESOURCE:resource,ACCESS_TRIAL_AUDIENCE:audience},resource));
    assert.throws(()=>managedAccessConfiguration({ACCESS_TRIAL_ENABLED:'true',ACCESS_TRIAL_ISSUER:issuer,
        ACCESS_TRIAL_RESOURCE:resource,ACCESS_TRIAL_AUDIENCE:audience},'https://foreign.invalid/access-trial/mcp'));
    assert.deepEqual(managedAccessConfiguration({ACCESS_TRIAL_ENABLED:'true',ACCESS_TRIAL_ISSUER:issuer,
        ACCESS_TRIAL_RESOURCE:resource,ACCESS_TRIAL_AUDIENCE:audience},resource),cfg);
});
test('signed Access JWT validates exact issuer and app audience, never converts subject into a Weave ID',async()=>{
    assert.deepEqual(await verify(token()),{issuer,resource,subject:'external-subject-A',
        expiresAt:Math.floor(now/1000)+300});
});
test('forged signature, unknown kid, missing assertion and hostile algorithms never authenticate',async()=>{
    assert.equal(await verifyManagedAccessAssertion(cfg,new Request(resource),certificates,now),null);
    assert.equal(await verify(token().slice(0,-3)+'abc'),null);
    assert.equal(await verify(token({}, {kid:'unknown-key'})),null);
    assert.equal(await verify(token({}, {alg:'HS256'})),null);
    assert.equal(await verify('not.a.jwt'),null);
});
test('wrong issuer, app audience, expiry, nbf, future issued and missing opaque subject are rejected',async()=>{
    const sec=Math.floor(now/1000);
    for(const value of [{iss:'https://foreign.cloudflareaccess.com'},{aud:['other']},{exp:sec},
        {exp:sec+100000},{iat:sec+120},{nbf:sec+120},{sub:''},{sub:'x'.repeat(300)}]){
        assert.equal(await verify(token(value)),null,JSON.stringify(value));
    }
});
test('pinned cert URL refuses untrusted fetch errors and does not expose bearer',async()=>{
    assert.equal(await verify(token(),(async()=>new Response('secret',{status:503})) as typeof fetch),null);
    assert.equal(await verify(token(),(async()=>{throw Error('secret hidden')}) as typeof fetch),null);
});
test('a token that expires while its signing keys are checked cannot authorize',async()=>{
    let clock=now;
    const short=token({exp:Math.floor(now/1000)+1});
    const delayed=(async(url:RequestInfo|URL,options?:RequestInit)=>{
        const result=await certificates(url,options);clock+=2000;return result;
    }) as typeof fetch;
    assert.equal(await verifyManagedAccessAssertion(cfg,request(short),delayed,()=>clock),null);
});
test('real workerd/D1 trial enforces two credentials, owner binding, revocation and private account isolation',async()=>{
    const observed:{calls:number;originMatched:boolean;methodMatched:boolean}={calls:0,originMatched:true,methodMatched:true};
    const outward=async(req:Request)=>{
        observed.calls++;
        observed.originMatched&&=req.url===issuer+'/cdn-cgi/access/certs';
        observed.methodMatched&&=req.method==='GET';
        return new Response(JSON.stringify({keys:[jwk]}),{headers:{'Content-Type':'application/json'}});
    };
    const h=await start({outboundService:outward,bindings:{
        ACCESS_TRIAL_ENABLED:'true',ACCESS_TRIAL_ISSUER:issuer,ACCESS_TRIAL_RESOURCE:resource,
        ACCESS_TRIAL_AUDIENCE:audience,OAUTH_IDENTITY_LINKS_ENABLED:'true'
    }});
    try{
        const a=await h.register(),b=await h.register(),clock=Date.now();
        const signed=(sub:string)=>token({sub}, {},clock);
        const client=(sub:string)=>({
            method:'POST',headers:{'Content-Type':'application/json',
                'Accept':'application/json, text/event-stream','MCP-Protocol-Version':'2025-11-25',
                Authorization:'Bearer oauth:synthetic-public-client-token',
                'Cf-Access-Jwt-Assertion':signed(sub)},
            body:JSON.stringify({jsonrpc:'2.0',id:3,method:'tools/call',params:{name:'get_my_profile',arguments:{}}})
        });
        const denied=await h.mf.dispatchFetch(resource,client('external-subject-A'));
        assert.equal(denied.status,401,'unlinked Access login never grants a Weave account');
        const link='/access-trial/link';
        const aSigned=signed('external-subject-A');
        const noControl=await h.mf.dispatchFetch('https://localhost'+link,{
            method:'POST',headers:{'Content-Type':'application/json','Cf-Access-Jwt-Assertion':aSigned},
            body:'{"consent":true}'});
        assert.equal(noControl.status,401);
        const noAssertion=await h.mf.dispatchFetch('https://localhost'+link,{
            method:'POST',headers:{'Content-Type':'application/json','X-Weave-Account-Control':a.control},
            body:'{"consent":true}'});
        assert.equal(noAssertion.status,401);
        const notConsented=await h.mf.dispatchFetch('https://localhost'+link,{
            method:'POST',headers:{'Content-Type':'application/json','X-Weave-Account-Control':a.control,
                'Cf-Access-Jwt-Assertion':aSigned},body:'{"consent":false}'});
        assert.equal(notConsented.status,400,`Signed consent preflight: cert-calls=${observed.calls} origin=${observed.originMatched} method=${observed.methodMatched}`);
        const postLink=async(control:string,jwt:string)=>h.mf.dispatchFetch('https://localhost'+link,{
            method:'POST',headers:{'Content-Type':'application/json','X-Weave-Account-Control':control,
                'Cf-Access-Jwt-Assertion':jwt},body:'{"consent":true}'});
        const linkA=await postLink(a.control,aSigned);
        assert.equal(linkA.status,200,await linkA.clone().text());
        const keyA=await linkA.json();
        assert.equal(keyA.subjectDigest.length,64);
        assert.equal(JSON.stringify(keyA).includes('external-subject-A'),false);
        const theft=await postLink(b.control,aSigned);
        assert.equal(theft.status,409,'same externally verified identity cannot join a foreign Weave owner');
        const profileA=await h.mf.dispatchFetch(resource,client('external-subject-A'));
        assert.equal(profileA.status,200,await profileA.clone().text());
        assert.equal((await profileA.json()).result.structuredContent.id,a.accountId);
        const linkB=await postLink(b.control,signed('external-subject-B'));
        assert.equal(linkB.status,200,await linkB.clone().text());
        const profileB=await h.mf.dispatchFetch(resource,client('external-subject-B'));
        assert.equal(profileB.status,200,await profileB.clone().text());
        assert.equal((await profileB.json()).result.structuredContent.id,b.accountId);
        const revoke=await h.mf.dispatchFetch('https://localhost/v1/accounts/oauth/links/revoke',{
            method:'POST',headers:{'Content-Type':'application/json','X-Weave-Account-Control':a.control},
            body:JSON.stringify(keyA)});
        assert.equal(revoke.status,200,await revoke.clone().text());
        assert.equal((await h.mf.dispatchFetch(resource,client('external-subject-A'))).status,401,
            'revoked owner mapping denies the next private call even with a valid Access JWT');
        assert.equal((await h.mf.dispatchFetch(resource,client('external-subject-B'))).status,200,
            'another owner retains independent access');
    }finally{await h.close();}
});
