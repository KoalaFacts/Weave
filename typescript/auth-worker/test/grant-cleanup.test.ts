import assert from "node:assert/strict";
import {randomUUID} from "node:crypto";
import {test} from "node:test";
import type {GrantSummary,ListOptions,ListResult} from "@cloudflare/workers-oauth-provider";
import {revokeSdkGrants,type SdkGrants} from "../src/grant-cleanup.ts";

/** An in-memory SDK grant store for one account, listed in pages of `options.limit`. */
function fakeSdk(accountId:string,metadata:unknown[]){
  const grants=metadata.map(m=>({id:randomUUID(),clientId:"c",userId:accountId,scope:[],metadata:m,createdAt:0} as GrantSummary));
  const revoked:string[]=[];
  let lists=0;
  const api:SdkGrants={
    async listUserGrants(userId:string,options?:ListOptions):Promise<ListResult<GrantSummary>>{
      lists++;
      assert.equal(userId,accountId);
      const live=grants.filter(g=>!revoked.includes(g.id)),start=Number(options?.cursor??0),limit=options?.limit??1000;
      const items=live.slice(start,start+limit);
      return start+limit<live.length?{items,cursor:String(start+limit)}:{items};
    },
    async revokeGrant(grantId:string,userId:string){assert.equal(userId,accountId);revoked.push(grantId);}
  };
  return {api,grants,revoked,lists:()=>lists};
}

test("revokeSdkGrants_MatchesOnlyMetadataGrantId",async()=>{
  const accountId=randomUUID(),target=randomUUID(),other=randomUUID();
  const sdk=fakeSdk(accountId,[{grantId:other},{grantId:target},{},null,"x",{grantId:target}]);
  assert.equal(await revokeSdkGrants(sdk.api,accountId,[target],20),2);
  assert.deepEqual(sdk.revoked.sort(),[sdk.grants[1].id,sdk.grants[5].id].sort());
  assert.equal(await revokeSdkGrants(sdk.api,accountId,[target],20),0,"idempotent: nothing left to revoke");
});

test("revokeSdkGrants_StopsAtLimitAndWithoutGrantIds",async()=>{
  const accountId=randomUUID(),ids=Array.from({length:5},()=>randomUUID());
  const sdk=fakeSdk(accountId,ids.map(grantId=>({grantId})));
  assert.equal(await revokeSdkGrants(sdk.api,accountId,ids,3),3);
  assert.equal(sdk.revoked.length,3);
  assert.equal(await revokeSdkGrants(sdk.api,accountId,[],20),0);
  assert.equal(await revokeSdkGrants(sdk.api,accountId,ids,0),0);
  assert.equal(sdk.lists(),1,"no listing when nothing may be revoked");
});

test("revokeSdkGrants_FollowsCursorButBoundsPages",async()=>{
  const accountId=randomUUID(),target=randomUUID();
  // The match sits on the third page of 100.
  const sdk=fakeSdk(accountId,[...Array.from({length:250},()=>({grantId:randomUUID()})),{grantId:target}]);
  assert.equal(await revokeSdkGrants(sdk.api,accountId,[target],20),1);
  const far=fakeSdk(accountId,[...Array.from({length:1000},()=>({grantId:randomUUID()})),{grantId:target}]);
  assert.equal(await revokeSdkGrants(far.api,accountId,[target],20),0,"beyond the scan bound: left to the mirror, which already refuses it");
  assert.equal(far.lists(),10);
});

test("revokeSdkGrants_RevokeThrows_Propagates",async()=>{
  const accountId=randomUUID(),target=randomUUID();
  const sdk=fakeSdk(accountId,[{grantId:target}]);
  sdk.api.revokeGrant=async()=>{throw Error("kv down");};
  await assert.rejects(revokeSdkGrants(sdk.api,accountId,[target],20),/kv down/);
});
