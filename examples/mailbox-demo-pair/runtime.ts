import { spawn } from 'node:child_process';
import { openSync, closeSync, writeFileSync, fsyncSync, renameSync, lstatSync, readFileSync, mkdirSync } from 'node:fs';
import { dirname, resolve, sep, join } from 'node:path';
import { randomUUID } from 'node:crypto';

export const ORIGIN = 'https://demo.weave-389.workers.dev';
export class PairError extends Error { constructor(code:string) { super(code); this.name='PairError'; } }
export function object(value:unknown):Record<string,any> {
    if (!value || typeof value!=='object' || Array.isArray(value)) throw new PairError('invalid_response');
    return value as Record<string,any>;
}
export function identity(value:unknown):string {
    if(typeof value!=='string'||! /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(value)) throw new PairError('invalid_identity');
    return value;
}
export function validateOrigin(value:string):string {
    if(value!==ORIGIN) throw new PairError('unapproved_origin');
    return value;
}
export function parseGeneration(value:unknown):string {
    if(typeof value==='number'&&!Number.isSafeInteger(value)) throw new PairError('lossy_generation');
    const text=String(value);
    if(!/^[1-9][0-9]{0,18}$/.test(text)||BigInt(text)>9223372036854775807n) throw new PairError('invalid_generation');
    return text;
}
export function parseWire(text:string):any {
    // The existing server preserves int64 source tokens. Preserve them on this side too.
    return JSON.parse(text, (_key:string,value:unknown,context?:{source?:string})=>{
        if(typeof value==='number'&&Number.isInteger(value)&&!Number.isSafeInteger(value)){
            if(!context?.source||! /^-?\d+$/.test(context.source))throw new PairError('lossy_wire_integer');
            return context.source;
        }
        return value;
    });
}
export function modelEnvironment(source:NodeJS.ProcessEnv=process.env):NodeJS.ProcessEnv {
    const env:NodeJS.ProcessEnv={};
    for(const key of ['PATH','HOME','USERPROFILE','SystemRoot','TMPDIR','TEMP','LANG','LC_ALL','CODEX_HOME']) if(source[key]!==undefined)env[key]=source[key];
    return env;
}
export const nodeFlags = () => process.execArgv.filter(arg=>arg==='--experimental-strip-types');
export async function capture(command:string,args:string[],options:{input?:string;env?:NodeJS.ProcessEnv;cwd?:string;timeoutMs?:number;maxBytes?:number;signal?:AbortSignal}={}):Promise<string> {
    if(options.signal?.aborted)throw new PairError('process_cancelled');
    return await new Promise((resolve,reject)=>{
        const child=spawn(command,args,{shell:false,stdio:['pipe','pipe','pipe'],env:options.env??process.env,cwd:options.cwd});
        let output='',bytes=0,failure:string|undefined,killTimer:NodeJS.Timeout|undefined;
        const fail=(code:string)=>{failure??=code;child.kill('SIGTERM');killTimer??=setTimeout(()=>child.kill('SIGKILL'),500);};
        const timer=setTimeout(()=>fail('process_timeout'),options.timeoutMs??15000);
        const abort=()=>fail('process_cancelled');options.signal?.addEventListener('abort',abort,{once:true});
        const cleanup=()=>{clearTimeout(timer);if(killTimer)clearTimeout(killTimer);options.signal?.removeEventListener('abort',abort);};
        child.stdout.setEncoding('utf8');
        child.stdout.on('data',(chunk:string)=>{bytes+=Buffer.byteLength(chunk);if(bytes>(options.maxBytes??1048576))fail('process_output_limit');else output+=chunk;});
        // Drain, bound and discard stderr; it may contain provider/secret-manager values.
        child.stderr.on('data',(chunk:Buffer)=>{bytes+=chunk.length;if(bytes>(options.maxBytes??1048576))fail('process_output_limit');});
        child.stdin.on('error',()=>{});
        child.on('error',()=>{cleanup();reject(new PairError('process_unavailable'));});
        child.on('close',code=>{cleanup();if(failure||code!==0)reject(new PairError(failure??'process_failed'));else resolve(output);});
        if(options.signal?.aborted)abort();
        child.stdin.end(options.input??'');
    });
}
export function privateDirectory(path:string,create=false):string {
    if(process.platform==='win32')throw new PairError('use_posix_private_storage');
    const target=resolve(path);
    if(create)mkdirSync(target,{recursive:false,mode:0o700});
    let current=target;
    for(;;){if(lstatSync(current).isSymbolicLink())throw new PairError('symlink_state_path');const parent=dirname(current);if(parent===current)break;current=parent;}
    const st=lstatSync(target);
    if(!st.isDirectory()||(st.mode&0o077)!==0||st.uid!==process.getuid!())throw new PairError('state_not_owner_private');
    return target;
}
export function save(path:string,value:unknown):void {
    privateDirectory(dirname(path));
    try{if(!lstatSync(path).isFile()||lstatSync(path).isSymbolicLink())throw new PairError('invalid_state_file');}catch(e){if((e as NodeJS.ErrnoException).code!=='ENOENT')throw e;}
    const tmp=join(dirname(path),'.write-'+randomUUID()),fd=openSync(tmp,'wx',0o600);
    try{writeFileSync(fd,JSON.stringify(value,null,2)+'\n');fsyncSync(fd);}finally{closeSync(fd);}
    renameSync(tmp,path);const dir=openSync(dirname(path),'r');try{fsyncSync(dir);}finally{closeSync(dir);}
}
export function load(path:string):any {
    privateDirectory(dirname(path));const st=lstatSync(path);
    if(!st.isFile()||st.isSymbolicLink()||st.size>1048576||(st.mode&0o077)!==0)throw new PairError('invalid_state_file');
    try{return JSON.parse(readFileSync(path,'utf8'));}catch{throw new PairError('invalid_state_json');}
}
export function outsideCheckout(state:string,root:string):void {
    const path=resolve(state),repo=resolve(root);
    if(path===repo||path.startsWith(repo+sep))throw new PairError('state_must_be_outside_checkout');
}
