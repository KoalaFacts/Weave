import type {HumanDirectory} from "../../mailbox-worker/src/index.ts";
import type {SignInResult} from "../../mailbox-worker/src/humans.ts";
import {createChallenge} from "./challenge.ts";
import {hostCookie,issueHumanSession,maxSignedCookieValueLength,readCookie,signedCookieValue,verifiedCookieValue} from "./human-session.ts";

export interface GithubLoginEnv{
  OAUTH_ISSUER:string;
  GITHUB_CLIENT_ID:string;
  GITHUB_CLIENT_SECRET:string;
  HUMAN_SESSION_KEY:string;
  MAILBOX:Service<HumanDirectory>;
}
// Fixed provider endpoints; never configuration.
const githubAuthorize="https://github.com/login/oauth/authorize";
const githubToken="https://github.com/login/oauth/access_token";
const githubUser="https://api.github.com/user";
const loginCookie="__Host-weave_login";
const loginSeconds=600;
// Bound both fixed GitHub HTTP subrequests, including a stalled response stream.
const providerTimeoutMs=3_500;
const githubLogin=/^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$/;
const callbackPath="/login/github/callback";

const esc=(s:string)=>s.replace(/[&<>"']/g,c=>"&#"+c.charCodeAt(0)+";");
/** A minimal no-store HTML page with a single escaped message and no forms. */
export function messagePage(status:number,title:string,message:string):Response{
  const html='<!doctype html><html lang="en"><head><meta charset="utf-8">'+
    '<meta name="viewport" content="width=device-width,initial-scale=1"><title>'+esc(title)+
    "</title></head><body><main><h1>"+esc(title)+"</h1><p>"+esc(message)+"</p></main></body></html>";
  return new Response(html,{status,headers:{
    "Content-Type":"text/html; charset=utf-8","Cache-Control":"no-store",
    "Referrer-Policy":"no-referrer","X-Content-Type-Options":"nosniff","X-Frame-Options":"DENY",
    "Content-Security-Policy":"default-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'"
  }});
}
const invalid=()=>messagePage(400,"Sign-in failed","The sign-in request is invalid or expired. Start again from your app.");
const githubFailed=()=>messagePage(502,"Sign-in failed","GitHub sign-in could not be completed. Try again.");

/** Only this issuer's own /authorize endpoint may receive the browser after sign-in. */
function allowedReturnTo(issuer:string,returnTo:URL):boolean{
  return returnTo.origin===new URL(issuer).origin&&returnTo.pathname==="/authorize"&&
    !returnTo.username&&!returnTo.password&&!returnTo.hash;
}
async function s256(verifier:string):Promise<string>{
  const digest=new Uint8Array(await crypto.subtle.digest("SHA-256",new TextEncoder().encode(verifier)));
  return btoa(String.fromCharCode(...digest)).replaceAll("+","-").replaceAll("/","_").replace(/=+$/,"");
}

export async function beginGithubLogin(env:Pick<GithubLoginEnv,"OAUTH_ISSUER"|"GITHUB_CLIENT_ID"|"HUMAN_SESSION_KEY">,
  returnTo:URL,now:number):Promise<Response>{
  if(!allowedReturnTo(env.OAUTH_ISSUER,returnTo))return invalid();
  const state=createChallenge(),verifier=createChallenge();
  const target=new URL(githubAuthorize);
  target.searchParams.set("client_id",env.GITHUB_CLIENT_ID);
  target.searchParams.set("redirect_uri",new URL(callbackPath,env.OAUTH_ISSUER).href);
  target.searchParams.set("scope","read:user");
  target.searchParams.set("state",state);
  target.searchParams.set("code_challenge",await s256(verifier));
  target.searchParams.set("code_challenge_method","S256");
  const value=await signedCookieValue(env.HUMAN_SESSION_KEY,"github-login",
    {state,verifier,returnTo:returnTo.href,expiresAt:now+loginSeconds*1000});
  // The callback could never read an oversized cookie back; refuse before leaving for GitHub.
  if(value.length>maxSignedCookieValueLength)return messagePage(400,"Authorization request too long",
    "This authorization request is too long to resume after sign-in. Start again from your app with a shorter request.");
  return new Response(null,{status:302,headers:{
    Location:target.href,"Set-Cookie":hostCookie(loginCookie,value,loginSeconds),
    "Cache-Control":"no-store","Referrer-Policy":"no-referrer"}});
}

type PendingLogin={state:string;verifier:string;returnTo:URL};
async function readPendingLogin(env:GithubLoginEnv,request:Request,now:number):Promise<PendingLogin|null>{
  const raw=readCookie(request,loginCookie);
  const value=raw===null?null:await verifiedCookieValue(env.HUMAN_SESSION_KEY,"github-login",raw);
  if(!value||Object.keys(value).length!==4)return null;
  const {state,verifier,returnTo,expiresAt}=value;
  if(typeof state!=="string"||typeof verifier!=="string"||typeof returnTo!=="string"||
    typeof expiresAt!=="number"||now>=expiresAt||!URL.canParse(returnTo))return null;
  const target=new URL(returnTo);
  return allowedReturnTo(env.OAUTH_ISSUER,target)?{state,verifier,returnTo:target}:null;
}
type GithubUser={subject:string;login:string;createdAt:string};
/** Fixed operator-facing failure categories; never tokens, codes or response bodies. */
export const failureLog={exchange:"github-login: token exchange failed",user:"github-login: user fetch failed",
  directory:"human-directory: call failed"} as const;

/** Bound untrusted provider response bodies before JSON decoding/allocating.
 * Fixed endpoints and redirect guards alone do not limit an oversized 2xx body.
 * GitHub's two expected responses are small; unexpected oversized data fails closed.
 */
export async function boundedProviderJson(response:Response,maxBytes:number):Promise<unknown|null>{
  if(!/^application\/json(?:;|$)/i.test(response.headers.get("Content-Type")??""))return null;
  const reader=response.body?.getReader();
  if(!reader)return null;
  const decoder=new TextDecoder("utf-8",{fatal:true});
  // A bounded fetch may return headers before an upstream stops delivering body bytes.
  // Use one deadline for the entire JSON stream, not a fresh timeout per chunk.
  let timer:ReturnType<typeof setTimeout>|undefined;
  const deadline=new Promise<null>(resolve=>{timer=setTimeout(()=>resolve(null),providerTimeoutMs);});
  let bytes=0,text="";
  try{
    while(true){
      const chunk=await Promise.race([reader.read(),deadline]);
      if(chunk===null)return null;
      if(chunk.done)break;
      bytes+=chunk.value.byteLength;
      if(bytes>maxBytes)return null;
      text+=decoder.decode(chunk.value,{stream:true});
    }
    text+=decoder.decode();
    return JSON.parse(text) as unknown;
  }finally{
    if(timer!==undefined)clearTimeout(timer);
    // A provider stream can refuse to finish cancellation after the byte cap is hit
    // or after the read deadline. Never await untrusted cancellation.
    void reader.cancel().catch(()=>{});
    reader.releaseLock();
  }
}
async function githubAccessToken(env:GithubLoginEnv,code:string,verifier:string):Promise<string|null>{
  try{
    // Never follow a provider redirect with the client secret, code or PKCE verifier.
    const exchange=await fetch(githubToken,{method:"POST",redirect:"manual",
      signal:AbortSignal.timeout(providerTimeoutMs),
      headers:{Accept:"application/json","Content-Type":"application/x-www-form-urlencoded","User-Agent":"weave-auth"},
      body:new URLSearchParams({client_id:env.GITHUB_CLIENT_ID,client_secret:env.GITHUB_CLIENT_SECRET,code,
        redirect_uri:new URL(callbackPath,env.OAUTH_ISSUER).href,code_verifier:verifier})});
    if(exchange.ok){
      const grant=await boundedProviderJson(exchange,8*1024);
      const token=grant&&typeof grant==="object"?(grant as {access_token?:unknown}).access_token:null;
      if(typeof token==="string"&&token)return token;
    }
  }catch{/* logged below as a category only */}
  console.error(failureLog.exchange);
  return null;
}
/** The access token stays inside this function: never logged, stored or returned. */
async function fetchGithubUser(env:GithubLoginEnv,code:string,verifier:string):Promise<GithubUser|null>{
  const token=await githubAccessToken(env,code,verifier);
  if(!token)return null;
  try{
    // Likewise, never forward a GitHub bearer token to a redirected endpoint.
    const response=await fetch(githubUser,{redirect:"manual",signal:AbortSignal.timeout(providerTimeoutMs),
      headers:{Authorization:"Bearer "+token,
      Accept:"application/vnd.github+json","User-Agent":"weave-auth"}});
    if(response.ok){
      const user=await boundedProviderJson(response,16*1024);
      const {id,login,created_at:createdAt}=user&&typeof user==="object"?
        user as {id?:unknown;login?:unknown;created_at?:unknown}:{};
      if(typeof id==="number"&&Number.isSafeInteger(id)&&id>=1&&typeof login==="string"&&githubLogin.test(login)&&
        typeof createdAt==="string"&&!Number.isNaN(Date.parse(createdAt)))return {subject:String(id),login,createdAt};
    }
  }catch{/* logged below as a category only */}
  console.error(failureLog.user);
  return null;
}

export async function completeGithubLogin(env:GithubLoginEnv,request:Request,now:number):Promise<Response>{
  const url=new URL(request.url);
  const pending=await readPendingLogin(env,request,now);
  if(!pending||url.searchParams.getAll("state").length!==1||url.searchParams.get("state")!==pending.state)return invalid();
  if(url.searchParams.has("error"))return url.searchParams.get("error")==="access_denied"
    ?messagePage(400,"Sign-in cancelled","GitHub sign-in was cancelled. Start again from your app to continue.")
    :githubFailed();
  const code=url.searchParams.get("code");
  if(url.searchParams.getAll("code").length!==1||!code||!/^[\x21-\x7e]{1,512}$/.test(code))return invalid();
  const user=await fetchGithubUser(env,code,pending.verifier);
  if(!user)return githubFailed();
  let result:SignInResult;
  try{
    result=await env.MAILBOX.signIn({provider:"github",subject:user.subject,
      providerCreatedAt:user.createdAt,displayLogin:user.login});
  }catch{console.error(failureLog.directory);result={ok:false,reason:"unavailable"};}
  if(!result.ok)return result.reason==="frozen"
    ?messagePage(403,"Sign-in refused","This GitHub identity is frozen; contact the operator.")
    :messagePage(503,"Sign-in temporarily unavailable","Sign-in temporarily unavailable. Try again later.");
  const headers=new Headers({Location:pending.returnTo.href,"Cache-Control":"no-store","Referrer-Policy":"no-referrer"});
  headers.append("Set-Cookie",await issueHumanSession(env.HUMAN_SESSION_KEY,{humanId:result.humanId,login:user.login},now));
  headers.append("Set-Cookie",hostCookie(loginCookie,"",0));
  return new Response(null,{status:302,headers});
}
