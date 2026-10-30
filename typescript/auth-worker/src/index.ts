import {WorkerEntrypoint} from "cloudflare:workers";
import {AuthorizationError,CimdFetchError,OAuthAuthorizationServer} from "@cloudflare/workers-oauth-provider";
import {verifyAccountProof,validateAccountProofBinding,type AccountProofConfig} from "../../mailbox-worker/src/account-proof.ts";
import {createChallenge,validChallenge,validHandle,readConsent} from "./challenge.ts";
import {consentPage} from "./consent-page.ts";
import {completeGithubLogin,type GithubLoginEnv} from "./github-login.ts";
import {validSigningKey} from "./human-session.ts";
export {ConsentNonce} from "./nonce-do.ts";
interface Env extends GithubLoginEnv {
  OAUTH_KV:KVNamespace;
  CONSENT_NONCES:DurableObjectNamespace;
  OAUTH_ISSUER:string;
  OAUTH_RESOURCE:string;
  OAUTH_ACCOUNT_PROOF_KEY:string;
}
const scopes=["weave:addresses.read","weave:addresses.write","weave:contacts.read",
 "weave:contacts.write","weave:messages.read","weave:messages.send","weave:messages.ack","offline_access"];
function config(env:Env,requestUrl?:string):AccountProofConfig{
  const cfg={issuer:env.OAUTH_ISSUER,resource:env.OAUTH_RESOURCE,key:env.OAUTH_ACCOUNT_PROOF_KEY};
  validateAccountProofBinding(cfg,new URL(cfg.resource).origin);
  const issuer=new URL(cfg.issuer);
  if(issuer.pathname!=="/"||issuer.search||issuer.hash||
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
    clientIdMetadataDocumentEnabled:true
    // Open dynamic-client registration deliberately disabled; operator must
    // separately approve DCR policy before exposing public registrations.
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
async function authorize(req:Request,env:Env,server:OAuthAuthorizationServer<Env>):Promise<Response>{
  const cfg=config(env,req.url), oauth=server.getOAuthApi(env);
  try{
    if(req.method==="GET"){
      const parsed=await oauth.parseAuthRequest(req);
      const url=new URL(req.url);
      // Do not infer an account or audience. Reject implicit/plain PKCE.
      if(url.searchParams.getAll("resource").length!==1||parsed.resource!==cfg.resource||
        parsed.codeChallengeMethod!=="S256"||!parsed.codeChallenge||
        parsed.codeChallenge.length!==43)return denied();
      const details=await oauth.describeConsent(parsed);
      const pending=await oauth.beginConsent(parsed);
      if(!validHandle(pending.handle))return unavailable();
      const challenge=createChallenge();
      if(!await putNonce(env,pending.handle,challenge))return unavailable();
      const proofUrl=new URL("/account-proof",cfg.resource);
      proofUrl.searchParams.set("challenge",challenge);
      const page=consentPage(details,pending.handle,proofUrl.href);
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
    const proof=form.get("proof");
    if(!proof||proof.length>2048)return denied();
    const nonce=await getNonce(env,handle);
    if(!nonce)return denied();
    let accountId:string;
    try{accountId=await verifyAccountProof(cfg,proof,nonce);}catch{return denied();}
    // The SDK must validate the original browser's consent cookie BEFORE the
    // owner nonce is consumed. An unbound request carrying a copied proof must
    // never burn the legitimate owner's challenge (real workerd regression).
    const approved=await oauth.approveConsent(req,handle);
    if(approved.request.resource!==cfg.resource||
      approved.request.codeChallengeMethod!=="S256"||
      !approved.request.codeChallenge||approved.request.codeChallenge.length!==43)return denied();
    // SDK consent handles use KV, whose deletion is eventually consistent.
    // Only the Durable Object transaction fences simultaneous/replayed grants;
    // if consumption fails, never create an authorization code.
    if(!await consumeNonce(env,handle,nonce))return denied();
    const {redirectTo}=await oauth.completeAuthorization({
      request:approved.request,userId:accountId,metadata:{},
      scope:approved.request.scope,props:{accountId}
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
    return server.fetch(req,this.env,this.ctx);
  }
  validateToken(resource:string,token:string){
    return provider(this.env).validateToken(resource,token,this.env);
  }
}
