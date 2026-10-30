import assert from 'node:assert/strict';
import {randomBytes} from 'node:crypto';
import {readFileSync} from 'node:fs';
import {mkdtemp,rm} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {join,resolve} from 'node:path';
import {test} from 'node:test';
import {Miniflare} from 'miniflare';
import {createChallenge} from '../src/challenge.ts';

// The exact Wrangler dry-run bundle is loaded by workerd, not a ported or
// reimplemented copy of ConsentNonce. No real accounts, keys or cloud writes.
const issuer='https://auth.weave.example';
const resource='https://demo.weave-389.workers.dev/mcp';
const localKey=randomBytes(32).toString('base64url');
const bundled=readFileSync(resolve('dist/index.js'),'utf8');
// The AS refuses to serve without its human directory binding; this test never calls it.
const mailboxBundle=readFileSync(resolve('../mailbox-worker/dist/index.js'),'utf8');
function options(storage:string) {
  // Miniflare v5 uses the workerd-native Worker manifest, NOT v4
  // scriptPath/kvNamespaces shims. Persist the actual SQLite DO database.
  // The locked Miniflare workerd binary currently supports dates through
  // 2026-10-13. This test-only date preserves the exact bundled application
  // code while exercising real workerd SQLite DO semantics; deployment keeps
  // its later 2026-10-23 compatibility date and needs separate live proof.
  return {
    resourcePersistencePath:storage,
    cf:false,
    workers:[{
      config:{
        name:'weave-auth-local',
        compatibilityDate:'2026-10-13',
        compatibilityFlags:['nodejs_compat','global_fetch_strictly_public'],
        manifest:{mainModule:'index.js',modules:{
          'index.js':{type:'esm',contents:bundled}
        }},
        env:{
          OAUTH_KV:{type:'kv'},
          CONSENT_NONCES:{type:'durable-object',worker:'weave-auth-local',exportName:'ConsentNonce'},
          OAUTH_ISSUER:{type:'text',value:issuer},
          OAUTH_RESOURCE:{type:'text',value:resource},
          OAUTH_ACCOUNT_PROOF_KEY:{type:'text',value:localKey},
          GITHUB_CLIENT_ID:{type:'text',value:'Iv1.test-client-id'},
          GITHUB_CLIENT_SECRET:{type:'text',value:randomBytes(20).toString('hex')},
          HUMAN_SESSION_KEY:{type:'text',value:randomBytes(32).toString('base64url')},
          MAILBOX:{type:'worker',worker:'weave-mailbox-local',exportName:'HumanDirectory'}
        },
        exports:{ConsentNonce:{type:'durable-object',storage:'sqlite'}}
      }
    },{
      config:{
        name:'weave-mailbox-local',
        compatibilityDate:'2026-10-13',
        compatibilityFlags:['nodejs_compat','global_fetch_strictly_public'],
        manifest:{mainModule:'mailbox.js',modules:{'mailbox.js':{type:'esm',contents:mailboxBundle}}},
        env:{db:{type:'d1'}}
      }
    }]
  };
}
const url='https://nonce.internal/nonce';
function request(method:'PUT'|'DELETE',challenge:string) {
  // Miniflare's fetch overload takes its own Request type. Pass the
  // structurally compatible init object, not a Node/Workers Request.
  return {method,headers:{'Content-Type':'application/json'},
    body:JSON.stringify({challenge})};
}

test('real workerd consent nonce atomically blocks parallel replay across cold restarts',async()=>{
  const storage=await mkdtemp(join(tmpdir(),'weave-auth-nonce-'));
  let mf:Miniflare|undefined;
  try {
    mf=new Miniflare(options(storage));
    const metadata=await mf.dispatchFetch(issuer+'/.well-known/oauth-authorization-server');
    assert.equal(metadata.status,200,'actual bundled AS must boot and advertise its issuer');
    const details=await metadata.json() as {issuer?:string};
    assert.equal(details.issuer,issuer);

    const ns=await mf.getDurableObjectNamespace('CONSENT_NONCES');
    const first=ns.get(ns.idFromName('independent-consent-handle'));
    const challenge=createChallenge(),other=createChallenge();
    assert.equal((await first.fetch(url)).status,404,'no nonce exists before a consent begins');
    assert.equal((await first.fetch(url,request('PUT',challenge))).status,201);
    assert.equal((await first.fetch(url,request('PUT',other))).status,409,'one handle cannot silently change identity');
    const stored=await first.fetch(url);
    assert.equal(stored.status,200);
    assert.equal(stored.headers.get('cache-control'),'no-store');
    assert.deepEqual(await stored.json(),{challenge});

    // An actual process disposal and reopened SQLite store must preserve an
    // outstanding transaction; no successful response may lose the nonce.
    await mf.dispose();mf=undefined;
    mf=new Miniflare(options(storage));
    const reopened=await mf.getDurableObjectNamespace('CONSENT_NONCES');
    const second=reopened.get(reopened.idFromName('independent-consent-handle'));
    assert.deepEqual(await (await second.fetch(url)).json(),{challenge});
    const attempts=await Promise.all(Array.from({length:8},()=>second.fetch(url,request('DELETE',challenge))));
    const statuses=attempts.map(x=>x.status).sort((a,b)=>a-b);
    assert.deepEqual(statuses,[204,...Array.from({length:7},()=>409)],
      'only one parallel authorization can consume a signed proof challenge');
    assert.equal((await second.fetch(url)).status,404,'consumed challenge is no longer available');
    assert.equal((await second.fetch(url,request('DELETE',challenge))).status,409,'replay must not reauthorize');

    await mf.dispose();mf=undefined;
    mf=new Miniflare(options(storage));
    const again=await mf.getDurableObjectNamespace('CONSENT_NONCES');
    const third=again.get(again.idFromName('independent-consent-handle'));
    assert.equal((await third.fetch(url)).status,404,'spent nonce stays spent after another cold restart');
  }finally{
    if(mf)await mf.dispose();
    await rm(storage,{recursive:true,force:true});
  }
});
