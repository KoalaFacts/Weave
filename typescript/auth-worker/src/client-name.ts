import type {ConsentDescription} from "@cloudflare/workers-oauth-provider";

/** C0/C1 controls and the bidi embedding, override and isolate controls, which could make a name read as another. */
const nameControls=/[\u0000-\u001f\u007f-\u009f‪-‮⁦-⁩]/gu;

/**
 * The client's self-reported name as the owner page shows it: at most 128 characters, control and bidi characters as `?`;
 * null when it gave none or only whitespace. Same rule as the mailbox's `recordGrant` (mailbox-worker/src/client-grants.ts).
 */
export function ownerClientName(name:string|null|undefined):string|null{
  if(name===undefined||name===null||name.trim()==="")return null;
  return [...name.replace(nameControls,"?")].slice(0,128).join("");
}

/** The name the consent page showed, as recorded for the owner; the SDK shows the client ID when the client gave no name. */
export const consentClientName=(details:Pick<ConsentDescription,"clientId"|"clientName">)=>
  ownerClientName(details.clientName===details.clientId?null:details.clientName);

/** A value `ownerClientName` would store unchanged. */
export const normalisedClientName=(value:unknown):value is string|null=>
  value===null||typeof value==="string"&&ownerClientName(value)===value;
