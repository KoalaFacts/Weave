import assert from "node:assert/strict";
import {test} from "node:test";
import {limitRegistration,registrationLimiterKey,registrationOpen} from "../src/registration-limit.ts";

test("registrationLimiterKey_Ipv4_KeepsAddress",()=>{
  assert.equal(registrationLimiterKey("203.0.113.9"),"v4:203.0.113.9");
  assert.equal(registrationLimiterKey("0.0.0.0"),"v4:0.0.0.0");
});

test("registrationLimiterKey_Ipv6_UsesExpandedSlash64",()=>{
  assert.equal(registrationLimiterKey("2001:db8:1:2::1"),"v6:2001:0db8:0001:0002::/64");
  assert.equal(registrationLimiterKey("2001:DB8:1:2:ffff:ffff:ffff:ffff"),"v6:2001:0db8:0001:0002::/64");
  assert.equal(registrationLimiterKey("2001:db8::"),"v6:2001:0db8:0000:0000::/64");
  assert.equal(registrationLimiterKey("::1"),"v6:0000:0000:0000:0000::/64");
  assert.equal(registrationLimiterKey("2001:db8:1:2:3:4:192.0.2.1"),"v6:2001:0db8:0001:0002::/64");
});

test("registrationLimiterKey_SameSlash64_SharesKeyAndOtherSlash64Differs",()=>{
  const a=registrationLimiterKey("2001:db8:aa:bb::1"),b=registrationLimiterKey("2001:db8:aa:bb:1234:5678:9abc:def0");
  assert.equal(a,b);
  assert.notEqual(a,registrationLimiterKey("2001:db8:aa:bc::1"));
});

test("registrationLimiterKey_Ipv4MappedIpv6_UsesIpv4Key",()=>{
  assert.equal(registrationLimiterKey("::ffff:203.0.113.9"),"v4:203.0.113.9");
  assert.equal(registrationLimiterKey("::FFFF:cb00:7109"),"v4:203.0.113.9");
});

test("registrationLimiterKey_MissingOrUnparseable_ReturnsUnknown",()=>{
  for(const value of [null,"","unknown","203.0.113","203.0.113.256","1.2.3.4.5"," 203.0.113.9","2001:db8::1::2",
    "2001:db8:1:2:3:4:5:6:7","12345::1","2001:db8:g::1",":::","fe80::1%eth0","::ffff:1.2.3"])
    assert.equal(registrationLimiterKey(value),"unknown",String(value));
});

type Call={key:string};
function limiter(result:boolean|Error,calls:Call[]=[]):RateLimit{
  return {limit:async(options:Call)=>{
    calls.push(options);
    if(result instanceof Error)throw result;
    return {success:result};
  }} as RateLimit;
}
const post=(ip?:string)=>new Request("https://auth.weave.example/register",
  {method:"POST",headers:ip?{"CF-Connecting-IP":ip}:{}});

test("limitRegistration_BothAllow_PassesWithPerIpAndGlobalKeys",async()=>{
  const perIp:Call[]=[],global:Call[]=[];
  const result=await limitRegistration({REGISTRATION_LIMITER:limiter(true,perIp),
    REGISTRATION_GLOBAL_LIMITER:limiter(true,global)},post("2001:db8:1:2::7"));
  assert.equal(result,null);
  assert.deepEqual(perIp,[{key:"v6:2001:0db8:0001:0002::/64"}]);
  assert.deepEqual(global,[{key:"global"}]);
});

test("limitRegistration_LimiterThrows_Returns503",async()=>{
  for(const env of [
    {REGISTRATION_LIMITER:limiter(Error("down")),REGISTRATION_GLOBAL_LIMITER:limiter(true)},
    {REGISTRATION_LIMITER:limiter(true),REGISTRATION_GLOBAL_LIMITER:limiter(Error("down"))}
  ]){
    const result=await limitRegistration(env,post("203.0.113.9"));
    assert.equal(result?.status,503);
    assert.equal(result?.headers.get("cache-control"),"no-store");
  }
});

test("limitRegistration_MissingEitherBinding_Returns503",async()=>{
  for(const env of [{},{REGISTRATION_LIMITER:limiter(true)},{REGISTRATION_GLOBAL_LIMITER:limiter(true)}]){
    assert.equal((await limitRegistration(env,post("203.0.113.9")))?.status,503);
    assert.equal(registrationOpen(env),false);
  }
  assert.equal(registrationOpen({REGISTRATION_LIMITER:limiter(true),REGISTRATION_GLOBAL_LIMITER:limiter(true)}),true);
});

test("limitRegistration_PerIpDenied_Returns429WithoutSpendingGlobal",async()=>{
  const global:Call[]=[];
  const result=await limitRegistration({REGISTRATION_LIMITER:limiter(false),
    REGISTRATION_GLOBAL_LIMITER:limiter(true,global)},post("203.0.113.9"));
  assert.equal(result?.status,429);
  assert.equal(result?.headers.get("retry-after"),"60");
  assert.deepEqual(global,[]);
});

test("limitRegistration_GlobalDenied_Returns429",async()=>{
  const result=await limitRegistration({REGISTRATION_LIMITER:limiter(true),
    REGISTRATION_GLOBAL_LIMITER:limiter(false)},post());
  assert.equal(result?.status,429);
  assert.equal(result?.headers.get("retry-after"),"60");
});

test("limitRegistration_NonPost_SpendsNoBudget",async()=>{
  const perIp:Call[]=[],global:Call[]=[];
  const result=await limitRegistration({REGISTRATION_LIMITER:limiter(true,perIp),
    REGISTRATION_GLOBAL_LIMITER:limiter(true,global)},new Request("https://auth.weave.example/register"));
  assert.equal(result,null);
  assert.deepEqual([perIp,global],[[],[]]);
});
