import assert from 'node:assert/strict';
import {test} from 'node:test';
import {existsSync,mkdtempSync,readFileSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';

function state(t:any){const dir=mkdtempSync(join(tmpdir(),'weave-pair-'));t.after(()=>rmSync(dir,{recursive:true,force:true}));return dir;}
test('vault allocation persists reference before account access and never writes the control',async t=>{
    assert.ok(existsSync(new URL('../vault.ts',import.meta.url)),'Missing real vault adapter');
    const {allocate}=await import('../vault.ts');
    const dir=state(t),vault='a'.repeat(26),id='b'.repeat(26),calls:any[]=[];
    const ref=await allocate(dir,vault,'A',async (args:string[])=>{calls.push(args);return JSON.stringify({id,vault:{id:vault},fields:[{value:'NEVER_SAVE_SECRET'}]});});
    assert.equal(ref,`op://${vault}/${id}/password`);
    assert.equal(calls.length,1);assert.ok(calls[0].includes('--generate-password=64,letters,digits'));
    assert.ok(!readFileSync(join(dir,'vault-A.json'),'utf8').includes('NEVER_SAVE_SECRET'));
    assert.equal(await allocate(dir,vault,'A',async()=>{throw Error('should not run');}),ref);
});
test('uncertain vault creation cannot create another key on retry',async t=>{
    const {allocate}=await import('../vault.ts');const dir=state(t);let count=0;
    await assert.rejects(allocate(dir,'a'.repeat(26),'B',async()=>{count++;throw Error('PRIVATE');}));
    await assert.rejects(allocate(dir,'a'.repeat(26),'B',async()=>{count++;return '{}';}));
    assert.equal(count,1);
});
test('secret references require exact item ids and a password field',async()=>{
    const {validateReference}=await import('../vault.ts');
    for(const ref of ['raw-control','op://vault/item/password','op://'+ 'a'.repeat(26)+'/'+ 'b'.repeat(26)+'/password?x=y'])assert.throws(()=>validateReference(ref));
});
test('Codex events require a real completed inference and bounded generated text',async()=>{
    assert.ok(existsSync(new URL('../model.ts',import.meta.url)),'Missing model driver');
    const {parseModelEvents}=await import('../model.ts');
    const events=[{type:'thread.started',thread_id:'01999999-1111-7111-8111-111111111111'},{type:'item.completed',item:{type:'agent_message',text:'{"text":"A real reply"}'}},{type:'turn.completed',usage:{input_tokens:100,output_tokens:20}}];
    assert.equal(parseModelEvents(events.map(x=>JSON.stringify(x)).join('\n')).text,'A real reply');
    assert.throws(()=>parseModelEvents(events.slice(0,2).map(x=>JSON.stringify(x)).join('\n')));
    assert.throws(()=>parseModelEvents(JSON.stringify({type:'item.started',item:{type:'mcp_tool_call'}})));
});
test('model input contains only its own conversation and commands never carry credentials',async()=>{
    const {modelArguments,modelPrompt}=await import('../model.ts');
    const prompt=modelPrompt('A',3,[{sequence:1,text:'own question'},{sequence:2,text:'received reply'}]);
    assert.ok(prompt.includes('own question'));assert.ok(!prompt.includes('WEAVE_ACCOUNT_CONTROL'));
    const args=modelArguments('example-model');assert.ok(args.includes('read-only'));assert.ok(args.includes('--ephemeral'));assert.ok(!args.includes('--dangerously-bypass-approvals-and-sandbox'));
});

test('only the final agent message is parsed, not preliminary commentary',async()=>{
    const {parseModelEvents}=await import('../model.ts');
    const events=[{type:'thread.started',thread_id:'01999999-1111-7111-8111-111111111111'},{type:'item.completed',item:{type:'agent_message',text:'Preparing a reply.'}},{type:'item.completed',item:{type:'agent_message',text:'{"text":"Final reply"}'}},{type:'turn.completed',usage:{input_tokens:100,output_tokens:20}}];
    assert.equal(parseModelEvents(events.map(x=>JSON.stringify(x)).join('\n')).text,'Final reply');
});
test('vault origin mismatch blocks secret resolution',async()=>{
    const {resolveControl}=await import('../vault.ts');let reads=0;
    await assert.rejects(resolveControl('op://'+'a'.repeat(26)+'/'+'b'.repeat(26)+'/password',async()=>{reads++;return 'https://untrusted.example';}),/vault_origin_mismatch/);assert.equal(reads,1);
});
test('model invocation excludes unrelated user configuration while keeping sandboxing enabled',async()=>{
    const {modelArguments}=await import('../model.ts');const args=modelArguments('fixture');
    assert.ok(args.includes('--ignore-user-config'));assert.equal(args[args.indexOf('--sandbox')+1],'read-only');
    assert.ok(!args.some(a=>a.includes('dangerously')||a==='--ignore-rules'));
});
