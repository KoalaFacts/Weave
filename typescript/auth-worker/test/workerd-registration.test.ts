import assert from "node:assert/strict";
import {createHash,randomBytes,randomUUID} from "node:crypto";
import {readFileSync} from "node:fs";
import {mkdtemp,rm} from "node:fs/promises";
import {tmpdir} from "node:os";
import {join,resolve} from "node:path";
import {test} from "node:test";
import {Miniflare} from "miniflare";
import {initializeDatabase} from "../../mailbox-worker/scripts/latest-schema.ts";

const issuer="https://auth.weave.example";
const resource="https://demo.weave-389.workers.dev/mcp";
const redirectUri="http://127.0.0.1:53621/callback";
const githubClientSecret=randomBytes(20).toString("hex");
const sessionKey=randomBytes(32).toString("base64url");
const bundled=readFileSync(resolve("dist/index.js"),"utf8");
const mailboxBundle=readFileSync(resolve("../mailbox-worker/dist/index.js"),"utf8");

// Fake GitHub: the code "gh-<id>" signs in GitHub user <id>. Every other destination is refused.
async function outbound(request:Request):Promise<Response>{
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
  return new Response("blocked",{status:502});
}
function options(storage:string,withLimiter:boolean){
  const common={compatibilityDate:"2026-10-13",compatibilityFlags:["nodejs_compat","global_fetch_strictly_public"]};
  return {
    resourcePersistencePath:storage,cf:false,
    workers:[{
      dev:{outboundService:{type:"fetcher",handler:outbound}},
      config:{
        name:"weave-registration-regression",...common,
        manifest:{mainModule:"index.js",modules:{"index.js":{type:"esm",contents:bundled}}},
        env:{
          OAUTH_KV:{type:"kv"},
          CONSENT_NONCES:{type:"durable-object",worker:"weave-registration-regression",exportName:"ConsentNonce"},
          OAUTH_ISSUER:{type:"text",value:issuer},
          OAUTH_RESOURCE:{type:"text",value:resource},
          GITHUB_CLIENT_ID:{type:"text",value:"Iv1.test-client-id"},
          GITHUB_CLIENT_SECRET:{type:"text",value:githubClientSecret},
          HUMAN_SESSION_KEY:{type:"text",value:sessionKey},
          MAILBOX:{type:"worker",worker:"weave-mailbox-regression",exportName:"HumanDirectory"},
          ...withLimiter?{REGISTRATION_LIMITER:{type:"rate-limit",namespace:"weave-registration",simple:{limit:10,period:60}}}:{}
        },
        exports:{ConsentNonce:{type:"durable-object",storage:"sqlite"}}
      }
    },{
      config:{
        name:"weave-mailbox-regression",...common,
        manifest:{mainModule:"mailbox.js",modules:{"mailbox.js":{type:"esm",contents:mailboxBundle}}},
        env:{
          AUTH_SERVER:{type:"worker",worker:"weave-registration-regression"},
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
    }]
  };
}
async function withWorker(run:(mf:Miniflare)=>Promise<void>,withLimiter=true){
  const storage=await mkdtemp(join(tmpdir(),"weave-auth-registration-"));
  const mf=new Miniflare(options(storage,withLimiter) as ConstructorParameters<typeof Miniflare>[0]);
  try{
    await initializeDatabase(await mf.getD1Database("db","weave-mailbox-regression"),BigInt(Date.now())*10000n);
    await run(mf);
  }finally{
    await mf.dispose();
    await rm(storage,{recursive:true,force:true});
  }
}
const register=(mf:Miniflare,ip:string)=>
  mf.dispatchFetch(issuer+"/register",{method:"POST",headers:{"Content-Type":"application/json","CF-Connecting-IP":ip},
    body:JSON.stringify({client_name:"Codex",redirect_uris:[redirectUri],token_endpoint_auth_method:"none",
      grant_types:["authorization_code","refresh_token"],response_types:["code"]})});
const cookiePairs=(headers:{getSetCookie():string[]},prefix:string)=>
  headers.getSetCookie().filter(x=>x.startsWith(prefix)).map(x=>x.split(";")[0]);

test("metadata_AdvertisesRegistrationEndpoint",{timeout:60_000},async()=>{
  await withWorker(async mf=>{
    const response=await mf.dispatchFetch(issuer+"/.well-known/oauth-authorization-server");
    assert.equal(response.status,200);
    const metadata=await response.json() as {registration_endpoint?:string};
    assert.equal(metadata.registration_endpoint,issuer+"/register");
  });
});

test("register_PublicClient_ReturnsClientIdAndConsentLabelsUnverified",{timeout:60_000},async()=>{
  await withWorker(async mf=>{
    const response=await register(mf,"203.0.113.1");
    assert.equal(response.status,201);
    const client=await response.json() as {client_id?:string;client_secret?:string;token_endpoint_auth_method?:string};
    assert.ok(client.client_id);
    assert.equal(client.client_secret,undefined,"a public client must not be issued a secret");
    assert.equal(client.token_endpoint_auth_method,"none");

    const verifier=randomBytes(32).toString("base64url"),state="state-"+randomUUID();
    const url=new URL(issuer+"/authorize");
    for(const [k,v] of Object.entries({
      response_type:"code",client_id:client.client_id,redirect_uri:redirectUri,
      scope:"weave:messages.send",state,resource,
      code_challenge:createHash("sha256").update(verifier).digest("base64url"),code_challenge_method:"S256"
    }))url.searchParams.set(k,v);
    const begin=await mf.dispatchFetch(url.href,{redirect:"manual"});
    assert.equal(begin.status,302);
    const github=new URL(begin.headers.get("location")!);
    const [login]=cookiePairs(begin.headers,"__Host-weave_login=");
    const back=await mf.dispatchFetch(issuer+"/login/github/callback?"+new URLSearchParams(
      {code:"gh-7",state:github.searchParams.get("state")!}),{redirect:"manual",headers:{Cookie:login}});
    const [session]=cookiePairs(back.headers,"__Host-weave_human=");
    assert.ok(session);
    const page=await mf.dispatchFetch(url.href,{redirect:"manual",headers:{Cookie:session}});
    assert.equal(page.status,200);
    const html=await page.text();
    assert.match(html,/not independently verified/);
    assert.match(html,/Codex/);
  });
});

test("register_OverLimitFromOneIp_Returns429",{timeout:60_000},async()=>{
  await withWorker(async mf=>{
    for(let index=0;index<10;index++)assert.equal((await register(mf,"203.0.113.9")).status,201,"registration "+index);
    const refused=await register(mf,"203.0.113.9");
    assert.equal(refused.status,429);
    assert.equal(refused.headers.get("cache-control"),"no-store");
    assert.equal((await register(mf,"203.0.113.10")).status,201,"another address keeps its own budget");
  });
});

test("register_MissingLimiterBinding_Returns503AndOtherRoutesWork",{timeout:60_000},async()=>{
  await withWorker(async mf=>{
    assert.equal((await register(mf,"203.0.113.1")).status,503);
    assert.equal((await mf.dispatchFetch(issuer+"/.well-known/oauth-authorization-server")).status,200);
  },false);
});
