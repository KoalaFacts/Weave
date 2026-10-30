import assert from "node:assert/strict";
import {randomBytes, randomUUID} from "node:crypto";
import {test} from "node:test";
import {createChallenge,validChallenge} from "../src/challenge.ts";
import {issueAccountProof,verifyAccountProof} from "../../mailbox-worker/src/account-proof.ts";

test("one AS challenge accepts only a signed first-party proof for its own account and resource",async()=>{
  const config={issuer:"https://auth.weave.example",resource:"https://demo.weave-389.workers.dev/mcp",
    key:randomBytes(32).toString("base64url")};
  const account=randomUUID(), other=randomUUID(), issuedAt=1_800_000_000;
  const challenge=createChallenge(), different=createChallenge();
  assert.equal(validChallenge(challenge),true);
  const proof=await issueAccountProof(config,account,challenge,issuedAt);
  assert.equal(await verifyAccountProof(config,proof,challenge,issuedAt+30),account);
  assert.notEqual(account,other);
  await assert.rejects(verifyAccountProof(config,proof,different,issuedAt+30),/proof/i);
  await assert.rejects(verifyAccountProof(config,proof,challenge,issuedAt+60),/expired|proof/i);
  await assert.rejects(verifyAccountProof({...config,issuer:"https://foreign.example"},proof,challenge,issuedAt+30),/proof/i);
  await assert.rejects(verifyAccountProof({...config,resource:"https://other.example/mcp"},proof,challenge,issuedAt+30),/proof/i);
  await assert.rejects(verifyAccountProof({...config,key:randomBytes(32).toString("base64url")},proof,challenge,issuedAt+30),/signature|proof/i);
  const pieces=proof.split(".");
  const forged=pieces.slice(0,2).join(".")+"."+"A".repeat(43);
  await assert.rejects(verifyAccountProof(config,forged,challenge,issuedAt+30),/signature|proof/i);
});

test("separate account proofs do not collapse distinct subjects even with same issuer and nonce",async()=>{
  const config={issuer:"https://auth.weave.example",resource:"https://demo.weave-389.workers.dev/mcp",
    key:randomBytes(32).toString("base64url")};
  const a=randomUUID(),b=randomUUID(),challenge=createChallenge(),clock=1_800_000_000;
  const proofA=await issueAccountProof(config,a,challenge,clock);
  const proofB=await issueAccountProof(config,b,challenge,clock);
  assert.notEqual(proofA,proofB);
  assert.equal(await verifyAccountProof(config,proofA,challenge,clock+1),a);
  assert.equal(await verifyAccountProof(config,proofB,challenge,clock+1),b);
  // Note: replay prevention is the authorization server's Durable Object,
  // NOT this stateless HMAC verification test.
});
