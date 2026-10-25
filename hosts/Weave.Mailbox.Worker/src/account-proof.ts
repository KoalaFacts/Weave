/** A short-lived account-ownership assertion for a separately consented OAuth AS.
 * The Web client sends its existing Weave account control ONLY to the Weave
 * resource server, never to the authorization server. The assertion is NOT an
 * OAuth token, and only a challenge consumed once by the AS prevents replay.
 */
export interface AccountProofConfig {
    issuer: string;
    resource: string;
    key: string; // 32-byte random secret in unpadded base64url, injected, never committed.
}

const accountIdPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const b64urlPattern = /^[A-Za-z0-9_-]+$/;
const challengePattern = /^[A-Za-z0-9_-]{43}$/;
const encoder = new TextEncoder();
const decoder = new TextDecoder('utf-8', {fatal:true});
const lifetimeSeconds = 60;

function encode(bytes:Uint8Array): string {
    let binary='';
    for(const byte of bytes)binary+=String.fromCharCode(byte);
    return btoa(binary).replaceAll('+','-').replaceAll('/','_').replace(/=+$/,'');
}
function decode(value:string):Uint8Array {
    if(!b64urlPattern.test(value)||value.length>4096)throw Error('Invalid account proof encoding');
    const raw=atob(value.replaceAll('-','+').replaceAll('_','/')+'='.repeat((4-value.length%4)%4));
    const bytes=Uint8Array.from(raw,ch=>ch.charCodeAt(0));
    if(encode(bytes)!==value)throw Error('Noncanonical account proof encoding');
    return bytes;
}
export function validAccountProofChallenge(value:unknown):asserts value is string {
    if(typeof value!=='string'||!challengePattern.test(value)||decode(value).length!==32)
        throw Error('Invalid account proof challenge');
}
function validConfig(config:AccountProofConfig):void {
    const issuer=new URL(config.issuer),resource=new URL(config.resource);
    if(issuer.protocol!=='https:'||issuer.username||issuer.password||issuer.search||issuer.hash||
       issuer.href.replace(/\/$/,'')!==config.issuer.replace(/\/$/,'')||
       !['https:', ...(resource.hostname==='localhost'||resource.hostname==='127.0.0.1'?['http:']:[])].includes(resource.protocol)||
       resource.username||resource.password||resource.search||resource.hash||resource.pathname!=='/mcp'||
       resource.href!==config.resource||typeof config.key!=='string'||config.key.length!==43||decode(config.key).length!==32)
        throw Error('Invalid account proof configuration');
}
async function signingKey(config:AccountProofConfig):Promise<CryptoKey>{
    validConfig(config);
    return crypto.subtle.importKey('raw',decode(config.key),{name:'HMAC',hash:'SHA-256'},false,['sign','verify']);
}
function clock(value:number):number {
    if(!Number.isSafeInteger(value)||value<0)throw Error('Invalid account proof clock');
    return value;
}

/** Mint only after an existing account control was authenticated by Weave D1. */
export async function issueAccountProof(config:AccountProofConfig,accountId:string,challenge:string,
    nowSeconds=Math.floor(Date.now()/1000)):Promise<string>{
    if(!accountIdPattern.test(accountId))throw Error('Invalid account identity');
    validAccountProofChallenge(challenge);
    const iat=clock(nowSeconds);
    const claims={v:1,iss:config.issuer,aud:config.resource,sub:accountId,nonce:challenge,iat,exp:iat+lifetimeSeconds};
    const text='wop1.'+encode(encoder.encode(JSON.stringify(claims)));
    const mac=new Uint8Array(await crypto.subtle.sign('HMAC',await signingKey(config),encoder.encode(text)));
    return text+'.'+encode(mac);
}

/** Verify binding only; the AS MUST additionally match and atomically consume
 * its outstanding nonce, authenticate/obtain consent, and then issue a separate
 * resource/audience-scoped OAuth grant. A valid assertion alone is not consent.
 */
export async function verifyAccountProof(config:AccountProofConfig,proof:string,challenge:string,
    nowSeconds=Math.floor(Date.now()/1000)):Promise<string>{
    validAccountProofChallenge(challenge);
    const key=await signingKey(config);
    if(typeof proof!=='string'||proof.length>2048)throw Error('Invalid account proof');
    const pieces=proof.split('.');
    if(pieces.length!==3||pieces[0]!=='wop1')throw Error('Invalid account proof');
    const signed=pieces[0]+'.'+pieces[1];
    let claims:unknown;
    try {
        const sig=decode(pieces[2]);
        if(sig.length!==32||!await crypto.subtle.verify('HMAC',key,sig,encoder.encode(signed)))
            throw Error('Invalid account proof signature');
        claims=JSON.parse(decoder.decode(decode(pieces[1])));
    }catch{throw Error('Invalid account proof signature or body');}
    if(!claims||typeof claims!=='object'||Array.isArray(claims))throw Error('Invalid account proof claims');
    const c=claims as Record<string,unknown>,now=clock(nowSeconds);
    if(Object.keys(c).sort().join(',')!==['v','iss','aud','sub','nonce','iat','exp'].sort().join(',')||
       c.v!==1||c.iss!==config.issuer||c.aud!==config.resource||c.nonce!==challenge||
       typeof c.sub!=='string'||!accountIdPattern.test(c.sub)||
       typeof c.iat!=='number'||!Number.isSafeInteger(c.iat)||
       typeof c.exp!=='number'||!Number.isSafeInteger(c.exp)||c.exp!==c.iat+lifetimeSeconds||
       c.iat>now+5||c.exp<=now||c.iat<now-90)
        throw Error('Invalid or expired account proof');
    return c.sub;
}
