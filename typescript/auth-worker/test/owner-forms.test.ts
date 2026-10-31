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
    {kind:"confirm-binding",requestId:randomUUID(),keysDigest:randomBytes(32).toString("base64url")},{kind:"reject-binding",requestId:randomUUID()},
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

test("readOwnerAction_Expired_ReportsExpiredOnlyForOwnWellFormedToken",async()=>{
  const humanId=randomUUID();
  const token=await ownerActionToken(key,humanId,{kind:"reject-key",requestId:randomUUID()},now);
  assert.equal((await readOwnerAction(key,form(["action",token]),sessionFor(humanId),now+599_999) as {kind:string}).kind,"reject-key");
  assert.equal(await readOwnerAction(key,form(["action",token]),sessionFor(humanId),now+600_000),"expired");
  assert.equal(await readOwnerAction(key,form(["action",token]),sessionFor(randomUUID()),now+600_000),null,"another human's expired token is not reported");
  const malformed=await signedCookieValue(key,"owner-action",{humanId,kind:"delete-everything",requestId:randomUUID(),expiresAt:now});
  assert.equal(await readOwnerAction(key,form(["action",malformed]),sessionFor(humanId),now+1),null);
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
  const token=await ownerActionToken(key,humanId,{kind:"confirm-binding",requestId:x,keysDigest:randomBytes(32).toString("base64url")},now);
  const [payload,signature]=token.split(".");
  const swapped=Buffer.from(Buffer.from(payload,"base64url").toString().replace(x,y)).toString("base64url");
  assert.notEqual(swapped,payload);
  assert.equal(await readOwnerAction(key,form(["action",swapped+"."+signature]),sessionFor(humanId),now),null);
});

test("readOwnerAction_ConfirmBindingTamperedOrMissingKeysDigest_Null",async()=>{
  const humanId=randomUUID(),requestId=randomUUID(),shown=randomBytes(32).toString("base64url"),other=randomBytes(32).toString("base64url");
  const token=await ownerActionToken(key,humanId,{kind:"confirm-binding",requestId,keysDigest:shown},now);
  assert.deepEqual(await readOwnerAction(key,form(["action",token]),sessionFor(humanId),now),{kind:"confirm-binding",requestId,keysDigest:shown});
  const [payload,signature]=token.split(".");
  const swapped=Buffer.from(Buffer.from(payload,"base64url").toString().replace(shown,other)).toString("base64url");
  assert.notEqual(swapped,payload);
  assert.equal(await readOwnerAction(key,form(["action",swapped+"."+signature]),sessionFor(humanId),now),null,"a token cannot carry a different key set");
  const bare=await signedCookieValue(key,"owner-action",{humanId,kind:"confirm-binding",requestId,expiresAt:now+600_000});
  assert.equal(await readOwnerAction(key,form(["action",bare]),sessionFor(humanId),now),null,"a confirmation must name the key set it was shown");
});

test("readOwnerAction_IssueRecoveryCode_RoundTrip",async()=>{
  const humanId=randomUUID(),x=randomUUID(),y=randomUUID();
  const token=await ownerActionToken(key,humanId,{kind:"issue-recovery-code",agentId:x},now);
  assert.deepEqual(await readOwnerAction(key,form(["action",token]),sessionFor(humanId),now),{kind:"issue-recovery-code",agentId:x});
  const [payload,signature]=token.split(".");
  const swapped=Buffer.from(Buffer.from(payload,"base64url").toString().replace(x,y)).toString("base64url");
  assert.equal(await readOwnerAction(key,form(["action",swapped+"."+signature]),sessionFor(humanId),now),null,"a token for one agent cannot name another");
});

test("readOwnerAction_OtherPurposeUnknownKindMissingOrEmptyMember_Null",async()=>{
  const humanId=randomUUID(),session=sessionFor(humanId),expiresAt=now+600_000,requestId=randomUUID();
  const read=(token:string)=>readOwnerAction(key,form(["action",token]),session,now);
  const sign=(payload:object)=>signedCookieValue(key,"owner-action",payload);
  assert.notEqual(await read(await sign({humanId,kind:"confirm-key",requestId,expiresAt})),null,"positive control");
  assert.equal(await read(await signedCookieValue(key,"github-login",{humanId,kind:"confirm-key",requestId,expiresAt})),null);
  assert.equal(await read(await sign({humanId,kind:"delete-everything",requestId,expiresAt})),null);
  assert.equal(await read(await sign({humanId,kind:"revoke-key",agentId:randomUUID(),expiresAt})),null,"missing kid");
  assert.equal(await read(await sign({humanId,kind:"confirm-key",requestId:"",expiresAt})),null);
});

test("ownerActionToken_ActionCannotOverrideHumanId",async()=>{
  const humanId=randomUUID(),other=randomUUID();
  const hostile={kind:"confirm-key",requestId:randomUUID(),humanId:other} as OwnerFormAction;
  const token=await ownerActionToken(key,humanId,hostile,now);
  assert.equal(await readOwnerAction(key,form(["action",token]),sessionFor(other),now),null);
  assert.notEqual(await readOwnerAction(key,form(["action",token]),sessionFor(humanId),now),null);
});

test("readOwnerAction_OversizedStreamedBody_NullWithoutReadingPastCap",async()=>{
  const humanId=randomUUID();
  let pulled=0;
  const body=new ReadableStream<Uint8Array>({pull(controller){pulled+=1024;controller.enqueue(new Uint8Array(1024).fill(97));}});
  const request=new Request("https://auth.weave.example/account/action",{method:"POST",
    headers:{"Content-Type":"application/x-www-form-urlencoded"},body,duplex:"half"} as RequestInit);
  assert.equal(await readOwnerAction(key,request,sessionFor(humanId),now),null);
  assert.ok(pulled<=4096+8192,"stopped reading shortly after the cap, pulled "+pulled);
});

test("readOwnerAction_InvalidUtf8Body_Null",async()=>{
  const request=new Request("https://auth.weave.example/account/action",{method:"POST",
    headers:{"Content-Type":"application/x-www-form-urlencoded"},body:new Uint8Array([0x61,0x63,0x74,0x69,0x6f,0x6e,0x3d,0xff,0xfe])});
  assert.equal(await readOwnerAction(key,request,sessionFor(randomUUID()),now),null);
});
