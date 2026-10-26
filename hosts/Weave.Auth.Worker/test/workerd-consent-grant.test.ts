import assert from "node:assert/strict";
import {createHash,randomBytes,randomUUID} from "node:crypto";
import {readFileSync} from "node:fs";
import {mkdtemp,rm} from "node:fs/promises";
import {tmpdir} from "node:os";
import {join,resolve} from "node:path";
import {test} from "node:test";
import {Miniflare} from "miniflare";
import ts from "typescript";
import {issueAccountProof} from "../../Weave.Mailbox.Worker/src/account-proof.ts";

const issuer="https://auth.weave.example";
const resource="https://demo.weave-389.workers.dev/mcp";
const clientId="weave-consent-regression-client";
const redirectUri="http://127.0.0.1:53621/callback";
const bundled=readFileSync(resolve("dist/index.js"),"utf8");
// Execute the real Weave resource authorization module in a separate workerd
// isolate. No mock token validator or canned claims may stand in for the AS.
const resourceAuthorization=ts.transpileModule(
  readFileSync(resolve("../Weave.Mailbox.Worker/src/remote-oauth.ts"),"utf8"),
  {compilerOptions:{module:ts.ModuleKind.ESNext,target:ts.ScriptTarget.ES2022}}
).outputText;
const resourceWorker=resourceAuthorization+`
export default {
  async fetch(request,env){
    let oauth;
    try {
      oauth=oauthConfiguration({
        OAUTH_ISSUER:env.OAUTH_ISSUER,
        OAUTH_RESOURCE:env.OAUTH_RESOURCE,
        AUTH_SERVER:env.AUTH_SERVER
      },request.url);
    } catch {return new Response("OAuth unavailable",{status:503});}
    if(!oauth)return new Response("OAuth unavailable",{status:503});
    const access=await authorizeOAuth(oauth,request,"weave:messages.send",
      async owner=>(await env.ACCOUNTS.prepare(
        "SELECT account_id FROM accounts WHERE account_id=?"
      ).bind(owner).first("account_id"))===owner);
    return access instanceof Response?access:Response.json({accountId:access.accountId});
  }
};`;

function options(storage:string,key:string) {
  return {
    resourcePersistencePath:storage,
    cf:false,
    workers:[{
      config:{
        name:"weave-consent-regression",
        // Exact shipped bundle; the locked workerd supports 2026-10-13.
        compatibilityDate:"2026-10-13",
        compatibilityFlags:["nodejs_compat","global_fetch_strictly_public"],
        manifest:{mainModule:"index.js",modules:{"index.js":{type:"esm",contents:bundled}}},
        env:{
          OAUTH_KV:{type:"kv"},
          CONSENT_NONCES:{type:"durable-object",worker:"weave-consent-regression",exportName:"ConsentNonce"},
          OAUTH_ISSUER:{type:"text",value:issuer},
          OAUTH_RESOURCE:{type:"text",value:resource},
          OAUTH_ACCOUNT_PROOF_KEY:{type:"text",value:key}
        },
        exports:{ConsentNonce:{type:"durable-object",storage:"sqlite"}}
      }
    },{
      name:"weave-resource-regression",
      modules:true,
      script:resourceWorker,
      compatibilityDate:"2026-10-13",
      compatibilityFlags:["nodejs_compat","global_fetch_strictly_public"],
      serviceBindings:{AUTH_SERVER:"weave-consent-regression"},
      d1Databases:{ACCOUNTS:"weave-resource-regression-accounts"},
      bindings:{OAUTH_ISSUER:issuer,OAUTH_RESOURCE:resource}
    }]
  };
}

function consentForm(handle:string,proof:string) {
  return new URLSearchParams({handle,proof,decision:"approve"}).toString();
}

test("a foreign browser cannot burn an owner nonce before consent-cookie verification", {timeout:90_000}, async()=>{
  const storage=await mkdtemp(join(tmpdir(),"weave-auth-consent-"));
  const key=randomBytes(32).toString("base64url");
  const mf=new Miniflare(options(storage,key));
  try {
    // Controlled test registration only: no DCR endpoint, real user or cloud client.
    const kv=await mf.getKVNamespace("OAUTH_KV");
    await kv.put("client:"+clientId,JSON.stringify({
      clientId,redirectUris:[redirectUri],clientName:"Local test agent",
      grantTypes:["authorization_code","refresh_token"],responseTypes:["code"],
      tokenEndpointAuthMethod:"none",authMethodExplicit:true,
      registrationDate:Math.floor(Date.now()/1000)
    }));
    const verifier=randomBytes(32).toString("base64url");
    const challenge=createHash("sha256").update(verifier).digest("base64url");
    const url=new URL(issuer+"/authorize");
    for(const [k,v] of Object.entries({
      response_type:"code",client_id:clientId,redirect_uri:redirectUri,
      scope:"weave:contacts.read weave:messages.send",state:"separate-browser-state",
      resource,code_challenge:challenge,code_challenge_method:"S256"
    }))url.searchParams.set(k,v);

    const page=await mf.dispatchFetch(url.href,{redirect:"manual"});
    assert.equal(page.status,200,"real AS must accept a pre-registered PKCE native client");
    const html=await page.text();
    const handle=html.match(/name="handle" value="([A-Za-z0-9_-]{43})"/)?.[1];
    assert.ok(handle,"SDK-generated browser-bound consent handle must be rendered");
    const cookie=page.headers.getSetCookie().map(x=>x.split(";")[0]).join("; ");
    assert.ok(cookie.includes("oauth"),"SDK browser-bound consent cookie must be set");
    const ns=await mf.getDurableObjectNamespace("CONSENT_NONCES");
    const ownerNonce=ns.get(ns.idFromName(handle));
    const initial=await ownerNonce.fetch("https://nonce.internal/nonce");
    assert.equal(initial.status,200);
    const {challenge:nonce}=await initial.json() as {challenge:string};
    const account=randomUUID();
    const proof=await issueAccountProof({issuer,resource,key},account,nonce);

    // A stolen handle + signed proof must NOT consume the owner's one-time
    // challenge without the SDK's original browser-binding cookie.
    const withoutCookie=await mf.dispatchFetch(issuer+"/authorize",{
      method:"POST",redirect:"manual",
      headers:{"Origin":issuer,"Content-Type":"application/x-www-form-urlencoded"},
      body:consentForm(handle,proof)
    });
    assert.equal(withoutCookie.status,400);
    const unspent=await ownerNonce.fetch("https://nonce.internal/nonce");
    assert.equal(unspent.status,200,"unauthorized browser must not destroy valid owner's nonce");
    assert.deepEqual(await unspent.json(),{challenge:nonce});

    const allowed=await mf.dispatchFetch(issuer+"/authorize",{
      method:"POST",redirect:"manual",
      headers:{"Origin":issuer,"Cookie":cookie,"Content-Type":"application/x-www-form-urlencoded"},
      body:consentForm(handle,proof)
    });
    assert.equal(allowed.status,302,"the original browser can still approve exactly once");
    const redirect=new URL(allowed.headers.get("location")!);
    assert.equal(redirect.origin,new URL(redirectUri).origin);
    assert.equal(redirect.searchParams.get("state"),"separate-browser-state");
    assert.equal(redirect.searchParams.get("iss"),issuer);
    const code=redirect.searchParams.get("code");
    assert.ok(code,"authorization code must be returned after explicit consent");

    // Discover the SDK's actual token endpoint; v1.2.4 defaults to
    // /oauth/token, not /token. A native MCP client must do the same.
    const metadata=await mf.dispatchFetch(issuer+"/.well-known/oauth-authorization-server");
    assert.equal(metadata.status,200);
    const {token_endpoint:tokenEndpoint}=await metadata.json() as {token_endpoint:string};
    assert.equal(new URL(tokenEndpoint).origin,issuer);
    const token=await mf.dispatchFetch(tokenEndpoint,{
      method:"POST",redirect:"manual",
      headers:{"Content-Type":"application/x-www-form-urlencoded"},
      body:new URLSearchParams({
        grant_type:"authorization_code",code,client_id:clientId,
        redirect_uri:redirectUri,code_verifier:verifier,resource
      }).toString()
    });
    assert.equal(token.status,200,"actual AS must exchange a PKCE code for resource-bound token");
    const grant=await token.json() as {access_token?:string;expires_in?:number;resource?:string};
    assert.ok(grant.access_token);
    assert.equal(grant.expires_in,900,"resource's 15-minute bound must not be relaxed");
    assert.equal(grant.resource,resource);
    const owners=await kv.list({prefix:"grant:"+account+":"});
    assert.equal(owners.keys.length,1,"grant must be attributed to the proven D1 account UUID");

    const replay=await mf.dispatchFetch(issuer+"/authorize",{
      method:"POST",redirect:"manual",
      headers:{"Origin":issuer,"Cookie":cookie,"Content-Type":"application/x-www-form-urlencoded"},
      body:consentForm(handle,proof)
    });
    assert.equal(replay.status,400,"SDK handle + Durable Object proof can never grant twice");

    // A second independently consented existing account gets a separate grant
    // even when using the same registered native OAuth client. No shared owner
    // or simulated token string may satisfy this real workerd/KV boundary.
    const accountB=randomUUID(),verifierB=randomBytes(32).toString("base64url");
    const authorizeB=new URL(url);
    authorizeB.searchParams.set("state","independent-second-agent");
    authorizeB.searchParams.set("code_challenge",
      createHash("sha256").update(verifierB).digest("base64url"));
    const pageB=await mf.dispatchFetch(authorizeB.href,{redirect:"manual"});
    assert.equal(pageB.status,200);
    const htmlB=await pageB.text();
    const handleB=htmlB.match(/name="handle" value="([A-Za-z0-9_-]{43})"/)?.[1];
    assert.ok(handleB);
    assert.notEqual(handleB,handle);
    const cookieB=pageB.headers.getSetCookie().map(x=>x.split(";")[0]).join("; ");
    const nonceStubB=ns.get(ns.idFromName(handleB));
    const nonceResultB=await nonceStubB.fetch("https://nonce.internal/nonce");
    assert.equal(nonceResultB.status,200);
    const {challenge:nonceB}=await nonceResultB.json() as {challenge:string};
    assert.notEqual(nonceB,nonce);
    const proofB=await issueAccountProof({issuer,resource,key},accountB,nonceB);

    const crossNonce=await mf.dispatchFetch(issuer+"/authorize",{
      method:"POST",redirect:"manual",
      headers:{"Origin":issuer,"Cookie":cookieB,"Content-Type":"application/x-www-form-urlencoded"},
      body:consentForm(handleB,proof)
    });
    assert.equal(crossNonce.status,400,"an existing first-account proof is not valid for another grant");
    assert.equal((await nonceStubB.fetch("https://nonce.internal/nonce")).status,200);

    const allowedB=await mf.dispatchFetch(issuer+"/authorize",{
      method:"POST",redirect:"manual",
      headers:{"Origin":issuer,"Cookie":cookieB,"Content-Type":"application/x-www-form-urlencoded"},
      body:consentForm(handleB,proofB)
    });
    assert.equal(allowedB.status,302);
    const redirectB=new URL(allowedB.headers.get("location")!);
    assert.equal(redirectB.searchParams.get("state"),"independent-second-agent");
    assert.equal(redirectB.searchParams.get("iss"),issuer);
    const codeB=redirectB.searchParams.get("code");
    assert.ok(codeB);
    const tokenB=await mf.dispatchFetch(tokenEndpoint,{
      method:"POST",redirect:"manual",
      headers:{"Content-Type":"application/x-www-form-urlencoded"},
      body:new URLSearchParams({
        grant_type:"authorization_code",code:codeB,client_id:clientId,
        redirect_uri:redirectUri,code_verifier:verifierB,resource
      }).toString()
    });
    assert.equal(tokenB.status,200);
    const grantB=await tokenB.json() as {access_token?:string;expires_in?:number;resource?:string};
    assert.ok(grantB.access_token);
    assert.notEqual(grantB.access_token,grant.access_token);
    assert.equal(grantB.expires_in,900);
    assert.equal(grantB.resource,resource);
    assert.equal((await kv.list({prefix:"grant:"+accountB+":"})).keys.length,1);
    assert.equal((await kv.list({prefix:"grant:"+account+":"})).keys.length,1,
      "authorizing a different account must not replace the first account's grant");

    // Real two-Worker Service Binding, real AS KV tokens, and the real
    // resource's authorizeOAuth source running in workerd against D1.
    // This is still isolated emulation, not a live cloud deployment.
    const db=await mf.getD1Database("ACCOUNTS","weave-resource-regression");
    await db.exec("CREATE TABLE accounts (account_id TEXT PRIMARY KEY);");
    await db.prepare("INSERT INTO accounts (account_id) VALUES (?)").bind(account).run();
    await db.prepare("INSERT INTO accounts (account_id) VALUES (?)").bind(accountB).run();
    const worker=await mf.getWorker("weave-resource-regression");
    async function identify(token:string):Promise<Response>{
      return worker.fetch(resource,{method:"POST",headers:{Authorization:"Bearer "+token}});
    }
    const ownerA=await identify(grant.access_token!);
    assert.equal(ownerA.status,200,"real resource must accept AS-verified account A");
    assert.deepEqual(await ownerA.json(),{accountId:account});
    const ownerB=await identify(grantB.access_token!);
    assert.equal(ownerB.status,200,"a second token must never bind to account A");
    assert.deepEqual(await ownerB.json(),{accountId:accountB});
    assert.equal((await identify("not-a-valid-cloudflare-access-token")).status,401);
    await db.prepare("DELETE FROM accounts WHERE account_id=?").bind(accountB).run();
    assert.equal((await identify(grantB.access_token!)).status,401,
      "removing a Weave account must immediately revoke private resource access");
    assert.equal((await identify(grant.access_token!)).status,200,
      "removing B must not deny the independently owned account A");
    const revocationMetadata=await mf.dispatchFetch(issuer+"/.well-known/oauth-authorization-server");
    const {revocation_endpoint:revocationEndpoint}=await revocationMetadata.json() as {revocation_endpoint:string};
    assert.equal(new URL(revocationEndpoint).origin,issuer);
    const revoked=await mf.dispatchFetch(revocationEndpoint,{
      method:"POST",redirect:"manual",
      headers:{"Content-Type":"application/x-www-form-urlencoded"},
      body:new URLSearchParams({token:grant.access_token!,client_id:clientId}).toString()
    });
    assert.equal(revoked.status,200,"the real AS must accept its own token revocation");
    assert.equal((await identify(grant.access_token!)).status,401,
      "token revocation must be observed at the next Service Binding lookup");
  } finally {
    await mf.dispose();
    await rm(storage,{recursive:true,force:true});
  }
});
