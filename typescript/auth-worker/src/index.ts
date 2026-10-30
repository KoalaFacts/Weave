import {WorkerEntrypoint} from "cloudflare:workers";
import {AuthorizationError,CimdFetchError,OAuthAuthorizationServer,type AuthRequest} from "@cloudflare/workers-oauth-provider";
import type {CreateAgentResult} from "../../mailbox-worker/src/humans.ts";
import {createChallenge,validChallenge,validHandle,readConsent} from "./challenge.ts";
import {consentPage} from "./consent-page.ts";
import {beginGithubLogin,completeGithubLogin,failureLog,messagePage,type GithubLoginEnv} from "./github-login.ts";
import {readHumanSession,validSigningKey} from "./human-session.ts";
import {limitRegistration,registrationOpen,type RegistrationLimitEnv} from "./registration-limit.ts";
export {ConsentNonce} from "./nonce-do.ts";
interface Env extends GithubLoginEnv,RegistrationLimitEnv {
  OAUTH_KV:KVNamespace;
  CONSENT_NONCES:DurableObjectNamespace;
  OAUTH_ISSUER:string;
  OAUTH_RESOURCE:string;
}
const scopes=["weave:addresses.read","weave:addresses.write","weave:contacts.read",
 "weave:contacts.write","weave:messages.read","weave:messages.send","weave:messages.ack","offline_access"];
type Binding={issuer:string;resource:string};
/** One exact HTTPS issuer origin and one canonical /mcp resource; anything else is a configuration error. */
function config(env:Env,requestUrl?:string):Binding{
  const cfg={issuer:env.OAUTH_ISSUER,resource:env.OAUTH_RESOURCE};
  if(typeof cfg.issuer!=="string"||typeof cfg.resource!=="string"||
    !URL.canParse(cfg.issuer)||!URL.canParse(cfg.resource))throw Error("Invalid AS binding");
  const issuer=new URL(cfg.issuer),target=new URL(cfg.resource);
  const local=target.hostname==="localhost"||target.hostname==="127.0.0.1";
  if(issuer.protocol!=="https:"||issuer.username||issuer.password||issuer.pathname!=="/"||issuer.search||issuer.hash||
    issuer.href.replace(/\/$/,"")!==cfg.issuer.replace(/\/$/,"")||
    !(target.protocol==="https:"||(local&&target.protocol==="http:"))||target.username||target.password||
    target.search||target.hash||target.pathname!=="/mcp"||target.href!==cfg.resource||
    (requestUrl&&new URL(requestUrl).origin!==issuer.origin))throw Error("Invalid AS binding");
  // Human sign-in is mandatory: no GitHub client, session key or directory means no service at all.
  if(!/^[A-Za-z0-9._-]{1,128}$/.test(env.GITHUB_CLIENT_ID??"")||!/^[!-~]{1,256}$/.test(env.GITHUB_CLIENT_SECRET??"")||
    !validSigningKey(env.HUMAN_SESSION_KEY)||!env.MAILBOX)throw Error("Invalid human sign-in binding");
  return cfg;
}
function provider(env:Env):OAuthAuthorizationServer<Env>{
  const cfg=config(env);
  return new OAuthAuthorizationServer<Env>({
    issuer:cfg.issuer,resources:[cfg.resource],scopesSupported:scopes,
    accessTokenTTL:900,refreshTokenTTL:2_592_000,
    clientIdMetadataDocumentEnabled:true,
    // CIMD stays the primary client path. Open RFC 7591 registration is a fallback
    // for clients without a metadata document; fetch() rate-limits it per address
    // and globally, and the consent page labels such clients as unverified. It is
    // advertised only when both budgets are bound, so metadata never points at a 503.
    ...registrationOpen(env)?{clientRegistrationEndpoint:"/register"}:{}
  });
}
function stub(env:Env,handle:string):DurableObjectStub{
  return env.CONSENT_NONCES.get(env.CONSENT_NONCES.idFromName(handle));
}
async function putNonce(env:Env,handle:string,challenge:string):Promise<boolean>{
  const result=await stub(env,handle).fetch("https://nonce.internal/nonce",{
    method:"PUT",headers:{"Content-Type":"application/json"},body:JSON.stringify({challenge})});
  return result.status===201;
}
async function getNonce(env:Env,handle:string):Promise<string|null>{
  const result=await stub(env,handle).fetch("https://nonce.internal/nonce");
  if(!result.ok)return null;
  const value:unknown=await result.json();
  const challenge=value&&typeof value==="object"&&!Array.isArray(value)?
    (value as {challenge?:unknown}).challenge:null;
  return validChallenge(challenge)?challenge:null;
}
async function consumeNonce(env:Env,handle:string,challenge:string):Promise<boolean>{
  const result=await stub(env,handle).fetch("https://nonce.internal/nonce",{
    method:"DELETE",headers:{"Content-Type":"application/json"},body:JSON.stringify({challenge})});
  return result.status===204;
}
const denied=()=>new Response("Authorization request invalid or expired",{status:400,headers:{"Cache-Control":"no-store"}});
const unavailable=()=>new Response("Authorization not configured",{status:503,headers:{"Cache-Control":"no-store"}});
const directoryUnavailable=()=>{
  console.error(failureLog.directory);
  return messagePage(503,"Temporarily unavailable","Weave cannot reach your agents right now. Try again later.");
};
const agentRefused:Record<Exclude<CreateAgentResult,{ok:true}>["reason"],()=>Response>={
  "agent-limit":()=>messagePage(400,"Agent not created",
    "This GitHub account already has the maximum of 10 agents. Choose an existing agent instead."),
  "rate-limited":()=>messagePage(429,"Agent not created","Too many new agents were created recently. Try again later."),
  frozen:()=>messagePage(403,"Sign-in refused","This GitHub identity is frozen; contact the operator."),
  invalid:()=>messagePage(400,"Agent not created","The agent name must be 1 to 128 characters without control characters."),
  unavailable:()=>messagePage(503,"Agent not created","New agents cannot be created right now. Try again later.")
};
// Do not infer an account or audience. Reject implicit/plain PKCE.
const validRequest=(request:AuthRequest,resource:string)=>request.resource===resource&&
  request.codeChallengeMethod==="S256"&&!!request.codeChallenge&&request.codeChallenge.length===43;
const controlCharacters=/[\u0000-\u001f\u007f-\u009f]/gu;
/** The SDK-approved request as a fresh /authorize URL, so sign-in restarts it without trusting form input. */
function authorizeUrl(issuer:string,request:AuthRequest):URL{
  const url=new URL("/authorize",issuer);
  url.searchParams.set("response_type",request.responseType);
  url.searchParams.set("client_id",request.clientId);
  url.searchParams.set("redirect_uri",request.redirectUri);
  if(request.scope.length)url.searchParams.set("scope",request.scope.join(" "));
  if(request.state)url.searchParams.set("state",request.state);
  url.searchParams.set("resource",request.resource!);
  url.searchParams.set("code_challenge",request.codeChallenge!);
  url.searchParams.set("code_challenge_method",request.codeChallengeMethod!);
  return url;
}
/** The signed-in human's own agent: a new one created now, or an existing one they own. */
async function resolveAgent(env:Env,req:Request,humanId:string,form:URLSearchParams):Promise<string|Response>{
  const agent=form.get("agent")!;
  if(agent==="new"){
    let created:CreateAgentResult;
    try{
      created=await env.MAILBOX.createAgent({humanId,displayName:form.get("agentName")!,
        sourceIp:req.headers.get("CF-Connecting-IP")??"unknown"});
    }catch{return directoryUnavailable();}
    return created.ok?created.accountId:agentRefused[created.reason]();
  }
  let owned:boolean;
  try{owned=await env.MAILBOX.hasAgent(humanId,agent);}catch{return directoryUnavailable();}
  return owned?agent:denied();
}
async function authorize(req:Request,env:Env,server:OAuthAuthorizationServer<Env>):Promise<Response>{
  const cfg=config(env,req.url), oauth=server.getOAuthApi(env);
  try{
    if(req.method==="GET"){
      const parsed=await oauth.parseAuthRequest(req);
      const url=new URL(req.url);
      if(url.searchParams.getAll("resource").length!==1||!validRequest(parsed,cfg.resource))return denied();
      const human=await readHumanSession(env.HUMAN_SESSION_KEY,req,Date.now());
      if(!human)return beginGithubLogin(env,url,Date.now());
      const details=await oauth.describeConsent(parsed);
      const pending=await oauth.beginConsent(parsed);
      if(!validHandle(pending.handle))return unavailable();
      if(!await putNonce(env,pending.handle,createChallenge()))return unavailable();
      let agents;
      try{agents=await env.MAILBOX.listAgents(human.humanId);}catch{return directoryUnavailable();}
      const suggestedName=details.clientName.replace(controlCharacters,"").trim().slice(0,128);
      const page=consentPage(details,pending.handle,{login:human.login},agents,suggestedName);
      const headers=new Headers(page.headers);
      for(const cookie of pending.headers.getSetCookie())headers.append("Set-Cookie",cookie);
      return new Response(page.body,{status:200,headers});
    }
    if(req.method!=="POST"||req.headers.get("origin")!==new URL(req.url).origin)return denied();
    const form=await readConsent(req);
    if(!form)return denied();
    const handle=form.get("handle"),decision=form.get("decision");
    if(!validHandle(handle)||!["approve","deny"].includes(decision??""))return denied();
    if(decision==="deny"){
      const rejected=await oauth.denyConsent(req,handle);
      return new Response(null,{status:302,headers:rejected.headers});
    }
    // The SDK must validate the original browser's consent cookie BEFORE
    // anything else, so a stolen handle can never burn the owner's nonce.
    const approved=await oauth.approveConsent(req,handle);
    if(!validRequest(approved.request,cfg.resource))return denied();
    const human=await readHumanSession(env.HUMAN_SESSION_KEY,req,Date.now());
    if(!human){
      // No code without a live human session: sign in again, then restart this request.
      const login=await beginGithubLogin(env,authorizeUrl(cfg.issuer,approved.request),Date.now());
      const headers=new Headers(login.headers);
      for(const cookie of approved.headers.getSetCookie())headers.append("Set-Cookie",cookie);
      return new Response(login.body,{status:login.status,headers});
    }
    // SDK consent handles use KV, whose deletion is eventually consistent.
    // Only the Durable Object transaction fences simultaneous/replayed grants;
    // if consumption fails, never create an authorization code or an agent.
    const nonce=await getNonce(env,handle);
    if(!nonce||!await consumeNonce(env,handle,nonce))return denied();
    const accountId=await resolveAgent(env,req,human.humanId,form);
    if(accountId instanceof Response)return accountId;
    const {redirectTo}=await oauth.completeAuthorization({
      request:approved.request,userId:accountId,metadata:{},
      scope:approved.request.scope,props:{accountId,humanId:human.humanId}
    });
    const headers=new Headers(approved.headers);
    headers.set("Location",redirectTo);headers.set("Referrer-Policy","no-referrer");
    return new Response(null,{status:302,headers});
  }catch(error){
    if(error instanceof AuthorizationError){
      if(req.method==="GET"&&error.redirectTo)return Response.redirect(error.redirectTo,302);
      return denied();
    }
    if(error instanceof CimdFetchError)return denied();
    throw error;
  }
}
export default class AuthServer extends WorkerEntrypoint<Env>{
  async fetch(req:Request):Promise<Response>{
    let server:OAuthAuthorizationServer<Env>;
    try{config(this.env,req.url);server=provider(this.env);}catch{return unavailable();}
    const path=new URL(req.url).pathname;
    if(path==="/authorize")return authorize(req,this.env,server);
    if(path==="/login/github/callback")return req.method==="GET"?completeGithubLogin(this.env,req,Date.now())
      :new Response("Method not allowed",{status:405,headers:{Allow:"GET","Cache-Control":"no-store"}});
    if(path==="/register"){
      const refused=await limitRegistration(this.env,req);
      if(refused)return refused;
    }
    return server.fetch(req,this.env,this.ctx);
  }
  validateToken(resource:string,token:string){
    return provider(this.env).validateToken(resource,token,this.env);
  }
}
