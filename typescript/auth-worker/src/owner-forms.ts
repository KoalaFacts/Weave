import {readBoundedForm} from "./challenge.ts";
import {signedCookieValue,verifiedCookieValue,type HumanSession} from "./human-session.ts";

/** What a signed owner-action token can name; the mailbox worker re-validates and decides. */
export type OwnerFormAction=
  |{kind:"confirm-binding";requestId:string;keysDigest:string}
  |{kind:"reject-binding"|"confirm-key"|"reject-key";requestId:string}
  |{kind:"revoke-key";agentId:string;kid:string}
  |{kind:"issue-recovery-code";agentId:string};
/** Kinds that need a fresh GitHub authentication before they are offered or applied. */
export const destructiveKinds:ReadonlySet<OwnerFormAction["kind"]>=
  new Set(["confirm-binding","confirm-key","revoke-key","issue-recovery-code"]);

const tokenSeconds=600;
const maxBodyBytes=4096;
const memberSets:Record<OwnerFormAction["kind"],readonly string[]>={
  "confirm-binding":["requestId","keysDigest"],"reject-binding":["requestId"],"confirm-key":["requestId"],"reject-key":["requestId"],
  "revoke-key":["agentId","kid"],"issue-recovery-code":["agentId"]};

/** A token bound to one human and one exact action; it lives as long as the re-authentication window. */
export const ownerActionToken=(key:string,humanId:string,action:OwnerFormAction,now:number):Promise<string>=>
  signedCookieValue(key,"owner-action",{...action,humanId,expiresAt:now+tokenSeconds*1000});

/** The action in a one-field form post, or null for anything not signed for this session's human and still unexpired. */
export async function readOwnerAction(key:string,request:Request,session:HumanSession,now:number):Promise<OwnerFormAction|null>{
  const form=await readBoundedForm(request,maxBodyBytes);
  const fields=form?[...form]:[];
  if(fields.length!==1||fields[0][0]!=="action")return null;
  const value=await verifiedCookieValue(key,"owner-action",fields[0][1]);
  if(!value)return null;
  const {humanId,kind,expiresAt,...rest}=value;
  if(humanId!==session.humanId||typeof expiresAt!=="number"||!Number.isSafeInteger(expiresAt)||now>=expiresAt)return null;
  if(typeof kind!=="string"||!Object.hasOwn(memberSets,kind))return null;
  const members=memberSets[kind as OwnerFormAction["kind"]];
  const names=Object.keys(rest);
  if(names.length!==members.length||!members.every(name=>typeof rest[name]==="string"&&(rest[name] as string).length>0))return null;
  return {kind,...rest} as OwnerFormAction;
}
