export type Ed25519Jwk = {kty: 'OKP'; crv: 'Ed25519'; x: string};
export type CompactJws = {header: Record<string, unknown>; payload: Record<string, unknown>; signingInput: Uint8Array<ArrayBuffer>; signature: Uint8Array<ArrayBuffer>};

const jwkMembers=new Set(['kty','crv','x','kid','alg','use']);
const algorithms=new Set(['EdDSA','Ed25519']);
const base64urlText=/^[A-Za-z0-9_-]*$/;

export function base64url(bytes: Uint8Array): string {
    let binary='';
    for(const byte of bytes) binary+=String.fromCharCode(byte);
    return btoa(binary).replaceAll('+','-').replaceAll('/','_').replace(/=+$/,'');
}

/** Decodes unpadded base64url; any padding, foreign character or non-zero spare bit is refused. */
export function fromBase64url(text: string): Uint8Array<ArrayBuffer> | null {
    if(!base64urlText.test(text) || text.length%4===1) return null;
    const binary=atob(text.replaceAll('-','+').replaceAll('_','/').padEnd(Math.ceil(text.length/4)*4,'='));
    const bytes=Uint8Array.from(binary,character => character.charCodeAt(0));
    return base64url(bytes)===text ? bytes : null;
}

const fieldPrime=2n**255n-19n;
// y-coordinates of the eight small-order points (identity, order 2, order 4 and order 8; both x signs share a y).
const smallOrderY=new Set([0n,1n,fieldPrime-1n,
    2707385501144840649318225287225658788936804267575313519463743609750303402022n,
    55188659117513257062467267217118295137698188065244968500265048394206261417927n]);

/** Refuses non-canonical (y >= p) and small-order point encodings independently of the runtime's verify checks. */
function acceptablePoint(bytes: Uint8Array): boolean {
    let y=0n;
    for(let i=31;i>=0;i--) y=(y<<8n)|BigInt(i===31 ? bytes[i]&0x7f : bytes[i]);
    return y<fieldPrime && !smallOrderY.has(y);
}

const isObject=(value: unknown): value is Record<string, unknown> => typeof value==='object' && value!==null && !Array.isArray(value);

/** Accepts only a public Ed25519 JWK and returns its canonical members; private or unknown members are refused. */
export function publicJwk(value: unknown): Ed25519Jwk | null {
    if(!isObject(value) || Object.keys(value).some(member => !jwkMembers.has(member))) return null;
    const {kty,crv,x,kid,alg,use}=value;
    const point=typeof x==='string' ? fromBase64url(x) : null;
    if(kty!=='OKP' || crv!=='Ed25519' || typeof x!=='string' || point?.length!==32 || !acceptablePoint(point)) return null;
    if(kid!==undefined && typeof kid!=='string' || alg!==undefined && !algorithms.has(alg as string) || use!==undefined && use!=='sig') return null;
    return {kty,crv,x};
}

/** RFC 7638 thumbprint over the required Ed25519 members in lexicographic order. */
export async function jwkThumbprint(jwk: Ed25519Jwk): Promise<string> {
    const canonical=JSON.stringify({crv:jwk.crv,kty:jwk.kty,x:jwk.x});
    return base64url(new Uint8Array(await crypto.subtle.digest('SHA-256',new TextEncoder().encode(canonical))));
}

const jsonObject=(bytes: Uint8Array): Record<string, unknown> | null => {
    try {
        const value: unknown=JSON.parse(new TextDecoder('utf-8',{fatal:true}).decode(bytes));
        return isObject(value) ? value : null;
    } catch { return null; }
};

export function parseJws(token: string, maxLength = 4096): CompactJws | null {
    if(token.length>maxLength) return null;
    const parts=token.split('.');
    if(parts.length!==3) return null;
    const [headerBytes,payloadBytes,signature]=parts.map(fromBase64url);
    if(!headerBytes || !payloadBytes || signature?.length!==64) return null;
    const header=jsonObject(headerBytes),payload=jsonObject(payloadBytes);
    if(!header || !payload || !algorithms.has(header.alg as string) || 'crit' in header) return null;
    return {header,payload,signingInput:new TextEncoder().encode(`${parts[0]}.${parts[1]}`),signature};
}

export async function verifyJws(jws: CompactJws, jwk: Ed25519Jwk): Promise<boolean> {
    let key: CryptoKey;
    try {
        key=await crypto.subtle.importKey('jwk',{kty:jwk.kty,crv:jwk.crv,x:jwk.x},{name:'Ed25519'},false,['verify']);
    } catch { return false; } // A key the runtime cannot import verifies nothing.
    return crypto.subtle.verify({name:'Ed25519'},key,jws.signature,jws.signingInput);
}
