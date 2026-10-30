import assert from 'node:assert/strict';
import {test} from 'node:test';
import {fork} from 'node:child_process';
import {existsSync,mkdtempSync,mkdirSync,readFileSync,rmSync,writeFileSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {delimiter,join} from 'node:path';
import {fileURLToPath} from 'node:url';
import {setTimeout as delay} from 'node:timers/promises';
import {randomUUID} from 'node:crypto';
import {capture,ORIGIN,save} from '../runtime.ts';
import {StdioClient} from '../mcp.ts';

async function waitUntil(check:()=>boolean):Promise<void>{
    const deadline=Date.now()+5000;
    while(!check()){
        if(Date.now()>=deadline)throw Error('local fixture readiness timeout');
        await delay(20);
    }
}

test('an already cancelled capture never attempts to launch the executable',async()=>{
    await assert.rejects(capture('/weave-missing-fixture-executable',[],{signal:AbortSignal.abort()}),/process_cancelled/);
});

test('cancelling an MCP handshake reaps its child before close completes',{skip:process.platform==='win32'},async()=>{
    const root=mkdtempSync(join(tmpdir(),'weave-mcp-cancel-')),marker=join(root,'child.pid');
    const abort=new AbortController();
    const script="process.on('SIGTERM',()=>{});require('node:fs').writeFileSync(process.argv[1],String(process.pid));setInterval(()=>{},1000);";
    const client=new StdioClient(process.execPath,['-e',script,marker],{PATH:process.env.PATH},5000,abort.signal);
    const outcome=client.initialize().then(()=> 'unexpected success',error=>error.message);
    let pid:number|undefined;
    try{
        await waitUntil(()=>existsSync(marker));pid=Number(readFileSync(marker,'utf8'));
        abort.abort();
        assert.equal(await Promise.race([outcome,delay(1000,'request still pending',{ref:false})]),'mcp_transport_failed');
        await client.close();assert.equal(alive(pid),false);
        await assert.rejects(client.call('fixture'),/mcp_transport_closed/);
    }finally{
        await client.close();
        if(pid&&alive(pid))process.kill(pid,'SIGKILL');
        rmSync(root,{recursive:true,force:true});
    }
});
function alive(pid:number):boolean{
    try{process.kill(pid,0);return true;}catch(error){
        if((error as NodeJS.ErrnoException).code==='ESRCH')return false;
        throw error;
    }
}
for(const [command,executable] of [['probe','codex'],['setup','op']] as const){
    test(`worker disconnect reaps a SIGTERM-resistant ${executable} child before exiting`,{skip:process.platform==='win32'},async()=>{
        const root=mkdtempSync(join(tmpdir(),'weave-worker-cleanup-')),bin=join(root,'bin');
        mkdirSync(bin,{mode:0o700});
        const marker=join(bin,'child.pid');
        // Local process-only fixture: no model, vault, credentials or network.
        writeFileSync(join(bin,executable),`#!${process.execPath}\nconst fs=require('node:fs');process.on('SIGTERM',()=>{});fs.writeFileSync(__dirname+'/child.pid',String(process.pid));setInterval(()=>{},1000);\n`,{mode:0o700});
        save(join(root,'agent.json'),{role:'A',runId:randomUUID(),origin:ORIGIN,model:'fixture',reference:`op://${'a'.repeat(26)}/${'b'.repeat(26)}/password`});
        const child=fork(fileURLToPath(new URL('../worker.ts',import.meta.url)),[root],{
            execArgv:[],cwd:root,env:{PATH:bin+delimiter+process.env.PATH},stdio:['ignore','pipe','pipe','ipc']
        });
        child.stdout!.resume();child.stderr!.resume();
        const exited=new Promise<number|null>(resolve=>child.on('exit',resolve));
        let pid:number|undefined;
        try{
            child.send({id:1,command});
            await waitUntil(()=>existsSync(marker));pid=Number(readFileSync(marker,'utf8'));assert.ok(alive(pid));
            child.disconnect();
            const result=await Promise.race([exited,delay(5000,'worker-timeout',{ref:false})]);
            assert.equal(result,0);
            assert.equal(alive(pid),false,'worker exited while its external child was still alive');
        }finally{
            if(child.exitCode===null&&child.signalCode===null)child.kill('SIGKILL');
            await exited;
            if(pid&&alive(pid))process.kill(pid,'SIGKILL');
            rmSync(root,{recursive:true,force:true});
        }
    });
}

test('coordinator shutdown stops descendants even after their worker exits',{skip:process.platform!=='linux'},async()=>{
    const root=mkdtempSync(join(tmpdir(),'weave-pair-descendants-')),bin=join(root,'bin');
    mkdirSync(bin,{mode:0o700});
    const runId=randomUUID(),vault='a'.repeat(26),reference=(role:string)=>`op://${vault}/${(role==='A'?'b':'c').repeat(26)}/password`;
    save(join(root,'pair.json'),{version:1,runId,vault,model:'fixture',origin:ORIGIN});
    for(const role of ['A','B']){
        mkdirSync(join(root,role),{mode:0o700});
        save(join(root,role,'agent.json'),{role,runId,model:'fixture',origin:ORIGIN,reference:reference(role)});
        save(join(root,`vault-${role}.json`),{status:'saved',vault,origin:ORIGIN,reference:reference(role)});
    }
    // Fake executables run only local processes; the probe never completes or reaches setup.
    const descendant="process.on('SIGTERM',()=>{});require('node:fs').writeFileSync('descendant.pid',String(process.pid));setInterval(()=>{},1000);";
    writeFileSync(join(bin,'codex'),`#!${process.execPath}\nif(process.argv[2]==='mcp'){console.log('[]');}else if(process.argv[2]==='exec'){require('node:child_process').spawn(process.execPath,['-e',${JSON.stringify(descendant)}],{stdio:'ignore'});setInterval(()=>{},1000);}\n`,{mode:0o700});
    writeFileSync(join(bin,'op'),`#!${process.execPath}\nconsole.log('{}');\n`,{mode:0o700});
    const driver=join(root,'driver.ts');
    writeFileSync(driver,`globalThis.fetch=async()=>new Response(JSON.stringify({items:[],nextCursor:null}),{headers:{'content-type':'application/json'}});const {main}=await import(${JSON.stringify(new URL('../run.ts',import.meta.url).href)});try{process.exitCode=await main(process.argv.slice(2));}catch{process.exitCode=1;}`);
    const child=fork(driver,['run','--state',root,'--allow-demo-writes'],{
        execArgv:[],cwd:root,env:{PATH:bin+delimiter+process.env.PATH},stdio:['ignore','pipe','pipe','ipc']
    });
    child.stdout!.resume();child.stderr!.resume();
    const exited=new Promise<void>(resolve=>child.on('exit',()=>resolve()));
    const pids:number[]=[];
    const running=(pid:number)=>{try{return !/^\d+ \(.*\) Z /.test(readFileSync(`/proc/${pid}/stat`,'utf8'));}catch(error){if((error as NodeJS.ErrnoException).code==='ENOENT')return false;throw error;}};
    try{
        await waitUntil(()=>['A','B'].every(role=>existsSync(join(root,role,'descendant.pid'))));
        for(const role of ['A','B'])pids.push(Number(readFileSync(join(root,role,'descendant.pid'),'utf8')));
        assert.ok(pids.every(running));child.kill('SIGTERM');
        await Promise.race([exited,delay(5000,undefined,{ref:false}).then(()=>{throw Error('coordinator did not stop');})]);
        assert.equal(pids.some(running),false,'coordinator exited with running descendants');
    }finally{
        if(child.exitCode===null&&child.signalCode===null)child.kill('SIGKILL');
        await exited;
        for(const role of ['A','B']){const marker=join(root,role,'descendant.pid');if(existsSync(marker)){const pid=Number(readFileSync(marker,'utf8'));if(running(pid))process.kill(pid,'SIGKILL');}}
        rmSync(root,{recursive:true,force:true});
    }
});
