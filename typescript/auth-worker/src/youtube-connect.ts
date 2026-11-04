import type {HumanDirectory} from "../../mailbox-worker/src/index.ts";
import type {ConnectCredentialResult} from "../../mailbox-worker/src/external-credentials.ts";
import {youtubeScopes} from "../../mailbox-worker/src/youtube-provider.ts";
import {createChallenge} from "./challenge.ts";
import {hostCookie,readCookie,signedCookieValue,verifiedCookieValue,type HumanSession} from "./human-session.ts";

export interface YoutubeConnectEnv{
  OAUTH_ISSUER:string;
  HUMAN_SESSION_KEY:string;
  MAILBOX:Service<HumanDirectory>;
  /** Unset means YouTube cannot be connected and the owner page offers no connect form. */
  GOOGLE_CLIENT_ID?:string;
}
// Fixed provider endpoint; never configuration.
const googleAuthorize="https://accounts.google.com/o/oauth2/v2/auth";
const connectCookie="__Host-weave_connect";
const connectSeconds=600;
export const youtubeCallbackPath="/connect/youtube/callback";
const uuid=/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

const s256=async(verifier:string)=>btoa(String.fromCharCode(...new Uint8Array(await crypto.subtle.digest("SHA-256",new TextEncoder().encode(verifier)))))
  .replaceAll("+","-").replaceAll("/","_").replace(/=+$/,"");
const toAccount=(result:string)=>new Response(null,{status:303,headers:{Location:"/account?result="+result,
  "Cache-Control":"no-store","Referrer-Policy":"no-referrer","Set-Cookie":hostCookie(connectCookie,"",0)}});

/**
 * Sends the owner to Google to connect YouTube for one of their agents. The pending connection is a signed cookie bound to
 * this human and agent; the callback accepts only that. Asks for offline access with a fresh consent so a refresh token
 * is issued, and for exactly the scopes the mailbox requires.
 */
export async function beginYoutubeConnect(env:YoutubeConnectEnv,session:HumanSession,agentId:string,now:number):Promise<Response>{
  if(!env.GOOGLE_CLIENT_ID)return toAccount("provider-unavailable");
  const state=createChallenge(),verifier=createChallenge();
  const target=new URL(googleAuthorize);
  target.searchParams.set("client_id",env.GOOGLE_CLIENT_ID);
  target.searchParams.set("redirect_uri",new URL(youtubeCallbackPath,env.OAUTH_ISSUER).href);
  target.searchParams.set("response_type","code");
  target.searchParams.set("scope",youtubeScopes.join(" "));
  target.searchParams.set("access_type","offline");
  target.searchParams.set("prompt","consent select_account");
  target.searchParams.set("include_granted_scopes","false");
  target.searchParams.set("state",state);
  target.searchParams.set("code_challenge",await s256(verifier));
  target.searchParams.set("code_challenge_method","S256");
  const value=await signedCookieValue(env.HUMAN_SESSION_KEY,"youtube-connect",
    {state,verifier,humanId:session.humanId,agentId,expiresAt:now+connectSeconds*1000});
  return new Response(null,{status:302,headers:{Location:target.href,"Set-Cookie":hostCookie(connectCookie,value,connectSeconds),
    "Cache-Control":"no-store","Referrer-Policy":"no-referrer"}});
}

/** Google's redirect back. Only the signed-in human who started this connection, with a matching state, can finish it. */
export async function completeYoutubeConnect(env:YoutubeConnectEnv,request:Request,session:HumanSession|null,now:number):Promise<Response>{
  const url=new URL(request.url);
  const raw=readCookie(request,connectCookie);
  const pending=raw===null?null:await verifiedCookieValue(env.HUMAN_SESSION_KEY,"youtube-connect",raw);
  const {state,verifier,humanId,agentId,expiresAt}=pending??{};
  if(!session||!pending||Object.keys(pending).length!==5||typeof state!=="string"||typeof verifier!=="string"||
    humanId!==session.humanId||typeof agentId!=="string"||!uuid.test(agentId)||typeof expiresAt!=="number"||now>=expiresAt||
    url.searchParams.getAll("state").length!==1||url.searchParams.get("state")!==state)return toAccount("form-expired");
  if(url.searchParams.has("error"))return toAccount(url.searchParams.get("error")==="access_denied"?"connect-cancelled":"provider-refused");
  const code=url.searchParams.get("code");
  if(url.searchParams.getAll("code").length!==1||!code)return toAccount("provider-refused");
  let result:ConnectCredentialResult;
  try{
    result=await env.MAILBOX.connectCredential({humanId:session.humanId,authAt:session.authAt,agentId,provider:"youtube",code,verifier,
      redirectUri:new URL(youtubeCallbackPath,env.OAUTH_ISSUER).href});
  }catch{
    console.error("youtube-connect: directory call failed");
    return toAccount("unavailable");
  }
  return toAccount(result.ok?"connected":result.reason);
}
