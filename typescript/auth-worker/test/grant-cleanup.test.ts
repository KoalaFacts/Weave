import assert from "node:assert/strict";
import {randomUUID} from "node:crypto";
import {test} from "node:test";
import type {GrantSummary,ListOptions,ListResult} from "@cloudflare/workers-oauth-provider";
import {cleanUpRevokedGrants,revokeSdkGrants,type SdkGrants} from "../src/grant-cleanup.ts";

/**
 * An in-memory SDK grant store for one account. Like KV, it lists keys in key order and its cursor is an opaque position
 * after the last key returned, so revoking a listed grant does not shift later pages.
 */
function fakeSdk(accountId:string,metadata:unknown[]){
  const grants=metadata.map((m,i)=>({id:"g"+String(i).padStart(6,"0"),clientId:"c",userId:accountId,scope:[],metadata:m,createdAt:0} as GrantSummary));
  const revoked:string[]=[];
  let lists=0;
  const api:SdkGrants={
    async listUserGrants(userId:string,options?:ListOptions):Promise<ListResult<GrantSummary>>{
      lists++;
      assert.equal(userId,accountId);
      const after=options?.cursor===undefined?"":Buffer.from(options.cursor,"base64url").toString();
      const remaining=grants.filter(g=>g.id>after&&!revoked.includes(g.id)),limit=options?.limit??1000;
      const items=remaining.slice(0,limit);
      return remaining.length>limit?{items,cursor:Buffer.from(items.at(-1)!.id).toString("base64url")}:{items};
    },
    async revokeGrant(grantId:string,userId:string){assert.equal(userId,accountId);revoked.push(grantId);}
  };
  return {api,grants,revoked,lists:()=>lists};
}
const filler=(count:number)=>Array.from({length:count},()=>({grantId:randomUUID()}));

test("revokeSdkGrants_MatchesOnlyMetadataGrantId",async()=>{
  const accountId=randomUUID(),target=randomUUID(),other=randomUUID();
  const sdk=fakeSdk(accountId,[{grantId:other},{grantId:target},{},null,"x",{grantId:target}]);
  assert.deepEqual(await revokeSdkGrants(sdk.api,accountId,[target],20),{revoked:2,complete:true});
  assert.deepEqual(sdk.revoked.sort(),[sdk.grants[1].id,sdk.grants[5].id].sort());
  assert.deepEqual(await revokeSdkGrants(sdk.api,accountId,[target],20),{revoked:0,complete:true},"idempotent: nothing left to revoke");
});

test("revokeSdkGrants_StopsAtLimitAndWithoutGrantIds",async()=>{
  const accountId=randomUUID(),ids=Array.from({length:5},()=>randomUUID());
  const sdk=fakeSdk(accountId,ids.map(grantId=>({grantId})));
  assert.deepEqual(await revokeSdkGrants(sdk.api,accountId,ids,3),{revoked:3,complete:false});
  assert.equal(sdk.revoked.length,3);
  assert.deepEqual(await revokeSdkGrants(sdk.api,accountId,[],20),{revoked:0,complete:true});
  assert.deepEqual(await revokeSdkGrants(sdk.api,accountId,ids,0),{revoked:0,complete:false});
  assert.equal(sdk.lists(),1,"no listing when nothing may be revoked");
});

test("revokeSdkGrants_MatchesOnTwoPages_RevokesBoth",async()=>{
  const accountId=randomUUID(),first=randomUUID(),second=randomUUID();
  const sdk=fakeSdk(accountId,[{grantId:first},...filler(99),...filler(50),{grantId:second}]);
  assert.deepEqual(await revokeSdkGrants(sdk.api,accountId,[first,second],20),{revoked:2,complete:true});
  assert.deepEqual(sdk.revoked,[sdk.grants[0].id,sdk.grants[150].id]);
});

test("revokeSdkGrants_ScansAtMostTwoPages",async()=>{
  const accountId=randomUUID(),target=randomUUID();
  const near=fakeSdk(accountId,[...filler(150),{grantId:target}]);
  assert.deepEqual(await revokeSdkGrants(near.api,accountId,[target],20),{revoked:1,complete:true});
  const far=fakeSdk(accountId,[...filler(200),{grantId:target}]);
  assert.deepEqual(await revokeSdkGrants(far.api,accountId,[target],20),{revoked:0,complete:false},
    "beyond the scan bound: left to the mirror, which already refuses it");
  assert.equal(far.lists(),2);
});

test("revokeSdkGrants_RevokeThrows_Propagates",async()=>{
  const accountId=randomUUID(),target=randomUUID();
  const sdk=fakeSdk(accountId,[{grantId:target}]);
  sdk.api.revokeGrant=async()=>{throw Error("kv down");};
  await assert.rejects(revokeSdkGrants(sdk.api,accountId,[target],20),/kv down/);
});

/** One SDK serving several agents, each with one SDK grant per revoked mailbox grant. */
function agentsSdk(count:number){
  const agents=Array.from({length:count},()=>({agentId:randomUUID(),revokedGrantIds:[randomUUID()]}));
  const stores=new Map<string,ReturnType<typeof fakeSdk>>(agents.map(a=>[a.agentId,fakeSdk(a.agentId,a.revokedGrantIds.map(grantId=>({grantId})))]));
  const failing=new Set<string>();
  const api:SdkGrants={
    listUserGrants:(userId,options)=>{
      if(failing.has(userId))return Promise.reject(Error("kv down"));
      return stores.get(userId)!.api.listUserGrants(userId,options);
    },
    revokeGrant:(grantId,userId)=>stores.get(userId)!.api.revokeGrant(grantId,userId)
  };
  const cleaned=()=>agents.filter(a=>stores.get(a.agentId)!.revoked.length>0).map(a=>a.agentId);
  return {api,agents,failing,cleaned,lists:()=>[...stores.values()].reduce((n,s)=>n+s.lists(),0)};
}

test("cleanUpRevokedGrants_AllCleaned_Complete",async()=>{
  const sdk=agentsSdk(2);
  assert.deepEqual(await cleanUpRevokedGrants(sdk.api,[...sdk.agents,{agentId:randomUUID(),revokedGrantIds:[]}],0),{complete:true,failed:false});
  assert.deepEqual(sdk.cleaned(),sdk.agents.map(a=>a.agentId));
});

test("cleanUpRevokedGrants_OneAgentFails_OthersStillCleaned",async()=>{
  const sdk=agentsSdk(3);
  sdk.failing.add(sdk.agents[0].agentId);
  assert.deepEqual(await cleanUpRevokedGrants(sdk.api,sdk.agents,0),{complete:false,failed:true});
  assert.deepEqual(sdk.cleaned(),[sdk.agents[1].agentId,sdk.agents[2].agentId]);
});

test("cleanUpRevokedGrants_AtMostThreeAgentsPerLoad_RotatingStart",async()=>{
  const sdk=agentsSdk(5);
  assert.deepEqual(await cleanUpRevokedGrants(sdk.api,sdk.agents,3),{complete:false,failed:false});
  assert.deepEqual(sdk.cleaned().sort(),[sdk.agents[3],sdk.agents[4],sdk.agents[0]].map(a=>a.agentId).sort());
  assert.equal(sdk.lists(),3);
  assert.deepEqual(await cleanUpRevokedGrants(sdk.api,sdk.agents,1),{complete:false,failed:false});
  assert.equal(sdk.cleaned().length,5,"later loads reach the agents an earlier one skipped");
});

test("cleanUpRevokedGrants_RevocationBudgetSpent_Incomplete",async()=>{
  const accountId=randomUUID(),ids=Array.from({length:21},()=>randomUUID());
  const sdk=fakeSdk(accountId,ids.map(grantId=>({grantId})));
  assert.deepEqual(await cleanUpRevokedGrants(sdk.api,[{agentId:accountId,revokedGrantIds:ids}],0),{complete:false,failed:false});
  assert.equal(sdk.revoked.length,20);
});
