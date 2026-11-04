import {readBoundedForm} from "./challenge.ts";
import {signedCookieValue,verifiedCookieValue,type HumanSession} from "./human-session.ts";

/** What a signed owner-action token can name; the mailbox worker re-validates and decides. */
export type OwnerFormAction=
  |{kind:"confirm-binding";requestId:string;keysDigest:string}
  |{kind:"confirm-action";requestId:string;planDigest:string}
  |{kind:"reject-binding"|"confirm-key"|"reject-key"|"reject-action";requestId:string}
  |{kind:"revoke-key";agentId:string;kid:string}
  |{kind:"issue-recovery-code";agentId:string}
  |{kind:"revoke-grant";grantId:string}
  |{kind:"connect-youtube";agentId:string}
  |{kind:"connect-postiz";agentId:string}
  |{kind:"revoke-credential";agentId:string;credentialRef:string};
/** Kinds that need a fresh GitHub authentication before they are offered or applied. Revoking a client's grant is protective and does not. */
export const destructiveKinds:ReadonlySet<OwnerFormAction["kind"]>=
  new Set(["confirm-binding","confirm-key","revoke-key","issue-recovery-code","confirm-action","connect-youtube","connect-postiz"]);

const tokenSeconds=600;
const maxBodyBytes=4096;
const memberSets:Record<OwnerFormAction["kind"],readonly string[]>={
  "confirm-binding":["requestId","keysDigest"],"reject-binding":["requestId"],"confirm-key":["requestId"],"reject-key":["requestId"],
  "confirm-action":["requestId","planDigest"],"reject-action":["requestId"],
  "revoke-key":["agentId","kid"],"issue-recovery-code":["agentId"],"revoke-grant":["grantId"],
  "connect-youtube":["agentId"],"connect-postiz":["agentId"],"revoke-credential":["agentId","credentialRef"]};

/** A token bound to one human and one exact action; it lives as long as the re-authentication window. */
export const ownerActionToken=(key:string,humanId:string,action:OwnerFormAction,now:number):Promise<string>=>
  signedCookieValue(key,"owner-action",{...action,humanId,expiresAt:now+tokenSeconds*1000});

/**
 * The action in a one-field form post; "expired" for a well-formed token of this session's human past its lifetime (the page was
 * open too long); null for anything else.
 */
export async function readOwnerAction(key:string,request:Request,session:HumanSession,now:number):Promise<OwnerFormAction|"expired"|null>{
  const form=await readBoundedForm(request,maxBodyBytes);
  const fields=form?[...form]:[];
  if(fields.length!==1||fields[0][0]!=="action")return null;
  return verifiedAction(key,fields[0][1],session,now);
}

/**
 * The Connect Postiz form: its signed connect-postiz token plus the API key the owner typed. The key is only passed on to the
 * mailbox, never stored, logged or echoed by this worker.
 */
export async function readPostizConnect(key:string,request:Request,session:HumanSession,now:number):Promise<{agentId:string;apiKey:string}|"expired"|null>{
  const form=await readBoundedForm(request,maxBodyBytes);
  const fields=form?[...form]:[];
  if(fields.length!==2||fields[0][0]!=="action"||fields[1][0]!=="apiKey")return null;
  const action=await verifiedAction(key,fields[0][1],session,now);
  if(action==="expired")return action;
  return action?.kind==="connect-postiz"&&fields[1][1].length>0?{agentId:action.agentId,apiKey:fields[1][1]}:null;
}

async function verifiedAction(key:string,token:string,session:HumanSession,now:number):Promise<OwnerFormAction|"expired"|null>{
  const value=await verifiedCookieValue(key,"owner-action",token);
  if(!value)return null;
  const {humanId,kind,expiresAt,...rest}=value;
  if(humanId!==session.humanId||typeof expiresAt!=="number"||!Number.isSafeInteger(expiresAt))return null;
  if(typeof kind!=="string"||!Object.hasOwn(memberSets,kind))return null;
  const members=memberSets[kind as OwnerFormAction["kind"]];
  const names=Object.keys(rest);
  if(names.length!==members.length||!members.every(name=>typeof rest[name]==="string"&&(rest[name] as string).length>0))return null;
  if(now>=expiresAt)return "expired";
  return {kind,...rest} as OwnerFormAction;
}
