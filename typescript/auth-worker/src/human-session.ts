import {validChallenge} from "./challenge.ts";

/** A signed-in person; `expiresAt` is epoch milliseconds. Never an agent. */
export type HumanSession={humanId:string;login:string;expiresAt:number};
export const humanSessionCookie="__Host-weave_human";
const sessionSeconds=3600;
const uuid=/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const login=/^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$/;
const encoder=new TextEncoder();

const toBase64Url=(bytes:Uint8Array)=>btoa(String.fromCharCode(...bytes))
  .replaceAll("+","-").replaceAll("/","_").replace(/=+$/,"");
function fromBase64Url(value:string):Uint8Array<ArrayBuffer>|null{
  if(!/^[A-Za-z0-9_-]*$/.test(value))return null;
  try{return Uint8Array.from(atob(value.replaceAll("-","+").replaceAll("_","/")),c=>c.charCodeAt(0));}
  catch{return null;}
}
/** The key must be a canonical base64url 32-byte secret; anything else is a configuration error. */
export const validSigningKey=validChallenge;
async function hmacKey(key:string):Promise<CryptoKey>{
  if(!validSigningKey(key))throw Error("Invalid signing key");
  return crypto.subtle.importKey("raw",fromBase64Url(key)!,{name:"HMAC",hash:"SHA-256"},false,["sign","verify"]);
}

/** Which cookie a signature was made for; a value signed for one purpose never verifies as another. */
export type CookiePurpose="human-session"|"github-login";
const signedData=(purpose:CookiePurpose,json:Uint8Array)=>{
  const tag=encoder.encode("weave-"+purpose+"\n"),data=new Uint8Array(tag.length+json.length);
  data.set(tag);data.set(json,tag.length);return data;
};
/** base64url(JSON) "." base64url(HMAC-SHA256(key, "weave-<purpose>\n" + JSON)). */
export async function signedCookieValue(key:string,purpose:CookiePurpose,payload:object):Promise<string>{
  const json=encoder.encode(JSON.stringify(payload));
  const signature=new Uint8Array(await crypto.subtle.sign("HMAC",await hmacKey(key),signedData(purpose,json)));
  return toBase64Url(json)+"."+toBase64Url(signature);
}
/** Returns the parsed object only for an authentic value; callers still check shape and expiry. */
export async function verifiedCookieValue(key:string,purpose:CookiePurpose,value:string):Promise<Record<string,unknown>|null>{
  const cryptoKey=await hmacKey(key);
  const parts=value.split(".");
  if(parts.length!==2||value.length>2048)return null;
  const json=fromBase64Url(parts[0]),signature=fromBase64Url(parts[1]);
  if(!json||!signature||signature.length!==32)return null;
  if(!await crypto.subtle.verify("HMAC",cryptoKey,signature,signedData(purpose,json)))return null;
  try{
    const parsed:unknown=JSON.parse(new TextDecoder("utf-8",{fatal:true}).decode(json));
    return parsed&&typeof parsed==="object"&&!Array.isArray(parsed)?parsed as Record<string,unknown>:null;
  }catch{return null;}
}
/** Exactly one cookie of that name, or null; a duplicate is ambiguous and refused. */
export function readCookie(request:Request,name:string):string|null{
  const values=(request.headers.get("cookie")??"").split(";").map(x=>x.trim())
    .filter(x=>x.startsWith(name+"=")).map(x=>x.slice(name.length+1));
  return values.length===1?values[0]:null;
}
export const hostCookie=(name:string,value:string,maxAge:number)=>
  name+"="+value+"; Secure; HttpOnly; SameSite=Lax; Path=/; Max-Age="+maxAge;

export async function issueHumanSession(key:string,session:Omit<HumanSession,"expiresAt">,now:number):Promise<string>{
  const value:HumanSession={humanId:session.humanId,login:session.login,expiresAt:now+sessionSeconds*1000};
  return hostCookie(humanSessionCookie,await signedCookieValue(key,"human-session",value),sessionSeconds);
}
export async function readHumanSession(key:string,request:Request,now:number):Promise<HumanSession|null>{
  const raw=readCookie(request,humanSessionCookie);
  const value=raw===null?null:await verifiedCookieValue(key,"human-session",raw);
  if(!value||Object.keys(value).length!==3)return null;
  const {humanId,login:name,expiresAt}=value;
  if(typeof humanId!=="string"||!uuid.test(humanId)||typeof name!=="string"||!login.test(name)||
    typeof expiresAt!=="number"||!Number.isSafeInteger(expiresAt)||now>=expiresAt)return null;
  return {humanId,login:name,expiresAt};
}
