import html from './demo.html';
import { randomBytes } from 'node:crypto';
export function demo(): Response {
    const nonce=randomBytes(24).toString('base64');
    return new Response(html.replaceAll('WEAVE_NONCE',nonce),{headers:{
        'Content-Type':'text/html; charset=utf-8','Cache-Control':'no-store','Referrer-Policy':'no-referrer','X-Content-Type-Options':'nosniff',
        'Content-Security-Policy':`default-src 'none'; script-src 'nonce-${nonce}'; style-src 'nonce-${nonce}'; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'`
    }});
}
