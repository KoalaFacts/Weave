import html from './demo.html';
import connectHtml from './connect.html';
import { randomBytes } from 'node:crypto';
import { withAgentEntry } from './agent-start';
function htmlPage(content: string): Response {
    const nonce=randomBytes(24).toString('base64');
    return new Response(content.replaceAll('WEAVE_NONCE',nonce),{headers:{
        'Content-Type':'text/html; charset=utf-8','Cache-Control':'no-store','Referrer-Policy':'no-referrer','X-Content-Type-Options':'nosniff',
        'Content-Security-Policy':`default-src 'none'; script-src 'nonce-${nonce}'; style-src 'nonce-${nonce}'; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'`
    }});
}
export function demo(): Response { return htmlPage(withAgentEntry(html)); }
export function connect(): Response { return htmlPage(connectHtml); }
