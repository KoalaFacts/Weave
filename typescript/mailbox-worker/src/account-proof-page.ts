/** First-party browser handoff. The mailbox control never leaves the Weave
 * resource origin, and a proof is not an OAuth token or consent decision. */
const noStore = {
    'Cache-Control': 'no-store',
    'Pragma': 'no-cache',
    'Referrer-Policy': 'no-referrer',
    'X-Content-Type-Options': 'nosniff'
};
const escape = (value: string) => value.replace(/[&<>"']/g, char => `&#${char.charCodeAt(0)};`);

export function accountProofPage(issuer: string, resource: string): Response {
    const html = `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Verify your Weave account</title></head><body>
<main><h1>Verify your existing Weave account</h1>
<p>This page is served by Weave. Your long-lived account control is sent only to this same Weave origin. It is never sent to the authorization server, written into a URL, stored, or placed in a token.</p>
<p>Authorization server: <strong>${escape(issuer)}</strong><br>Protected resource: <strong>${escape(resource)}</strong></p>
<p>Check the address bar and the requesting app before continuing. A signed ownership proof expires after 60 seconds and does not itself grant access. The authorization server must separately show you the client, requested permissions and an Allow/Deny choice.</p>
<form id="owner-proof-form" autocomplete="off">
<label for="proof-challenge">Challenge from the trusted authorization server</label><br>
<input id="proof-challenge" name="challenge" type="text" autocomplete="off" spellcheck="false" required maxlength="43" size="48"><br>
<label for="owner-control">Existing Weave account control</label><br>
<input id="owner-control" type="password" autocomplete="off" spellcheck="false" required size="48"><br>
<button id="make-proof" type="submit">Create a short-lived ownership proof</button></form>
<p id="proof-status" role="status" aria-live="polite"></p>
<label for="owner-proof">Copy the proof to the authorization server only when you recognize it</label><br>
<textarea id="owner-proof" readonly rows="5" cols="68" aria-label="Signed ownership proof"></textarea>
<p>Never paste your account control into an external authorization server or agent prompt.</p>
</main><script src="/account-proof.js" defer></script></body></html>`;
    return new Response(html, {status: 200, headers: {
        ...noStore, 'Content-Type': 'text/html; charset=utf-8',
        'X-Frame-Options': 'DENY',
        'Content-Security-Policy': "default-src 'none'; script-src 'self'; connect-src 'self'; form-action 'none'; base-uri 'none'; frame-ancestors 'none'"
    }});
}

// Static same-origin source; never interpolate a control, challenge, issuer or
// redirect into executable JavaScript. No storage, telemetry, or auto-forward.
const script = `(() => {
  'use strict';
  const form = document.getElementById('owner-proof-form');
  const challenge = document.getElementById('proof-challenge');
  const control = document.getElementById('owner-control');
  const proof = document.getElementById('owner-proof');
  const button = document.getElementById('make-proof');
  const status = document.getElementById('proof-status');
  const supplied = new URL(window.location.href).searchParams.getAll('challenge');
  if (supplied.length === 1 && /^[A-Za-z0-9_-]{43}$/.test(supplied[0])) challenge.value = supplied[0];
  form.addEventListener('submit', async event => {
    event.preventDefault();
    proof.value = '';
    status.textContent = '';
    if (!/^[A-Za-z0-9_-]{43}$/.test(challenge.value) || control.value.length < 64) {
      control.value = '';
      status.textContent = 'A valid 32-byte challenge and existing account control are required.';
      return;
    }
    button.disabled = true;
    try {
      const response = await fetch('/v1/accounts/proof', {
        method: 'POST', mode: 'same-origin', credentials: 'omit', redirect: 'error', cache: 'no-store',
        headers: {'Content-Type': 'application/json', 'X-Weave-Account-Control': control.value},
        body: JSON.stringify({challenge: challenge.value})
      });
      control.value = '';
      if (!response.ok) throw Error('Proof was not issued');
      const data = await response.json();
      if (typeof data.proof !== 'string' || data.proof.length > 2048 ||
          !/^wop1\\.[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+$/.test(data.proof)) throw Error('Invalid proof response');
      proof.value = data.proof;
      status.textContent = 'Copy this proof only to the trusted authorization server. It expires after 60 seconds.';
    } catch {
      status.textContent = 'Ownership could not be verified. Check your control and the trusted challenge.';
    } finally {
      control.value = '';
      button.disabled = false;
    }
  });
})();`;

export function accountProofScript(): Response {
    return new Response(script, {status: 200, headers: {
        ...noStore, 'Content-Type': 'text/javascript; charset=utf-8'
    }});
}
