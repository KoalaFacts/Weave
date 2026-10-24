import assert from 'node:assert/strict';
import {test} from 'node:test';
import {existsSync,mkdtempSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {randomUUID} from 'node:crypto';

test('read-only doctor reports missing runtime, vault and network without writes',async()=>{
    assert.ok(existsSync(new URL('../run.ts',import.meta.url)),'Missing pair launcher');
    const {doctor}=await import('../run.ts');let writes=0;
    const result=await doctor(undefined,{version:'22.16.0',platform:'linux',execute:async(_cmd,args)=>{if(args.includes('create'))writes++;throw Error('secret');},publicRead:async()=>{throw Error('DNS');},sourceAvailable:true});
    assert.equal(result.ready,false);assert.ok(result.missing.includes('node_24_19_or_newer'));assert.ok(result.missing.includes('codex_login'));assert.ok(result.missing.includes('secure_vault'));assert.ok(result.missing.includes('demo_connectivity'));assert.equal(writes,0);assert.ok(!JSON.stringify(result).includes('secret'));
});
test('doctor rejects an inherited MCP configuration instead of exposing unrelated tools',async()=>{
    const {doctor}=await import('../run.ts');
    const result=await doctor(undefined,{version:'24.19.0',platform:'linux',execute:async(cmd,args)=>cmd==='codex'&&args[0]==='mcp'?'[{"name":"unrelated"}]':'{}',publicRead:async()=>({items:[],nextCursor:null}),sourceAvailable:true});
    assert.ok(result.missing.includes('clean_codex_profile'));
});
test('coordinator forwards identities and step numbers only, never peer message text',async()=>{
    const {exchange}=await import('../run.ts');const calls:any[]=[];
    const process=(role:string)=>({call:async(command:string,value:any)=>{calls.push({role,command,value});if(command==='setup')return {accountId:role==='A'?'11111111-1111-4111-8111-111111111111':'22222222-2222-4222-8222-222222222222',addressId:randomUUID()};if(command==='evidence')throw Error('stop before verification');return {};}});
    await assert.rejects(exchange(process('A'),process('B')));
    const firstSetup=calls.findIndex(c=>c.command==='setup');assert.equal(calls.slice(0,firstSetup).filter(c=>c.command==='probe').length,2);
    assert.deepEqual(calls.filter(c=>c.command==='step').map(c=>c.value),[1,2,3,4,5,6]);
    const second=calls.findIndex(c=>c.command==='step'&&c.value===2),third=calls.findIndex(c=>c.command==='step'&&c.value===3);
    const reconnects=calls.flatMap((c,i)=>c.command==='reconnect'?[i]:[]);
    assert.equal(reconnects.length,2);
    assert.ok(reconnects.every(i=>i>second&&i<third));
    assert.ok(calls.filter(c=>c.command==='peer').every(c=>Object.keys(c.value).sort().join(',')==='accountId,addressId'));
});

test('a released run lock does not permit re-running a retained attempt',async t=>{
    const {claimAttempt}=await import('../run.ts');
    const dir=mkdtempSync(join(tmpdir(),'weave-attempt-'));t.after(()=>rmSync(dir,{recursive:true,force:true}));
    const release=claimAttempt(dir,randomUUID());
    assert.throws(()=>claimAttempt(dir,randomUUID()));release();
    assert.throws(()=>claimAttempt(dir,randomUUID()),/existing_attempt_requires_reconciliation/);
});
test('existing Worker gates run and typecheck the pair without replacing marketplace checks',async()=>{
    const {readFileSync}=await import('node:fs');
    const pkg=JSON.parse(readFileSync(new URL('../../../hosts/Weave.Mailbox.Worker/package.json',import.meta.url),'utf8'));
    const config=JSON.parse(readFileSync(new URL('../../../hosts/Weave.Mailbox.Worker/tsconfig.tools.json',import.meta.url),'utf8'));
    assert.match(pkg.scripts.test,/^npm run test:marketplace && npm run test:pair && /);
    assert.equal(pkg.scripts['test:pair'],'node --test ../../examples/mailbox-demo-pair/test/*.test.ts');
    assert.ok(config.include.includes('../../examples/mailbox-demo-pair/**/*.ts'));
});
test('run rejects a duplicated vault reference before any prerequisite or account operation',async t=>{
    const {mkdtempSync,mkdirSync,rmSync}=await import('node:fs');const {tmpdir}=await import('node:os');const {join}=await import('node:path');
    const {save,ORIGIN}=await import('../runtime.ts');const {main}=await import('../run.ts');
    const state=mkdtempSync(join(tmpdir(),'weave-config-'));t.after(()=>rmSync(state,{recursive:true,force:true}));
    const vault='a'.repeat(26),reference=`op://${vault}/${'b'.repeat(26)}/password`,runId='11111111-1111-4111-8111-111111111111';
    save(join(state,'pair.json'),{version:1,runId,model:'fixture',origin:ORIGIN,vault});
    for(const role of ['A','B']){mkdirSync(join(state,role),{mode:0o700});save(join(state,role,'agent.json'),{role,model:'fixture',runId,origin:ORIGIN,reference});save(join(state,`vault-${role}.json`),{status:'saved',vault,origin:ORIGIN,reference});}
    await assert.rejects(main(['run','--state',state,'--allow-demo-writes']),/vault_references_not_distinct/);
});
