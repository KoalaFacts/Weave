import assert from "node:assert/strict";
import {randomBytes,randomUUID} from "node:crypto";
import {test} from "node:test";
import {signedCookieValue,type HumanSession} from "../src/human-session.ts";
import {destructiveKinds,ownerActionToken,readOwnerAction,type OwnerFormAction} from "../src/owner-forms.ts";

const now=1_800_000_000_000;
const key=randomBytes(32).toString("base64url");
const sessionFor=(humanId:string):HumanSession=>({humanId,login:"alice",expiresAt:now+3_600_000,authAt:now});
const form=(...fields:[string,string][])=>new Request("https://auth.weave.example/account/action",{method:"POST",
  headers:{"Content-Type":"application/x-www-form-urlencoded"},body:new URLSearchParams(fields).toString()});

test("readOwnerAction_RoundTrip_ReturnsAction",async()=>{
  const humanId=randomUUID(),agentId=randomUUID();
  const actions:OwnerFormAction[]=[
    {kind:"confirm-binding",requestId:randomUUID()},{kind:"reject-binding",requestId:randomUUID()},
    {kind:"confirm-key",requestId:randomUUID()},{kind:"reject-key",requestId:randomUUID()},
    {kind:"revoke-key",agentId,kid:randomBytes(32).toString("base64url")},{kind:"issue-recovery-code",agentId}];
  for(const action of actions){
    const token=await ownerActionToken(key,humanId,action,now);
    assert.deepEqual(await readOwnerAction(key,form(["action",token]),sessionFor(humanId),now+1000),action);
  }
  assert.deepEqual([...destructiveKinds].sort(),["confirm-binding","confirm-key","issue-recovery-code","revoke-key"]);
});

test("readOwnerAction_OtherHumansSession_Null",async()=>{
  const token=await ownerActionToken(key,randomUUID(),{kind:"confirm-key",requestId:randomUUID()},now);
  assert.equal(await readOwnerAction(key,form(["action",token]),sessionFor(randomUUID()),now),null);
});

test("readOwnerAction_Expired_Null",async()=>{
  const humanId=randomUUID();
  const token=await ownerActionToken(key,humanId,{kind:"reject-key",requestId:randomUUID()},now);
  assert.notEqual(await readOwnerAction(key,form(["action",token]),sessionFor(humanId),now+599_999),null);
  assert.equal(await readOwnerAction(key,form(["action",token]),sessionFor(humanId),now+600_000),null);
});

test("readOwnerAction_SessionPurposeSignature_Null",async()=>{
  const humanId=randomUUID();
  const token=await signedCookieValue(key,"human-session",
    {humanId,kind:"confirm-key",requestId:randomUUID(),expiresAt:now+600_000});
  assert.equal(await readOwnerAction(key,form(["action",token]),sessionFor(humanId),now),null);
});

test("readOwnerAction_ExtraFieldOrDuplicateAction_Null",async()=>{
  const humanId=randomUUID(),session=sessionFor(humanId);
  const token=await ownerActionToken(key,humanId,{kind:"confirm-key",requestId:randomUUID()},now);
  const other=await ownerActionToken(key,humanId,{kind:"reject-key",requestId:randomUUID()},now);
  assert.equal(await readOwnerAction(key,form(["action",token],["requestId",randomUUID()]),session,now),null);
  assert.equal(await readOwnerAction(key,form(["action",token],["action",other]),session,now),null);
  assert.equal(await readOwnerAction(key,form(),session,now),null);
  const json=new Request("https://auth.weave.example/account/action",{method:"POST",
    headers:{"Content-Type":"application/json"},body:JSON.stringify({action:token})});
  assert.equal(await readOwnerAction(key,json,session,now),null);
  const oversized=form(["action","x".repeat(4100)]);
  assert.equal(await readOwnerAction(key,oversized,session,now),null);
});

test("readOwnerAction_ExtraTokenMember_Null",async()=>{
  const humanId=randomUUID();
  const token=await signedCookieValue(key,"owner-action",
    {humanId,kind:"confirm-key",requestId:randomUUID(),agentId:randomUUID(),expiresAt:now+600_000});
  assert.equal(await readOwnerAction(key,form(["action",token]),sessionFor(humanId),now),null);
});

test("readOwnerAction_TamperedRequestId_Null",async()=>{
  const humanId=randomUUID(),x=randomUUID(),y=randomUUID();
  const token=await ownerActionToken(key,humanId,{kind:"confirm-binding",requestId:x},now);
  const [payload,signature]=token.split(".");
  const swapped=Buffer.from(Buffer.from(payload,"base64url").toString().replace(x,y)).toString("base64url");
  assert.notEqual(swapped,payload);
  assert.equal(await readOwnerAction(key,form(["action",swapped+"."+signature]),sessionFor(humanId),now),null);
});

test("readOwnerAction_IssueRecoveryCode_RoundTrip",async()=>{
  const humanId=randomUUID(),x=randomUUID(),y=randomUUID();
  const token=await ownerActionToken(key,humanId,{kind:"issue-recovery-code",agentId:x},now);
  assert.deepEqual(await readOwnerAction(key,form(["action",token]),sessionFor(humanId),now),{kind:"issue-recovery-code",agentId:x});
  const [payload,signature]=token.split(".");
  const swapped=Buffer.from(Buffer.from(payload,"base64url").toString().replace(x,y)).toString("base64url");
  assert.equal(await readOwnerAction(key,form(["action",swapped+"."+signature]),sessionFor(humanId),now),null,"a token for one agent cannot name another");
});
