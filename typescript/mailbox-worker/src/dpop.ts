import { base64url, jwkThumbprint, parseJws, publicJwk, verifyJws, type Ed25519Jwk } from './jose.ts';

export const dpopWindowSeconds = 60;
export type DpopResult = {ok: true; jkt: string; jwk: Ed25519Jwk} | {ok: false};

const jtiText=/^[\x20-\x7E]{1,128}$/;
const refused: DpopResult={ok:false};

/** RFC 9449 htu comparison form: scheme-normalised origin plus path; query and fragment are ignored. */
export function htuOf(url: string): string | null {
    let parsed: URL;
    try { parsed=new URL(url); } catch { return null; }
    if(parsed.protocol!=='https:' && parsed.protocol!=='http:' || parsed.username || parsed.password) return null;
    return parsed.origin+parsed.pathname;
}

export async function accessTokenHash(token: string): Promise<string> {
    return base64url(new Uint8Array(await crypto.subtle.digest('SHA-256',new TextEncoder().encode(token))));
}

export async function verifyDpop(request: Request, options: {now: number; accessToken?: string}): Promise<DpopResult> {
    // Headers joins repeated fields with ", "; a compact JWS never contains a comma.
    const value=request.headers.get('DPoP');
    if(value===null || value.includes(',')) return refused;
    const jws=parseJws(value);
    if(!jws || jws.header.typ!=='dpop+jwt') return refused;
    const jwk=publicJwk(jws.header.jwk);
    if(!jwk) return refused;
    const {jti,htm,htu,iat,ath}=jws.payload;
    const target=htuOf(request.url);
    if(typeof jti!=='string' || !jtiText.test(jti) || htm!==request.method) return refused;
    if(typeof htu!=='string' || target===null || htuOf(htu)!==target) return refused;
    if(!Number.isSafeInteger(iat) || Math.abs(options.now-(iat as number))>dpopWindowSeconds) return refused;
    if(options.accessToken===undefined ? 'ath' in jws.payload : ath!==await accessTokenHash(options.accessToken)) return refused;
    if(!await verifyJws(jws,jwk)) return refused;
    return {ok:true,jkt:await jwkThumbprint(jwk),jwk};
}
