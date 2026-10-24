import {fork,type ChildProcess} from 'node:child_process';
import {existsSync,mkdirSync,openSync,closeSync,unlinkSync} from 'node:fs';
import {join,resolve} from 'node:path';
import {fileURLToPath} from 'node:url';
import {randomUUID} from 'node:crypto';
import {allocate,validateReference} from './vault.ts';
import {verifyEvidence} from './endpoint.ts';
import {modelArguments} from './model.ts';
import {ORIGIN,PairError,capture,load,save,object,identity,privateDirectory,outsideCheckout,modelEnvironment,nodeFlags,validateOrigin} from './runtime.ts';
const root=fileURLToPath(new URL('../../',import.meta.url));
export type Diagnosis={ready:boolean;missing:string[]};
type DoctorOptions={version?:string;platform?:string;execute?:(command:string,args:string[])=>Promise<string>;publicRead?:()=>Promise<unknown>;sourceAvailable?:boolean};
async function publicRead():Promise<unknown>{
    const response=await fetch(ORIGIN+'/v1/directory?limit=1',{redirect:'error',signal:AbortSignal.timeout(10000),headers:{Accept:'application/json'}});
    if(!response.ok||!response.headers.get('content-type')?.startsWith('application/json'))throw new PairError('demo_connectivity');
    let size=0;const chunks:Uint8Array[]=[];
    if(!response.body)throw new PairError('demo_connectivity');
    for await(const chunk of response.body){size+=chunk.length;if(size>65536)throw new PairError('demo_response_too_large');chunks.push(chunk);}
    return JSON.parse(Buffer.concat(chunks).toString('utf8'));
}
export async function doctor(vault?:string,options:DoctorOptions={}):Promise<Diagnosis>{
    const missing:string[]=[],[major,minor]=(options.version??process.versions.node).split('.').map(Number);
    if(!(major>24||major===24&&minor>=19))missing.push('node_24_19_or_newer');
    if((options.platform??process.platform)==='win32')missing.push('posix_or_wsl_private_storage');
    const execute=options.execute??((command,args)=>capture(command,args,{timeoutMs:15000,...(command==='codex'?{env:modelEnvironment()}:{})}));
    try{await execute('codex',['login','status']);}catch{missing.push('codex_login');}
    try{const servers=JSON.parse(await execute('codex',['mcp','list','--json']));if(!Array.isArray(servers)||servers.length)missing.push('clean_codex_profile');}catch{missing.push('codex_profile_check');}
    try{await execute('op',['whoami','--format=json']);if(vault)await execute('op',['vault','get',vault,'--format=json']);}catch{missing.push('secure_vault');}
    try{const body=object(await (options.publicRead??publicRead)());if(!Array.isArray(body.items)||body.items.length>1||!('nextCursor' in body))throw Error();}catch{missing.push('demo_connectivity');}
    if(!(options.sourceAvailable??(['server.ts','tools.ts'].every(f=>existsSync(join(root,'examples/mailbox-demo-mcp',f)))&&existsSync(join(root,'hosts/Weave.Mailbox.Worker/src/agent-start.md')))))missing.push('mcp_source_missing');
    return {ready:missing.length===0,missing};
}
export interface Agent {call(command:string,value?:unknown):Promise<any>}
class AgentProcess implements Agent {
    private child:ChildProcess;private count=0;private dead=false;private pending=new Map<number,{resolve:(v:any)=>void;reject:(e:Error)=>void;timer:NodeJS.Timeout}>();private closed:Promise<void>;private stopping?:Promise<void>;
    constructor(directory:string){
        this.child=fork(fileURLToPath(new URL('./worker.ts',import.meta.url)),[directory],{execArgv:nodeFlags(),cwd:directory,detached:true,stdio:['ignore','pipe','pipe','ipc']});
        this.closed=new Promise(resolve=>this.child.on('close',()=>{this.fail();resolve();}));
        this.child.on('error',()=>this.fail());
        let bytes=0;for(const stream of [this.child.stdout,this.child.stderr])stream?.on('data',(b:Buffer)=>{bytes+=b.length;if(bytes>1048576)this.fail();});
        this.child.on('message',raw=>{try{const message=object(raw),p=this.pending.get(message.id);if(!p)throw Error();clearTimeout(p.timer);this.pending.delete(message.id);
            if(message.error)p.reject(new PairError(/^[a-z0-9_]{1,80}$/.test(message.error)?message.error:'agent_failed'));else p.resolve(message.result);
        }catch{this.fail();}});
    }
    private fail():void{this.dead=true;for(const p of this.pending.values()){clearTimeout(p.timer);p.reject(new PairError('agent_process_failed'));}this.pending.clear();}
    async call(command:string,value?:unknown):Promise<any>{
        if(this.dead)throw new PairError('agent_process_closed');const id=++this.count;
        return await new Promise((resolve,reject)=>{const timer=setTimeout(()=>{this.fail();void this.stop();},150000);this.pending.set(id,{resolve,reject,timer});this.child.send({id,command,value},error=>{if(error)this.fail();});});
    }
    stop():Promise<void>{return this.stopping??=this.stopGroup();}
    private async stopGroup():Promise<void>{
        this.fail();const kill=(signal:NodeJS.Signals)=>{if(this.child.pid)try{process.kill(-this.child.pid,signal);}catch{/* already exited */}};
        kill('SIGTERM');
        // Worker exit does not prove that descendants in its process group have exited.
        const terminated=new Promise<void>(resolve=>setTimeout(()=>{kill('SIGKILL');resolve();},1000));
        await Promise.all([this.closed,terminated]);
    }
}
export async function exchange(a:Agent,b:Agent):Promise<ReturnType<typeof verifyEvidence>>{
    // Probe both real model runtimes before either endpoint creates a demo account.
    await Promise.all([a.call('probe'),b.call('probe')]);
    const [ai,bi]=await Promise.all([a.call('setup'),b.call('setup')]);
    if(identity(ai.accountId)===identity(bi.accountId))throw new PairError('accounts_not_distinct');
    const invitation=(v:any)=>({accountId:identity(v.accountId),addressId:identity(v.addressId)});
    await a.call('peer',invitation(bi));await b.call('peer',invitation(ai));
    await a.call('introduce');await b.call('accept');
    for(let sequence=1;sequence<=6;sequence++){
        await (sequence%2?a:b).call('step',sequence);
        // Keep the second reply unacknowledged while both endpoints reopen their own MCP bindings.
        if(sequence===2)await Promise.all([a.call('reconnect'),b.call('reconnect')]);
    }
    await a.call('consumeFinal');return verifyEvidence(await a.call('evidence'),await b.call('evidence'));
}
function flags(args:string[]):Record<string,string|boolean>{
    const result:Record<string,string|boolean>={};const known=['state','vault','model','allow-vault-writes','allow-demo-writes'];
    for(let i=0;i<args.length;i++){
        const key=args[i].startsWith('--')?args[i].slice(2):'';
        if(!known.includes(key)||key in result)throw new PairError('invalid_option');
        if(key.startsWith('allow-'))result[key]=true;
        else{const value=args[++i];if(!value||value.startsWith('--'))throw new PairError('missing_option_value');result[key]=value;}
    }
    return result;
}
export function claimAttempt(state:string,runId:string):()=>void {
    privateDirectory(state);identity(runId);
    let fd:number;try{fd=openSync(join(state,'run.lock'),'wx',0o600);}catch{throw new PairError('run_locked');}
    let released=false;const release=()=>{if(!released){released=true;closeSync(fd);unlinkSync(join(state,'run.lock'));}};
    try{if(existsSync(join(state,'attempt.json')))throw new PairError('existing_attempt_requires_reconciliation');save(join(state,'attempt.json'),{status:'started',runId});}
    catch(error){release();throw error;}
    return release;
}
export async function main(args:string[]):Promise<number>{
    const command=args[0];if(!['doctor','init','run'].includes(command)){console.log('Usage: node examples/mailbox-demo-pair/run.ts doctor | init --state PATH --vault VAULT_ID --model MODEL --allow-vault-writes | run --state PATH --allow-demo-writes');return 2;}
    const f=flags(args.slice(1));
    if(command==='doctor'){const result=await doctor(typeof f.vault==='string'?f.vault:undefined);console.log(JSON.stringify({status:result.ready?'ready':'blocked',...result,remoteWrites:false}));return result.ready?0:2;}
    if(typeof f.state!=='string')throw new PairError('state_required');const state=resolve(f.state);outsideCheckout(state,root);
    if(command==='init'){
        if(f['allow-vault-writes']!==true)throw new PairError('vault_write_authorization_required');
        if(typeof f.vault!=='string'||!/^[a-z0-9]{26}$/.test(f.vault)||typeof f.model!=='string')throw new PairError('vault_and_model_required');modelArguments(f.model);
        const diagnosis=await doctor(f.vault);if(!diagnosis.ready){console.log(JSON.stringify({status:'blocked',...diagnosis,remoteWrites:false}));return 2;}
        if(!existsSync(state))privateDirectory(state,true);else privateDirectory(state);
        const lock=openSync(join(state,'init.lock'),'wx',0o600);
        try{
            const intentPath=join(state,'init.json');let intent:any;
            if(existsSync(intentPath)){intent=object(load(intentPath));if(intent.vault!==f.vault||intent.model!==f.model||intent.origin!==ORIGIN)throw new PairError('initialization_config_changed');identity(intent.runId);}
            else{intent={vault:f.vault,model:f.model,runId:randomUUID(),origin:ORIGIN};save(intentPath,intent);}
            const refs={A:await allocate(state,f.vault,'A'),B:await allocate(state,f.vault,'B')};
            if(refs.A===refs.B)throw new PairError('vault_references_not_distinct');
            for(const role of ['A','B'] as const){const dir=join(state,role);if(!existsSync(dir))mkdirSync(dir,{mode:0o700});privateDirectory(dir);
                const path=join(dir,'agent.json');const agent={role,model:f.model,runId:intent.runId,origin:ORIGIN,reference:refs[role]};
                if(existsSync(path)&&JSON.stringify(load(path))!==JSON.stringify(agent))throw new PairError('agent_binding_changed');save(path,agent);}
            save(join(state,'pair.json'),{version:1,runId:intent.runId,vault:f.vault,model:f.model,origin:ORIGIN});
        }finally{closeSync(lock);unlinkSync(join(state,'init.lock'));}
        console.log(JSON.stringify({status:'vaults_ready',remoteAccountsCreated:false}));return 0;
    }
    if(f['allow-demo-writes']!==true)throw new PairError('demo_write_authorization_required');privateDirectory(state);
    const config=object(load(join(state,'pair.json')));validateOrigin(config.origin);identity(config.runId);modelArguments(config.model);
    const references=new Set<string>();
    for(const role of ['A','B']){const agent=object(load(join(state,role,'agent.json')));if(agent.runId!==config.runId||agent.origin!==ORIGIN||agent.model!==config.model||agent.role!==role)throw new PairError('agent_binding_mismatch');if(validateReference(agent.reference).vault!==config.vault)throw new PairError('vault_binding_mismatch');references.add(agent.reference);const binding=load(join(state,`vault-${role}.json`));if(binding.status!=='saved'||binding.vault!==config.vault||binding.origin!==ORIGIN||binding.reference!==agent.reference)throw new PairError('vault_binding_mismatch');}
    if(references.size!==2)throw new PairError('vault_references_not_distinct');
    const diagnosis=await doctor(config.vault);if(!diagnosis.ready){console.log(JSON.stringify({status:'blocked',...diagnosis,remoteWrites:false}));return 2;}
    const release=claimAttempt(state,config.runId);let a:AgentProcess|undefined,b:AgentProcess|undefined;
    const interrupt=()=>{void Promise.all([a?.stop(),b?.stop()]);};
    process.on('SIGINT',interrupt);process.on('SIGTERM',interrupt);
    try{
        a=new AgentProcess(join(state,'A'));b=new AgentProcess(join(state,'B'));
        const result=await exchange(a,b);
        save(join(state,'result.json'),{status:'completed',kind:'model-driven-endpoints',nativeChatGPTSubagents:false,origin:ORIGIN,runId:config.runId,requestedModel:config.model,conversationModelCalls:6,preflightModelCalls:2,...result});
        console.log(JSON.stringify({status:'completed',messages:result.messages.length,evidence:join(state,'result.json')}));return 0;
    }catch(error){save(join(state,'result.json'),{status:'not_completed',runId:config.runId,reason:error instanceof PairError?error.message:'test_failed',remoteWritesMayHaveOccurred:true});throw error;}
    finally{await Promise.all([a?.stop(),b?.stop()]);process.removeListener('SIGINT',interrupt);process.removeListener('SIGTERM',interrupt);release();}
}
if(process.argv[1]&&resolve(process.argv[1])===fileURLToPath(import.meta.url))main(process.argv.slice(2)).then(code=>{process.exitCode=code;}).catch(error=>{console.error(JSON.stringify({status:'not_completed',reason:error instanceof PairError?error.message:'test_failed'}));process.exitCode=1;});
