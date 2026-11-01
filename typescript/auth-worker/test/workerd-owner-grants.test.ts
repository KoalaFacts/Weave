import assert from "node:assert/strict";
import {createHash,randomBytes,randomUUID} from "node:crypto";
import {readFileSync} from "node:fs";
import {mkdtemp,rm} from "node:fs/promises";
import {tmpdir} from "node:os";
import {join,resolve} from "node:path";
import {after,before,test} from "node:test";
import {Miniflare} from "miniflare";
import {initializeDatabase} from "../../mailbox-worker/scripts/latest-schema.ts";
import {demoOAuthClientId,demoOAuthClientMetadata} from "../../mailbox-worker/src/oauth-client-metadata.ts";
import {signedCookieValue} from "../src/human-session.ts";
import {ownerActionToken} from "../src/owner-forms.ts";

const issuer="https://auth.weave.example";
const resource="https://demo.weave-389.workers.dev/mcp";
const redirectUri="http://127.0.0.1:53621/callback";
const scope="weave:addresses.read offline_access";
const githubClientSecret=randomBytes(20).toString("hex");
const sessionKey=randomBytes(32).toString("base64url");
const bundled=readFileSync(resolve("dist/index.js"),"utf8");
const mailboxBundle=readFileSync(resolve("../mailbox-worker/dist/index.js"),"utf8");
const authName="weave-auth-grants",mailboxName="weave-mailbox-grants",kvName="weave-kv-grants";
// The AS's OAUTH_KV, forwarded to a real KV namespace. POST /arm makes the next token listing (the first step of the
// SDK's revokeGrant) throw once, so a test can fail the SDK revocation without any hook in the product.
const kvProxy=`import {WorkerEntrypoint} from "cloudflare:workers";
let armed=false;
export class ProxiedKv extends WorkerEntrypoint{
  get(key,options){return this.env.INNER.get(key,options);}
  put(key,value,options){return this.env.INNER.put(key,value,options);}
  delete(key){return this.env.INNER.delete(key);}
  list(options){
    if(armed&&options?.prefix?.startsWith("token:")){armed=false;throw new Error("injected KV failure");}
    return this.env.INNER.list(options);
  }
}
export default {async fetch(){armed=true;return new Response(null,{status:204});}};`;

// The demo client's metadata document, served uncacheable so the SDK fetches it each time it needs it; a test may rename
// the client or take the document down, and counts fetches.
const cimd={name:null as string|null,down:false,fetches:0};
async function outbound(request:Request):Promise<Response>{
  if(request.method==="GET"&&request.url===demoOAuthClientId){
    cimd.fetches++;
    if(cimd.down)return new Response("Unavailable",{status:503});
    const document=await demoOAuthClientMetadata().json() as {client_name:string};
    return Response.json({...document,client_name:cimd.name??document.client_name},{headers:{"Cache-Control":"no-store"}});
  }
  return fakeGithub(request);
}
// Fake GitHub: the code "gh-<id>" signs in GitHub user <id> as login "human<id>".
async function fakeGithub(request:Request):Promise<Response>{
  if(request.method==="POST"&&request.url==="https://github.com/login/oauth/access_token"){
    const form=new URLSearchParams(await request.text());
    const id=/^gh-([1-9][0-9]{0,8})$/.exec(form.get("code")??"")?.[1];
    if(form.get("client_secret")!==githubClientSecret||!id)return Response.json({error:"bad_verification_code"});
    return Response.json({access_token:"gho_test_"+id,token_type:"bearer",scope:"read:user"});
  }
  if(request.method==="GET"&&request.url==="https://api.github.com/user"){
    const id=/^Bearer gho_test_([0-9]+)$/.exec(request.headers.get("authorization")??"")?.[1];
    if(!id)return new Response("Bad credentials",{status:401});
    return Response.json({id:Number(id),login:"human"+id,created_at:"2011-01-25T18:44:36Z"});
  }
  return new Response("Outbound denied",{status:403});
}

const logs:string[]=[];
function options(storage:string){
  const common={compatibilityDate:"2026-10-13",compatibilityFlags:["nodejs_compat","global_fetch_strictly_public"]};
  return {
    resourcePersistencePath:storage,cf:false,
    handleStructuredLogs:(log:{message:string})=>{logs.push(log.message);},
    workers:[{
      dev:{outboundService:{type:"fetcher",handler:outbound}},
      config:{
        name:authName,...common,
        manifest:{mainModule:"index.js",modules:{"index.js":{type:"esm",contents:bundled}}},
        env:{
          OAUTH_KV:{type:"worker",worker:kvName,exportName:"ProxiedKv"},
          CONSENT_NONCES:{type:"durable-object",worker:authName,exportName:"ConsentNonce"},
          OAUTH_ISSUER:{type:"text",value:issuer},
          OAUTH_RESOURCE:{type:"text",value:resource},
          GITHUB_CLIENT_ID:{type:"text",value:"Iv1.test-client-id"},
          GITHUB_CLIENT_SECRET:{type:"text",value:githubClientSecret},
          HUMAN_SESSION_KEY:{type:"text",value:sessionKey},
          MAILBOX:{type:"worker",worker:mailboxName,exportName:"HumanDirectory"}
        },
        exports:{ConsentNonce:{type:"durable-object",storage:"sqlite"}}
      }
    },{
      config:{
        name:mailboxName,...common,
        manifest:{mainModule:"mailbox.js",modules:{"mailbox.js":{type:"esm",contents:mailboxBundle}}},
        env:{
          AUTH_SERVER:{type:"worker",worker:authName},
          db:{type:"d1"},
          OAUTH_ISSUER:{type:"text",value:issuer},
          OAUTH_RESOURCE:{type:"text",value:resource},
          REGISTRATION_ENABLED:{type:"text",value:"true"},
          REGISTRATION_MAX_ACCOUNTS:{type:"text",value:"1000"},
          REGISTRATION_PER_IP_PER_HOUR:{type:"text",value:"1000"},
          REGISTRATION_GLOBAL_PER_HOUR:{type:"text",value:"1000"},
          CLEANUP_MAX_MESSAGES:{type:"text",value:"0"},
          CLEANUP_TIME_BUDGET_MS:{type:"text",value:"0"}
        }
      }
    },{
      config:{
        name:kvName,...common,
        manifest:{mainModule:"kv.js",modules:{"kv.js":{type:"esm",contents:kvProxy}}},
        env:{INNER:{type:"kv"}}
      }
    }]
  };
}

let mf:Miniflare,storage:string;
before(async()=>{
  storage=await mkdtemp(join(tmpdir(),"weave-auth-grants-"));
  mf=new Miniflare(options(storage));
  await initializeDatabase(await mf.getD1Database("db",mailboxName),BigInt(Date.now())*10000n);
},{timeout:60_000});
after(async()=>{
  try{await mf.dispose();}
  finally{await rm(storage,{recursive:true,force:true,maxRetries:10,retryDelay:200});}
});
const db=()=>mf.getD1Database("db",mailboxName);

// ---- a human connects the demo client through GitHub sign-in and consent ------------------------------------------------
type Human={humanId:string;login:string};
type Connection={human:Human;accountId:string;grantId:string;accessToken:string;refreshToken:string};
const cookiePairs=(headers:{getSetCookie():string[]},prefix:string)=>
  headers.getSetCookie().filter(x=>x.startsWith(prefix)).map(x=>x.split(";")[0]);
let nextGithubId=9100;
async function tokenEndpoint():Promise<string>{
  return ((await (await mf.dispatchFetch(issuer+"/.well-known/oauth-authorization-server")).json()) as {token_endpoint:string}).token_endpoint;
}
async function connect(githubId=++nextGithubId,agent="new",beforeApproval?:()=>void):Promise<Connection>{
  const {approved,verifier}=await consent(githubId,agent,beforeApproval);
  assert.equal(approved.status,302);
  const code=new URL(approved.headers.get("location")!).searchParams.get("code")!;
  const token=await mf.dispatchFetch(await tokenEndpoint(),{method:"POST",headers:{"Content-Type":"application/x-www-form-urlencoded"},
    body:new URLSearchParams({grant_type:"authorization_code",code,redirect_uri:redirectUri,code_verifier:verifier,client_id:demoOAuthClientId,resource}).toString()});
  assert.equal(token.status,200,await token.clone().text());
  const issued=await token.json() as {access_token:string;refresh_token?:string};
  assert.ok(issued.refresh_token,"offline_access yields a refresh token");
  const human=(await (await db()).prepare("SELECT human_id,display_login FROM humans WHERE subject=?").bind(String(githubId))
    .first<{human_id:string;display_login:string}>())!;
  const grant=(await (await db()).prepare("SELECT grant_id,account_id FROM client_grants WHERE human_id=? ORDER BY created_at DESC LIMIT 1").bind(human.human_id)
    .first<{grant_id:string;account_id:string}>())!;
  return {human:{humanId:human.human_id,login:human.display_login},accountId:grant.account_id,grantId:grant.grant_id,
    accessToken:issued.access_token,refreshToken:issued.refresh_token};
}
/** GitHub sign-in, the consent page, then (after `beforeApproval`) the approval POST, whose response it returns. */
async function consent(githubId:number,agent:string,beforeApproval?:()=>void){
  const verifier=randomBytes(32).toString("base64url"),url=new URL(issuer+"/authorize");
  for(const [k,v] of Object.entries({response_type:"code",client_id:demoOAuthClientId,redirect_uri:redirectUri,scope,state:"s",resource,
    code_challenge:createHash("sha256").update(verifier).digest("base64url"),code_challenge_method:"S256"}))url.searchParams.set(k,v);
  const begin=await mf.dispatchFetch(url.href,{redirect:"manual"});
  const [login]=cookiePairs(begin.headers,"__Host-weave_login=");
  const back=await mf.dispatchFetch(issuer+"/login/github/callback?"+new URLSearchParams({code:"gh-"+githubId,
    state:new URL(begin.headers.get("location")!).searchParams.get("state")!}),{redirect:"manual",headers:{Cookie:login}});
  assert.equal(back.status,302);
  const [session]=cookiePairs(back.headers,"__Host-weave_human=");
  const page=await mf.dispatchFetch(url.href,{redirect:"manual",headers:{Cookie:session}});
  assert.equal(page.status,200);
  const handle=(await page.text()).match(/name="handle" value="([A-Za-z0-9_-]{43})"/)![1];
  beforeApproval?.();
  const approved=await mf.dispatchFetch(issuer+"/authorize",{method:"POST",redirect:"manual",
    headers:{Origin:issuer,Cookie:[session,...page.headers.getSetCookie().map(x=>x.split(";")[0])].join("; "),"Content-Type":"application/x-www-form-urlencoded"},
    body:new URLSearchParams(agent==="new"?{handle,decision:"approve",agent,agentName:"Grants"}:{handle,decision:"approve",agent}).toString()});
  return {approved,verifier};
}
async function refresh(connection:Connection):Promise<Response>{
  return mf.dispatchFetch(await tokenEndpoint(),{method:"POST",headers:{"Content-Type":"application/x-www-form-urlencoded"},
    body:new URLSearchParams({grant_type:"refresh_token",refresh_token:connection.refreshToken,client_id:demoOAuthClientId,resource}).toString()}) as unknown as Promise<Response>;
}
async function mcpStatus(token:string):Promise<number>{
  const mailbox=await mf.getWorker(mailboxName);
  const response=await mailbox.fetch(resource,{method:"POST",headers:{"Content-Type":"application/json",Accept:"application/json, text/event-stream",
    "MCP-Protocol-Version":"2025-11-25",Authorization:"Bearer "+token},
    body:JSON.stringify({jsonrpc:"2.0",id:1,method:"tools/call",params:{name:"get_my_profile",arguments:{}}})});
  await response.arrayBuffer();
  return response.status;
}
/** The SDK grant records stored for an agent, read straight from the AS's KV. */
async function sdkGrants(accountId:string):Promise<{id:string;metadata:unknown}[]>{
  const kv=await mf.getKVNamespace("INNER",kvName);
  const keys=(await kv.list({prefix:"grant:"+accountId+":"})).keys;
  return Promise.all(keys.map(async key=>(await kv.get(key.name,"json")) as {id:string;metadata:unknown}));
}
const mirrorRevoked=async(grantId:string)=>(await (await db()).prepare("SELECT revoked_at FROM client_grants WHERE grant_id=?").bind(grantId)
  .first<{revoked_at:unknown}>())!.revoked_at!==null;

// ---- the owner page --------------------------------------------------------------------------------------------------------
async function sessionCookie(human:Human,authAt=Date.now()):Promise<string>{
  return "__Host-weave_human="+await signedCookieValue(sessionKey,"human-session",{humanId:human.humanId,login:human.login,expiresAt:authAt+3_600_000,authAt});
}
const account=async(cookie:string,path="/account")=>mf.dispatchFetch(issuer+path,{redirect:"manual",headers:{Cookie:cookie}});
const postAccount=(token:string,cookie:string)=>mf.dispatchFetch(issuer+"/account",{method:"POST",redirect:"manual",headers:{
  "Content-Type":"application/x-www-form-urlencoded",Origin:issuer,Cookie:cookie},body:new URLSearchParams({action:token}).toString()});
function revokeForms(html:string):string[]{
  return [...html.matchAll(/name="action" value="([^"]+)"><button type="submit">Revoke access<\/button>/g)].map(m=>m[1]);
}
const result=(response:{headers:{get(name:string):string|null}})=>new URL(response.headers.get("location")!,issuer).searchParams.get("result");

// ---- tests -------------------------------------------------------------------------------------------------------------------
test("consent_SdkGrantMetadataCarriesGrantId",{timeout:60_000},async()=>{
  const connection=await connect();
  const grants=await sdkGrants(connection.accountId);
  assert.equal(grants.length,1);
  assert.deepEqual(grants[0].metadata,{grantId:connection.grantId});
  const row=await (await db()).prepare("SELECT client_id,client_name FROM client_grants WHERE grant_id=?").bind(connection.grantId)
    .first<{client_id:string;client_name:string|null}>();
  assert.deepEqual({...row},{client_id:demoOAuthClientId,client_name:"Weave demo public loopback MCP acceptance client"});
});

test("account_RevokeGrant_StaleSession_DoneNoReauth",{timeout:60_000},async()=>{
  const connection=await connect(),stale=Date.now()-1_800_000,cookie=await sessionCookie(connection.human,stale);
  const html=await (await account(cookie)).text();
  assert.ok(html.includes("Connected clients")&&html.includes("Weave demo public loopback MCP acceptance client (self-reported, not verified)"));
  const tokens=revokeForms(html);
  assert.equal(tokens.length,1);
  const response=await postAccount(tokens[0],cookie);
  assert.equal(response.status,303,"no GitHub re-authentication for a protective action");
  assert.equal(result(response),"done");
  assert.ok(await mirrorRevoked(connection.grantId));
  assert.deepEqual(await sdkGrants(connection.accountId),[]);
  assert.deepEqual(revokeForms(await (await account(cookie)).text()),[],"a revoked client is no longer listed");
});

test("account_RevokeGrant_ClientNext401AndRefreshInvalidGrant",{timeout:60_000},async()=>{
  const connection=await connect(),other=await connect(),cookie=await sessionCookie(connection.human);
  assert.equal(await mcpStatus(connection.accessToken),200,"positive control: the client works before revocation");
  const refreshed=await refresh(connection);
  assert.equal(refreshed.status,200,"positive control: the refresh token works before revocation");
  const rotated=await refreshed.json() as {access_token:string;refresh_token:string};
  const live={...connection,accessToken:rotated.access_token,refreshToken:rotated.refresh_token};
  assert.equal(result(await postAccount(revokeForms(await (await account(cookie)).text())[0],cookie)),"done");
  assert.equal(await mcpStatus(live.accessToken),401);
  for(const attempt of [live,connection]){
    const denied=await refresh(attempt);
    assert.equal(denied.status,400);
    assert.equal((await denied.json() as {error:string}).error,"invalid_grant");
  }
  assert.equal(await mcpStatus(other.accessToken),200,"another human's client is unaffected");
  assert.equal((await refresh(other)).status,200);
});

test("account_RevokeGrant_OtherHumansToken_NotFound",{timeout:60_000},async()=>{
  const alice=await connect(),bob=await connect();
  const aliceCookie=await sessionCookie(alice.human),bobCookie=await sessionCookie(bob.human);
  const aliceToken=revokeForms(await (await account(aliceCookie)).text())[0];
  // Alice's form replayed in Bob's session, and a form signed for Bob that names Alice's grant.
  const forged=await ownerActionToken(sessionKey,bob.human.humanId,{kind:"revoke-grant",grantId:alice.grantId},Date.now());
  for(const token of [aliceToken,forged]){
    const response=await postAccount(token,bobCookie);
    assert.equal(response.status,303);
    assert.equal(result(response),"not-found");
  }
  assert.equal(await mirrorRevoked(alice.grantId),false);
  assert.equal((await sdkGrants(alice.accountId)).length,1);
  assert.equal(await mcpStatus(alice.accessToken),200);
  assert.equal(await mirrorRevoked(bob.grantId),false,"Bob's own grant is untouched too");
});

test("account_SdkRevokeFails_PageSaysRevokedAndNextVisitRetries",{timeout:60_000},async()=>{
  const connection=await connect(),cookie=await sessionCookie(connection.human);
  const token=revokeForms(await (await account(cookie)).text())[0];
  const armed=await (await mf.getWorker(kvName)).fetch("https://kv.internal/arm",{method:"POST"});
  assert.equal(armed.status,204);
  logs.length=0;
  const response=await postAccount(token,cookie);
  assert.equal(response.status,303);
  assert.equal(result(response),"cleanup-pending");
  assert.ok(logs.some(x=>x.includes("grant-sdk-revoke")),JSON.stringify(logs));
  assert.ok(logs.every(x=>!x.includes(connection.refreshToken)&&!x.includes(connection.accessToken)&&!x.includes(token)),"no token is logged");
  assert.ok(await mirrorRevoked(connection.grantId),"the mailbox revocation stands");
  assert.equal(await mcpStatus(connection.accessToken),401,"revoked at the resource despite the SDK failure");
  assert.equal((await sdkGrants(connection.accountId)).length,1,"the SDK grant survived the injected failure");
  // The redirect target is the next visit: it retries the clean-up and says whether that retry finished it.
  assert.equal((await (await mf.getWorker(kvName)).fetch("https://kv.internal/arm",{method:"POST"})).status,204);
  const failedAgain=await (await account(cookie,response.headers.get("location")!)).text();
  assert.ok(failedAgain.includes("Access is revoked; token clean-up will be retried on your next visit."));
  assert.equal((await sdkGrants(connection.accountId)).length,1);
  const page=await (await account(cookie,response.headers.get("location")!)).text();
  assert.ok(page.includes("Access is revoked and token clean-up is complete."));
  assert.ok(!page.includes("will be retried"));
  assert.deepEqual(await sdkGrants(connection.accountId),[]);
  const denied=await refresh(connection);
  assert.equal(denied.status,400);
  assert.equal((await denied.json() as {error:string}).error,"invalid_grant");
  // Idempotent: a further visit finds nothing to do and still renders.
  assert.equal((await account(cookie)).status,200);
  assert.ok(await mirrorRevoked(connection.grantId));
});

test("account_FrozenHumanRevokeGrant_FrozenPageNothingRevoked",{timeout:60_000},async()=>{
  const connection=await connect(),cookie=await sessionCookie(connection.human);
  const token=revokeForms(await (await account(cookie)).text())[0];
  await (await db()).prepare("UPDATE humans SET frozen_at=updated_at WHERE human_id=?").bind(connection.human.humanId).run();
  const response=await postAccount(token,cookie);
  assert.equal(response.status,303);
  assert.equal(result(response),"frozen");
  const page=await (await account(cookie,response.headers.get("location")!)).text();
  assert.ok(page.includes("Nothing can be changed from here."));
  assert.deepEqual(revokeForms(page),[]);
  assert.equal(await mirrorRevoked(connection.grantId),false);
  assert.equal((await sdkGrants(connection.accountId)).length,1);
});

test("consent_ClientRenamedBeforeApproval_RecordsTheNameThePageShowed",{timeout:60_000},async()=>{
  cimd.name="Shown <b>Name</b>";
  let fetches=0;
  try{
    const githubId=++nextGithubId;
    const {approved}=await consent(githubId,"new",()=>{cimd.name="Swapped Name";fetches=cimd.fetches;});
    assert.equal(approved.status,302);
    const row=await (await db()).prepare("SELECT g.client_name FROM client_grants g JOIN humans h ON h.human_id=g.human_id WHERE h.subject=?")
      .bind(String(githubId)).first<{client_name:string}>();
    assert.equal(row!.client_name,"Shown <b>Name</b>");
    // At approval only the SDK's completeAuthorization fetches the metadata document, to check the redirect URI.
    assert.equal(cimd.fetches-fetches,1);
  }finally{cimd.name=null;}
});

test("consent_CimdDownAtApproval_RefusedOnlyByCompleteAuthorization",{timeout:60_000},async()=>{
  let fetches=0;
  try{
    const {approved}=await consent(++nextGithubId,"new",()=>{cimd.down=true;fetches=cimd.fetches;});
    // The recorded name needs no fetch; the SDK still resolves the document in completeAuthorization and refuses there.
    assert.equal(approved.status,400);
    assert.equal(approved.headers.get("location"),null);
    assert.equal(cimd.fetches-fetches,1);
  }finally{cimd.down=false;}
});

test("account_RevokeGrantRevokedLongAgo_CleanupPendingNotDone",{timeout:60_000},async()=>{
  const connection=await connect(),cookie=await sessionCookie(connection.human);
  const token=revokeForms(await (await account(cookie)).text())[0];
  // Revoked 31 days ago by some earlier path: outside the clean-up window, so its SDK grant is never looked for.
  const longAgo=(BigInt(Date.now())-31n*86_400_000n)*10_000n;
  await (await db()).prepare("UPDATE client_grants SET created_at=CAST(? AS INTEGER)-1,revoked_at=CAST(? AS INTEGER) WHERE grant_id=?")
    .bind(longAgo.toString(),longAgo.toString(),connection.grantId).run();
  const response=await postAccount(token,cookie);
  assert.equal(response.status,303);
  assert.equal(result(response),"cleanup-pending");
  assert.equal((await sdkGrants(connection.accountId)).length,1);
});
