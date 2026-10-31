import type {OAuthHelpers} from "@cloudflare/workers-oauth-provider";

/** The two SDK operations clean-up needs; `getOAuthApi(env)` in production, a fake in tests. */
export type SdkGrants=Pick<OAuthHelpers,"listUserGrants"|"revokeGrant">;

/*
 * Bounds per owner-page load, kept well under the Workers limit of 1,000 KV operations per invocation:
 * - one `listUserGrants` page is 1 KV list plus 1 get per grant listed, at most 101 operations;
 * - one agent's scan is at most 2 pages (202) and a load scans at most 3 agents (606);
 * - one SDK `revokeGrant` is 1 token list, 1 delete per token still stored under the grant (access tokens expire after
 *   15 minutes) and 1 grant delete; at most 20 per load, so about 20 x 5 = 100 for clients with a few live tokens.
 * That is roughly 700 at worst for ordinary clients, leaving the rest of the request its own headroom.
 */
const sdkRevocationsPerLoad=20,agentsPerLoad=3,pageSize=100,maxPages=2;

export type SdkRevocation={revoked:number;complete:boolean};
export type Cleanup={complete:boolean;failed:boolean};

const metadataGrantId=(metadata:unknown)=>metadata&&typeof metadata==="object"?(metadata as {grantId?:unknown}).grantId:undefined;

/**
 * Revokes, with their access and refresh tokens, the SDK grants of `accountId` (the SDK user ID) whose metadata names one of
 * the mailbox `grantIds`; at most `limit`, scanning at most 200 SDK grants. `complete` is true only when the whole listing
 * was scanned without reaching `limit`. The mailbox mirror is the enforcement point and is already revoked, so anything left
 * here only lets a dead client mint tokens /mcp refuses. Throws when the SDK does.
 */
export async function revokeSdkGrants(api:SdkGrants,accountId:string,grantIds:readonly string[],limit:number):Promise<SdkRevocation>{
  if(grantIds.length===0)return {revoked:0,complete:true};
  if(limit<=0)return {revoked:0,complete:false};
  const wanted=new Set(grantIds);
  let revoked=0,cursor:string|undefined;
  for(let page=0;page<maxPages;page++){
    const listed=await api.listUserGrants(accountId,cursor===undefined?{limit:pageSize}:{limit:pageSize,cursor});
    for(const grant of listed.items){
      const grantId=metadataGrantId(grant.metadata);
      if(typeof grantId!=="string"||!wanted.has(grantId))continue;
      await api.revokeGrant(grant.id,accountId);
      if(++revoked>=limit)return {revoked,complete:false};
    }
    if(listed.cursor===undefined)return {revoked,complete:true};
    cursor=listed.cursor;
  }
  return {revoked,complete:false};
}

/**
 * SDK clean-up for grants the mailbox already revoked, within the per-load bounds above. Agents with revocations are taken
 * in turn from `first` (random by default, so later loads reach agents an earlier one skipped); one agent's SDK failure does
 * not stop the others. `complete` means every listed revocation was looked for in full and nothing failed. Never un-revokes.
 */
export async function cleanUpRevokedGrants(api:SdkGrants,agents:readonly {agentId:string;revokedGrantIds:readonly string[]}[],
  first?:number):Promise<Cleanup>{
  const pending=agents.filter(agent=>agent.revokedGrantIds.length>0);
  if(pending.length===0)return {complete:true,failed:false};
  const start=(first??Math.floor(Math.random()*pending.length))%pending.length;
  const chosen=Array.from({length:Math.min(agentsPerLoad,pending.length)},(_,i)=>pending[(start+i)%pending.length]);
  let budget=sdkRevocationsPerLoad,complete=chosen.length===pending.length,failed=false;
  for(const agent of chosen){
    try{
      const result=await revokeSdkGrants(api,agent.agentId,agent.revokedGrantIds,budget);
      budget-=result.revoked;
      complete&&=result.complete;
    }catch{
      failed=true;complete=false;
    }
  }
  return {complete,failed};
}
