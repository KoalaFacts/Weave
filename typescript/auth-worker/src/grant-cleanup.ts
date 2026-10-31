import type {OAuthHelpers} from "@cloudflare/workers-oauth-provider";

/** The two SDK operations clean-up needs; `getOAuthApi(env)` in production, a fake in tests. */
export type SdkGrants=Pick<OAuthHelpers,"listUserGrants"|"revokeGrant">;

/** Most SDK grants revoked per owner-page load. */
export const sdkRevocationsPerPage=20;
const pageSize=100,maxPages=10;

const metadataGrantId=(metadata:unknown)=>metadata&&typeof metadata==="object"?(metadata as {grantId?:unknown}).grantId:undefined;

/**
 * Revokes, with their access and refresh tokens, the SDK grants of `accountId` (the SDK user ID) whose metadata names one of
 * the mailbox `grantIds`; at most `limit`, scanning at most 1,000 grants. Returns how many it revoked. The mailbox mirror is
 * the enforcement point and is already revoked, so anything left here only lets a dead client mint tokens /mcp refuses.
 * Throws when the SDK does; the caller logs and retries on a later visit.
 */
export async function revokeSdkGrants(api:SdkGrants,accountId:string,grantIds:readonly string[],limit:number):Promise<number>{
  if(limit<=0||grantIds.length===0)return 0;
  const wanted=new Set(grantIds);
  let revoked=0,cursor:string|undefined;
  for(let page=0;page<maxPages;page++){
    const listed=await api.listUserGrants(accountId,cursor===undefined?{limit:pageSize}:{limit:pageSize,cursor});
    for(const grant of listed.items){
      const grantId=metadataGrantId(grant.metadata);
      if(typeof grantId!=="string"||!wanted.has(grantId))continue;
      await api.revokeGrant(grant.id,accountId);
      if(++revoked>=limit)return revoked;
    }
    if(listed.cursor===undefined)return revoked;
    cursor=listed.cursor;
  }
  return revoked;
}
