import assert from 'node:assert/strict';
import {test} from 'node:test';
import {generateKeyPairSync,createSign,randomUUID} from 'node:crypto';
import {managedAccessConfiguration,verifyManagedAccessAssertion} from '../src/access-managed.ts';
import {start,payload} from './harness.ts';

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
test('default 24-hour Access application JWT remains usable, beyond-policy lifetime stays denied',async()=>{
    const sec=Math.floor(now/1000);
    const allowed=token({iat:sec-10,exp:sec+86390});
    assert.equal((await verify(allowed))?.expiresAt,sec+86390,
        'Cloudflare Access defaults application JWTs to 24 hours; a valid default session must authenticate');
    assert.equal((await verify(token({iat:sec-16,exp:sec+86389})))?.expiresAt,sec+86389,
        'the live Managed OAuth assertion (24h plus a few seconds) must authenticate');
    assert.equal((await verify(token({iat:sec-10,exp:sec+86510})))?.expiresAt,sec+86510,
        'a span exactly at the 24h plus 120-second bound is accepted');
    assert.equal(await verify(token({iat:sec-10,exp:sec+86511})),null,
        'sessions exceeding 24h plus the 120-second skew must still fail closed');
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
test('rejections report a fixed, value-free category for each failed check',async()=>{
    const sec=Math.floor(now/1000);
    const reasonFor=async(jwt:string|null,fetcher:typeof fetch=certificates)=>{
        let reason:string|undefined;
        const result=await verifyManagedAccessAssertion(cfg,jwt===null?new Request(resource):request(jwt),fetcher,now,r=>{reason=r;});
        assert.equal(result,null);
        return reason;
    };
    const cases:[string|null,string][]=[
        [null,'missing'],['not.a.jwt','malformed'],[token({}, {alg:'HS256'}),'header'],[token({}, {typ:'at+jwt'}),'header'],
        [token({iss:'https://foreign.cloudflareaccess.com'}),'issuer'],[token({aud:['other']}),'audience'],
        [token({sub:''}),'subject'],[token({exp:sec+100000}),'lifetime'],[token({iat:sec+120}),'not-yet-valid'],
        [token({nbf:sec+120}),'not-yet-valid'],[token({exp:sec}),'expired'],[token({}, {kid:'unknown-key'}),'key-not-found'],
        [token().split('.').slice(0,2).join('.')+'.'+token({sub:'other'}).split('.')[2],'signature']];
    for(const [jwt,expected] of cases)assert.equal(await reasonFor(jwt),expected,expected);
    assert.equal(await reasonFor(token(),(async()=>new Response('secret',{status:503})) as typeof fetch),'keys-unavailable');
    let lifetime:unknown;
    await verifyManagedAccessAssertion(cfg,request(token({iat:sec-10,exp:sec+90000})),certificates,now,(_r,d)=>{lifetime=d;});
    assert.deepEqual(lifetime,{hasExp:true,hasIat:true,expiresInSeconds:90000,spanSeconds:90010});
    await verifyManagedAccessAssertion(cfg,request(token({iat:undefined})),certificates,now,(_r,d)=>{lifetime=d;});
    assert.deepEqual(lifetime,{hasExp:true,hasIat:false,expiresInSeconds:300,spanSeconds:null});
    let reported:string|undefined;
    assert.ok(await verifyManagedAccessAssertion(cfg,request(token()),certificates,now,r=>{reported=r;}));
    assert.equal(reported,undefined,'a valid assertion reports no rejection');
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
        assert.deepEqual(await denied.json(),{error:'access_denied',reason:'not-linked'});
        const link='/access-trial/link';
        const aSigned=signed('external-subject-A');
        const noControl=await h.mf.dispatchFetch('https://localhost'+link,{
            method:'POST',headers:{'Content-Type':'application/json','Cf-Access-Jwt-Assertion':aSigned},
            body:'{"consent":true}'});
        assert.equal(noControl.status,401);
        assert.deepEqual(await noControl.json(),{error:'access_denied',reason:'account-control'});
        const noAssertion=await h.mf.dispatchFetch('https://localhost'+link,{
            method:'POST',headers:{'Content-Type':'application/json','X-Weave-Account-Control':a.control},
            body:'{"consent":true}'});
        assert.equal(noAssertion.status,401);
        assert.deepEqual(await noAssertion.json(),{error:'access_denied',reason:'assertion-missing'});
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
        // Managed OAuth may consume the opaque bearer before forwarding to origin.
        // A genuinely validated Access JWT + linked owner, not a forwarded Authorization
        // header, must admit the existing 128 KiB bounded private send path.
        const asOwner=async(sub:string,name:string,args:Record<string,unknown>={})=>{
            const response=await h.mf.dispatchFetch(resource,{method:'POST',headers:{
                'Content-Type':'application/json',Accept:'application/json, text/event-stream',
                'Cf-Access-Jwt-Assertion':signed(sub)
            },body:JSON.stringify({jsonrpc:'2.0',id:88,method:'tools/call',
                params:{name,arguments:args}})});
            assert.equal(response.status,200,
                'valid signed Access assertion without a forwarded bearer must reach '+name);
            const frame=await response.json() as {result?:{isError?:boolean;structuredContent?:unknown;content?:Array<{text:string}>}};
            assert.ok(frame.result&&!frame.result.isError,'actual private MCP operation failed: '+name);
            return frame.result.structuredContent??JSON.parse(frame.result.content![0].text);
        };
        const addressA=randomUUID(),addressB=randomUUID();
        await asOwner('external-subject-A','ensure_my_address',{addressId:addressA,label:'Access A'});
        await asOwner('external-subject-B','ensure_my_address',{addressId:addressB,label:'Access B'});
        const intro=payload('access-trial-consent-introduction');
        const requestContact=await asOwner('external-subject-A','request_contact',{
            requestId:intro.messageId,requesterAddressId:addressA,
            recipientAccountId:b.accountId,recipientAddressId:addressB,payload:intro});
        await asOwner('external-subject-B','decide_contact',{
            requestId:intro.messageId,requesterAccountId:a.accountId,
            expectedGeneration:String(requestContact.generation),status:'accepted'});
        const big=payload('managed-access-large-'+'x'.repeat(20_000));
        const sent=await asOwner('external-subject-A','send_message',{
            recipientAccountId:b.accountId,contactGeneration:String(requestContact.generation),payload:big});
        assert.equal(sent.messageId,big.messageId);
        const inbox=await asOwner('external-subject-B','list_my_inbox');
        assert.ok(inbox.items.some((item:{envelope?:{payload?:{messageId:string}}})=>
            item.envelope?.payload?.messageId===big.messageId));
        const ack=await asOwner('external-subject-B','ack_message',{
            messageId:big.messageId,senderAccountId:a.accountId});
        assert.equal(ack.state,'acknowledged');
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
