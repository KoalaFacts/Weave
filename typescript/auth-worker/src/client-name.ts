import type {ConsentDescription} from "@cloudflare/workers-oauth-provider";
import {replaceInvisible} from "../../mailbox-worker/src/visible-text.ts";

/**
 * The client's self-reported name as the owner page shows it: at most 128 characters, control, bidi and invisible
 * characters as `?`; null when it gave none or only whitespace. Same rule (and the same character set, from
 * mailbox-worker/src/visible-text.ts) as the mailbox's `recordGrant` (mailbox-worker/src/client-grants.ts).
 */
export function ownerClientName(name:string|null|undefined):string|null{
  if(name===undefined||name===null||name.trim()==="")return null;
  return [...replaceInvisible(name,"?")].slice(0,128).join("");
}

/** The name the consent page showed, as recorded for the owner; the SDK shows the client ID when the client gave no name. */
export const consentClientName=(details:Pick<ConsentDescription,"clientId"|"clientName">)=>
  ownerClientName(details.clientName===details.clientId?null:details.clientName);

/** A value `ownerClientName` would store unchanged. */
export const normalisedClientName=(value:unknown):value is string|null=>
  value===null||typeof value==="string"&&ownerClientName(value)===value;
