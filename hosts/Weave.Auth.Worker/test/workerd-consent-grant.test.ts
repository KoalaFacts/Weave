import assert from "node:assert/strict";
import {createHash,randomBytes,randomUUID} from "node:crypto";
import {readFileSync} from "node:fs";
import {mkdtemp,rm} from "node:fs/promises";
import {tmpdir} from "node:os";
import {join,resolve} from "node:path";
import {test} from "node:test";
import {Miniflare} from "miniflare";
import {stripTypeScriptTypes} from "node:module";
import {initializeDatabase} from "../../Weave.Mailbox.Worker/scripts/latest-schema.ts";
import {demoOAuthClientId,demoOAuthClientMetadata} from "../../Weave.Mailbox.Worker/src/oauth-client-metadata.ts";


const issuer="https://auth.weave.example";
const resource="https://demo.weave-389.workers.dev/mcp";
// Exercise the deployed demo's public CIMD contract, not a seeded KV client.
const clientId=demoOAuthClientId;
const redirectUri="http://127.0.0.1:53621/callback";
const redirectUriB="http://127.0.0.1:53622/callback";
// Payload generated with the exact mailbox wire shape. Importing the broader
// legacy test harness into a strict OAuth test creates unrelated type errors.
function mailboxPayload(bytes:string,now=Date.now()){
  const hex=now.toString(16).padStart(12,"0");
  return {
    messageId:hex.slice(0,8)+"-"+hex.slice(8)+"-7"+
      randomBytes(2).toString("hex").slice(1)+"-8"+
      randomBytes(2).toString("hex").slice(1)+"-"+
      randomBytes(6).toString("hex"),
    createdAt:new Date(now).toISOString(),
    expiresAt:new Date(now+3600000).toISOString(),
    payloadEncoding:"text/plain",
    bytes:Buffer.from(bytes).toString("base64")
  };
}
const bundled=readFileSync(resolve("dist/index.js"),"utf8");
const mailboxBundle=readFileSync(resolve("../Weave.Mailbox.Worker/dist/index.js"),"utf8");
// Execute the real Weave resource authorization module in a separate workerd
// isolate. No mock token validator or canned claims may stand in for the AS.
const resourceAuthorization=stripTypeScriptTypes(
  readFileSync(resolve("../Weave.Mailbox.Worker/src/remote-oauth.ts"),"utf8"),
  {mode:"strip"}
);
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

// The AS exposes only the default WorkerEntrypoint; the operator rollout must use it.
function options(storage:string,key:string) {
  return {
    resourcePersistencePath:storage,
    cf:false,
    workers:[{
      config:{
        name:"weave-consent-regression",
        // Route only the canonical public CIMD request through a local workerd
        // outbound handler. All other network access is denied; no client is
        // manually registered in the AS KV or sent to a real third party.
        outboundService:(request:Request)=>request.method==="GET"&&request.url===clientId
          ?demoOAuthClientMetadata():new Response("Outbound denied",{status:403}),
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
      config:{
        name:"weave-resource-regression",
        compatibilityDate:"2026-10-13",
        compatibilityFlags:["nodejs_compat","global_fetch_strictly_public"],
        manifest:{mainModule:"resource.js",modules:{"resource.js":{type:"esm",contents:resourceWorker}}},
        env:{
          AUTH_SERVER:{type:"worker",worker:"weave-consent-regression"},
          ACCOUNTS:{type:"d1"},
          OAUTH_ISSUER:{type:"text",value:issuer},
          OAUTH_RESOURCE:{type:"text",value:resource}
        }
      }
    },{
      config:{
        name:"weave-mailbox-regression",
        compatibilityDate:"2026-10-13",
        compatibilityFlags:["nodejs_compat","global_fetch_strictly_public"],
        manifest:{mainModule:"mailbox.js",modules:{"mailbox.js":{type:"esm",contents:mailboxBundle}}},
        env:{
          AUTH_SERVER:{type:"worker",worker:"weave-consent-regression"},
          db:{type:"d1"},
          OAUTH_ISSUER:{type:"text",value:issuer},
          OAUTH_RESOURCE:{type:"text",value:resource},
          OAUTH_ACCOUNT_PROOF_KEY:{type:"text",value:key},
          REGISTRATION_ENABLED:{type:"text",value:"true"},
          REGISTRATION_MAX_ACCOUNTS:{type:"text",value:"10"},
          REGISTRATION_PER_IP_PER_HOUR:{type:"text",value:"10"},
          REGISTRATION_GLOBAL_PER_HOUR:{type:"text",value:"10"},
          CLEANUP_MAX_MESSAGES:{type:"text",value:"0"},
          CLEANUP_TIME_BUDGET_MS:{type:"text",value:"0"}
        }
      }
    }]
  };
}

function consentForm(handle:string,proof:string) {
  return new URLSearchParams({handle,proof,decision:"approve"}).toString();
}

test("a foreign browser cannot burn an owner nonce before consent-cookie verification", {timeout:90_000}, async()=>{
  const storage=await mkdtemp(join(tmpdir(),"weave-auth-consent-"));
  const key=randomBytes(32).toString("base64url");
  let mf=new Miniflare(options(storage,key));
  try {
    // The real production mailbox Worker registers two independently controlled
    // test accounts against its generated D1 schema. No mocked owner IDs.
    let mailbox=await mf.getWorker("weave-mailbox-regression");
    const database=await mf.getD1Database("db","weave-mailbox-regression");
    await initializeDatabase(database,BigInt(Date.now())*10000n);
    const controlA=randomBytes(32).toString("hex");
    const controlB=randomBytes(32).toString("hex");
    async function register(control:string):Promise<string>{
      const res=await mailbox.fetch(new URL("/v1/accounts",resource),{
        method:"POST",headers:{"Content-Type":"application/json","X-Weave-Account-Control":control},body:"{}"
      });
      assert.equal(res.status,200,"real mailbox account registration must succeed");
      const a=await res.json() as {agentId:string};
      assert.match(a.agentId,/^[0-9a-f-]{36}$/);
      return a.agentId;
    }
    const account=await register(controlA);
    const accountB=await register(controlB);
    assert.notEqual(account,accountB);
    async function ownerProof(control:string,nonce:string):Promise<string>{
      // Long-lived controls never enter the authorization-server Worker.
      const res=await mailbox.fetch(new URL("/v1/accounts/proof",resource),{
        method:"POST",headers:{"Content-Type":"application/json","X-Weave-Account-Control":control},
        body:JSON.stringify({challenge:nonce})
      });
      assert.equal(res.status,200,"real Weave owner proof endpoint must sign challenge");
      const value=await res.json() as {proof:string};
      assert.ok(value.proof.startsWith("wop1."));
      return value.proof;
    }
    // The real client flow uses the first-party public CIMD at an HTTPS URL
    // with two independent ephemeral RFC 8252 loopback ports; no DCR or
    // synthetic KV pre-registration is allowed.
    const kv=await mf.getKVNamespace("OAUTH_KV");
    const verifier=randomBytes(32).toString("base64url");
    const challenge=createHash("sha256").update(verifier).digest("base64url");
    const url=new URL(issuer+"/authorize");
    for(const [k,v] of Object.entries({
      response_type:"code",client_id:clientId,redirect_uri:redirectUri,
      scope:"weave:addresses.read weave:addresses.write weave:contacts.read weave:contacts.write weave:messages.read weave:messages.send weave:messages.ack",state:"separate-browser-state",
      resource,code_challenge:challenge,code_challenge_method:"S256"
    }))url.searchParams.set(k,v);

    const page=await mf.dispatchFetch(url.href,{redirect:"manual"});
    assert.equal(page.status,200,"real AS must fetch demo CIMD and accept ephemeral loopback PKCE client");
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
    const proof=await ownerProof(controlA,nonce);

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
    const verifierB=randomBytes(32).toString("base64url");
    const authorizeB=new URL(url);
    authorizeB.searchParams.set("state","independent-second-agent");
    authorizeB.searchParams.set("redirect_uri",redirectUriB);
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
    const proofB=await ownerProof(controlB,nonceB);

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
        redirect_uri:redirectUriB,code_verifier:verifierB,resource
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


    // Full OAuth -> actual /mcp tools/call -> production account-scoped D1.
    // These are isolated test accounts, not real cloud users or model Agents.
    let rpcId=100;
    async function privateCall(token:string,name:string,args:Record<string,unknown>={}):Promise<any>{
      const response=await mailbox.fetch(resource,{
        method:"POST",headers:{"Content-Type":"application/json",
          Accept:"application/json, text/event-stream",
          "MCP-Protocol-Version":"2025-11-25",Authorization:"Bearer "+token},
        body:JSON.stringify({jsonrpc:"2.0",id:rpcId++,method:"tools/call",
          params:{name,arguments:args}})
      });
      assert.equal(response.status,200,"real MCP rejected "+name);
      const frame=await response.json() as {result?:{
        isError?:boolean;structuredContent?:unknown;content?:{text:string}[]}};
      assert.ok(frame.result&&!frame.result.isError,"MCP private tool failed "+name);
      return frame.result.structuredContent??JSON.parse(frame.result.content![0].text);
    }
    assert.deepEqual(await privateCall(grant.access_token!,"get_my_profile"),{id:account});
    assert.deepEqual(await privateCall(grantB.access_token!,"get_my_profile"),{id:accountB});
    const aAddress=randomUUID(),bAddress=randomUUID();
    const addressA=await privateCall(grant.access_token!,"ensure_my_address",
      {addressId:aAddress,label:"Test Agent A"});
    const addressB=await privateCall(grantB.access_token!,"ensure_my_address",
      {addressId:bAddress,label:"Test Agent B"});
    assert.equal(addressA.isPublic,false);
    assert.equal(addressB.isPublic,false);
    const addressesA=await privateCall(grant.access_token!,"list_my_addresses");
    assert.ok(addressesA.items.some((x:{addressId:string})=>x.addressId===aAddress));
    assert.ok(!addressesA.items.some((x:{addressId:string})=>x.addressId===bAddress),
      "account A must not read private B addresses");
    const intro=mailboxPayload("test-consent-introduction");
    const requested=await privateCall(grant.access_token!,"request_contact",{
      requestId:intro.messageId,requesterAddressId:aAddress,
      recipientAccountId:accountB,recipientAddressId:bAddress,payload:intro
    });
    const pending=await privateCall(grantB.access_token!,"list_my_contact_requests");
    assert.ok(pending.items.some((x:{requestId:string;status:string})=>
      x.requestId===intro.messageId&&x.status==="pending"));
    await privateCall(grantB.access_token!,"decide_contact",{
      requestId:intro.messageId,requesterAccountId:account,
      expectedGeneration:String(requested.generation),status:"accepted"
    });
    const deliveries:{token:string;receiverToken:string;sender:string;recipient:string;
      payload:ReturnType<typeof mailboxPayload>}[]=[];
    async function sendMessage(index:number):Promise<void>{
      const fromA=index%2===0;
      const token=fromA?grant.access_token!:grantB.access_token!;
      const receiverToken=fromA?grantB.access_token!:grant.access_token!;
      const sender=fromA?account:accountB,recipient=fromA?accountB:account;
      const payload=mailboxPayload("test-message-"+index);
      const receipt=await privateCall(token,"send_message",{
        recipientAccountId:recipient,contactGeneration:String(requested.generation),payload
      });
      assert.equal(receipt.messageId,payload.messageId);
      assert.equal(receipt.state,"pending");
      deliveries.push({token,receiverToken,sender,recipient,payload});
    }
    // Leave one message in EACH direction pending across a genuine cold restart.
    await sendMessage(0);
    await sendMessage(1);
    await mf.dispose();
    mf=new Miniflare(options(storage,key));
    mailbox=await mf.getWorker("weave-mailbox-regression");
    async function acknowledge(item:(typeof deliveries)[number]):Promise<void>{
      const inbox=await privateCall(item.receiverToken,"list_my_inbox");
      assert.ok(inbox.items.some((x:{envelope?:{payload?:{messageId:string}}})=>
        x.envelope?.payload?.messageId===item.payload.messageId),
        "inbox must retain original pending message after Worker restart");
      const receipt=await privateCall(item.receiverToken,"ack_message",{
        messageId:item.payload.messageId,senderAccountId:item.sender
      });
      assert.equal(receipt.state,"acknowledged");
    }
    await acknowledge(deliveries[0]);
    await acknowledge(deliveries[1]);
    for(let i=2;i<6;i++){
      await sendMessage(i);
      await acknowledge(deliveries[i]);
    }
    const replayedSend=await privateCall(deliveries[0].token,"send_message",{
      recipientAccountId:deliveries[0].recipient,
      contactGeneration:String(requested.generation),payload:deliveries[0].payload
    });
    assert.equal(replayedSend.state,"acknowledged","identical replay returns original ACK");
    const outA=await privateCall(grant.access_token!,"list_my_outbox");
    const outB=await privateCall(grantB.access_token!,"list_my_outbox");
    assert.ok(outA.items.length>=3&&outB.items.length>=3,
      "both accounts retain three outgoing receipts");
    // Real two-Worker Service Binding, real AS KV tokens, and the real
    // resource's authorizeOAuth source running in workerd against D1.
    // This is still isolated emulation, not a live cloud deployment.
    const db=await mf.getD1Database("ACCOUNTS","weave-resource-regression");
    await db.exec("CREATE TABLE accounts (account_id TEXT PRIMARY KEY);");
    await db.prepare("INSERT INTO accounts (account_id) VALUES (?)").bind(account).run();
    await db.prepare("INSERT INTO accounts (account_id) VALUES (?)").bind(accountB).run();
    const worker=await mf.getWorker("weave-resource-regression");
    async function identify(token:string){
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
