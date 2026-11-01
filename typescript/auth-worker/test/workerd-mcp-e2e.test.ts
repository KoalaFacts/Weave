import assert from "node:assert/strict";
import {createHash,randomBytes,randomUUID} from "node:crypto";
import {readFileSync} from "node:fs";
import {mkdtemp,rm} from "node:fs/promises";
import {tmpdir} from "node:os";
import {join,resolve} from "node:path";
import {test} from "node:test";
import {Miniflare} from "miniflare";
import {initializeDatabase} from "../../mailbox-worker/scripts/latest-schema.ts";
import {demoOAuthClientId,demoOAuthClientMetadata} from "../../mailbox-worker/src/oauth-client-metadata.ts";

const issuer="https://auth.weave.example";
const resource="https://demo.weave-389.workers.dev/mcp";
const redirectUri="http://127.0.0.1:53621/callback";
const scope="weave:addresses.read weave:addresses.write weave:contacts.read weave:contacts.write weave:messages.read weave:messages.send weave:messages.ack";
const githubClientSecret=randomBytes(20).toString("hex");
const sessionKey=randomBytes(32).toString("base64url");
const bundled=readFileSync(resolve("dist/index.js"),"utf8");
const mailboxBundle=readFileSync(resolve("../mailbox-worker/dist/index.js"),"utf8");
const authName="weave-auth-e2e",mailboxName="weave-mailbox-e2e",rpcName="weave-rpc-e2e";
// A test-only caller that reaches the mailbox HumanDirectory entrypoint exactly as the AS does.
const rpcWorker=`export default {async fetch(request,env){return Response.json(await env.MAILBOX.revokeGrant(await request.json()));}};`;

// Fake GitHub: the code "gh-<id>" signs in GitHub user <id> as login "human<id>".
function outbound(request:Request):Response|Promise<Response>{
  if(request.method==="GET"&&request.url===demoOAuthClientId)return demoOAuthClientMetadata();
  return fakeGithub(request);
}
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

function options(storage:string){
  const common={compatibilityDate:"2026-10-13",compatibilityFlags:["nodejs_compat","global_fetch_strictly_public"]};
  return {
    resourcePersistencePath:storage,cf:false,
    workers:[{
      dev:{outboundService:{type:"fetcher",handler:outbound}},
      config:{
        name:authName,...common,
        manifest:{mainModule:"index.js",modules:{"index.js":{type:"esm",contents:bundled}}},
        env:{
          OAUTH_KV:{type:"kv"},
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
        name:rpcName,...common,
        manifest:{mainModule:"rpc.js",modules:{"rpc.js":{type:"esm",contents:rpcWorker}}},
        env:{MAILBOX:{type:"worker",worker:mailboxName,exportName:"HumanDirectory"}}
      }
    }]
  };
}

const cookiePairs=(headers:{getSetCookie():string[]},prefix:string)=>
  headers.getSetCookie().filter(x=>x.startsWith(prefix)).map(x=>x.split(";")[0]);
type Authorization={url:URL;verifier:string;state:string};
function authorization():Authorization{
  const verifier=randomBytes(32).toString("base64url"),state="state-"+randomUUID();
  const url=new URL(issuer+"/authorize");
  for(const [k,v] of Object.entries({response_type:"code",client_id:demoOAuthClientId,redirect_uri:redirectUri,scope,state,resource,
    code_challenge:createHash("sha256").update(verifier).digest("base64url"),code_challenge_method:"S256"}))url.searchParams.set(k,v);
  return {url,verifier,state};
}
/** Sign in through fake GitHub, consent to a new agent and exchange the code; returns the access token. */
async function connect(mf:Miniflare,githubId:number,agentName:string):Promise<string>{
  const auth=authorization();
  const begin=await mf.dispatchFetch(auth.url.href,{redirect:"manual"});
  assert.equal(begin.status,302);
  const github=new URL(begin.headers.get("location")!);
  const [login]=cookiePairs(begin.headers,"__Host-weave_login=");
  const back=await mf.dispatchFetch(issuer+"/login/github/callback?"+new URLSearchParams({code:"gh-"+githubId,state:github.searchParams.get("state")!}),
    {redirect:"manual",headers:{Cookie:login}});
  assert.equal(back.status,302);
  const [session]=cookiePairs(back.headers,"__Host-weave_human=");
  const page=await mf.dispatchFetch(auth.url.href,{redirect:"manual",headers:{Cookie:session}});
  assert.equal(page.status,200);
  const handle=(await page.text()).match(/name="handle" value="([A-Za-z0-9_-]{43})"/)![1];
  const cookie=[session,...page.headers.getSetCookie().map(x=>x.split(";")[0])].join("; ");
  const approved=await mf.dispatchFetch(issuer+"/authorize",{method:"POST",redirect:"manual",
    headers:{Origin:issuer,Cookie:cookie,"Content-Type":"application/x-www-form-urlencoded"},
    body:new URLSearchParams({handle,decision:"approve",agent:"new",agentName}).toString()});
  assert.equal(approved.status,302);
  const code=new URL(approved.headers.get("location")!).searchParams.get("code");
  assert.ok(code);
  const {token_endpoint}=await (await mf.dispatchFetch(issuer+"/.well-known/oauth-authorization-server")).json() as {token_endpoint:string};
  const token=await mf.dispatchFetch(token_endpoint,{method:"POST",redirect:"manual",headers:{"Content-Type":"application/x-www-form-urlencoded"},
    body:new URLSearchParams({grant_type:"authorization_code",code,redirect_uri:redirectUri,code_verifier:auth.verifier,client_id:demoOAuthClientId,resource}).toString()});
  assert.equal(token.status,200,await token.clone().text());
  return (await token.json() as {access_token:string}).access_token;
}

let rpcId=100;
async function mcp(mf:Miniflare,token:string,name:string,args:Record<string,unknown>={}):Promise<Response>{
  const mailbox=await mf.getWorker(mailboxName);
  return mailbox.fetch(resource,{method:"POST",headers:{"Content-Type":"application/json",Accept:"application/json, text/event-stream",
    "MCP-Protocol-Version":"2025-11-25",Authorization:"Bearer "+token},
    body:JSON.stringify({jsonrpc:"2.0",id:rpcId++,method:"tools/call",params:{name,arguments:args}})}) as unknown as Promise<Response>;
}
async function call(mf:Miniflare,token:string,name:string,args:Record<string,unknown>={}):Promise<any>{
  const response=await mcp(mf,token,name,args);
  assert.equal(response.status,200,name+" must be accepted");
  const frame=await response.json() as {result?:{isError?:boolean;structuredContent?:unknown;content?:{text:string}[]}};
  assert.ok(frame.result&&!frame.result.isError,name+" failed: "+JSON.stringify(frame));
  return frame.result.structuredContent??JSON.parse(frame.result.content![0].text);
}
const text=(value:string)=>({parts:[{text:value}]});

test("twoOwners_FullWorkflowOverMcp",{timeout:240_000},async t=>{
  const storage=await mkdtemp(join(tmpdir(),"weave-auth-e2e-"));
  let mf=new Miniflare(options(storage)),disposed=false;
  const dispose=async()=>{if(!disposed){disposed=true;await mf.dispose();}};
  // An after hook also runs when the test times out, so workerd never outlives it.
  // Windows can briefly hold workerd's files after disposal; removal retries.
  t.after(async()=>{
    try{await dispose();}
    finally{await rm(storage,{recursive:true,force:true,maxRetries:10,retryDelay:200});}
  });
  await initializeDatabase(await mf.getD1Database("db",mailboxName),BigInt(Date.now())*10000n);
  const tokenA=await connect(mf,8101,"Agent A1"),tokenB=await connect(mf,8102,"Agent B1");
  const a=(await call(mf,tokenA,"get_my_profile")).id as string,b=(await call(mf,tokenB,"get_my_profile")).id as string;
  assert.notEqual(a,b);

  const publicA=randomUUID(),addressB=randomUUID();
  await call(mf,tokenA,"ensure_my_address",{addressId:publicA,label:"e2e-agent-a",isPublic:true});
  await call(mf,tokenB,"ensure_my_address",{addressId:addressB,label:"e2e-agent-b"});
  const found=await call(mf,tokenB,"search_registry",{q:"e2e-agent-a"});
  assert.ok(found.items.some((x:{addressId:string;accountId:string})=>x.addressId===publicA&&x.accountId===a),"B1 finds A1's public address");

  const requested=await call(mf,tokenB,"request_contact",{requesterAddressId:addressB,recipientAccountId:a,recipientAddressId:publicA,message:text("hello from B1")});
  const incoming=(await call(mf,tokenA,"list_my_contact_requests")).items as {requestId:string;requesterAccountId:string;status:string;generation:string}[];
  const pending=incoming.find(x=>x.requestId===requested.request.requestId);
  assert.ok(pending,"A1 sees the request");
  assert.equal(pending.requesterAccountId,b);
  assert.equal(pending.status,"pending");
  const accepted=await call(mf,tokenA,"decide_contact",{requestId:pending.requestId,requesterAccountId:b,expectedGeneration:pending.generation,status:"accepted"});
  assert.equal(accepted.isConnected,true);

  // Six messages alternate A1,B1,...; each receiver reads the inbox and acknowledges.
  const sides=[{token:tokenA,id:a},{token:tokenB,id:b}];
  const bodies:string[]=[];
  for(let i=0;i<6;i++){
    const sender=sides[i%2],receiver=sides[(i+1)%2],body="message "+i;
    const receipt=await call(mf,sender.token,"send_message",{recipientAccountId:receiver.id,message:text(body),idempotencyKey:"e2e-"+i});
    const inbox=(await call(mf,receiver.token,"list_my_inbox")).items as {senderAccountId:string;message?:{messageId:string;parts:{text:string}[]}}[];
    const item=inbox.find(x=>x.message?.messageId===receipt.messageId);
    assert.ok(item,"message "+i+" is in the receiver's inbox");
    assert.equal(item.senderAccountId,sender.id);
    bodies.push(item.message!.parts[0].text);
    await call(mf,receiver.token,"ack_message",{messageId:receipt.messageId,senderAccountId:sender.id});
    const after=(await call(mf,receiver.token,"list_my_inbox")).items as {message?:{messageId:string}}[];
    assert.ok(!after.some(x=>x.message?.messageId===receipt.messageId),"an acknowledged message leaves the inbox");
  }
  assert.deepEqual(bodies,["message 0","message 1","message 2","message 3","message 4","message 5"]);

  // Restart: dispose and recreate on the same persisted D1, KV and Durable Object state.
  await dispose();
  mf=new Miniflare(options(storage));
  disposed=false;
  assert.equal((await call(mf,tokenA,"get_my_profile")).id,a,"A1's token survives a restart");
  assert.equal((await call(mf,tokenB,"get_my_profile")).id,b,"B1's token survives a restart");
  const contacts=await call(mf,tokenA,"list_my_contacts");
  assert.ok(contacts.items.some((x:{accountId:string;canSend:boolean})=>x.accountId===b&&x.canSend),"the accepted contact persisted");

  // Revoking B's grant takes effect on B's very next call; A is unaffected.
  const db=await mf.getD1Database("db",mailboxName);
  const grant=await db.prepare("SELECT grant_id,human_id FROM client_grants WHERE account_id=? AND revoked_at IS NULL").bind(b).first<{grant_id:string;human_id:string}>();
  assert.ok(grant,"B1 has a live grant");
  const rpc=await mf.getWorker(rpcName);
  const revoked=await rpc.fetch("https://rpc.internal/",{method:"POST",body:JSON.stringify({humanId:grant.human_id,grantId:grant.grant_id})});
  assert.deepEqual(await revoked.json(),{ok:true});
  const denied=await mcp(mf,tokenB,"list_my_inbox");
  assert.equal(denied.status,401);
  assert.match(denied.headers.get("www-authenticate")??"",/invalid_token/);
  assert.equal((await call(mf,tokenA,"get_my_profile")).id,a,"A1 still works");
  assert.ok(Array.isArray((await call(mf,tokenA,"list_my_inbox")).items));
});
