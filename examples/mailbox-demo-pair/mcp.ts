import {spawn,type ChildProcessWithoutNullStreams} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import {PairError,parseWire,object,modelEnvironment,nodeFlags,ORIGIN} from './runtime.ts';

export interface Client {call(name:string,args?:Record<string,unknown>):Promise<any>;close():Promise<void>}
export class StdioClient implements Client {
    private child:ChildProcessWithoutNullStreams;
    private next=0;
    private buffer='';
    private pending=new Map<number,{resolve:(v:any)=>void;reject:(e:Error)=>void;timer:NodeJS.Timeout}>();
    private dead=false;
    private closed:Promise<void>;
    private timeoutMs:number;
    constructor(command:string,args:string[],env:NodeJS.ProcessEnv,timeoutMs=20000){
        this.timeoutMs=timeoutMs;
        this.child=spawn(command,args,{env,shell:false,stdio:['pipe','pipe','pipe']});
        this.closed=new Promise(resolve=>this.child.on('close',()=>{this.fail();resolve();}));
        this.child.on('error',()=>this.fail());this.child.stdin.on('error',()=>this.fail());
        let stderrBytes=0;
        this.child.stderr.on('data',(b:Buffer)=>{stderrBytes+=b.length;if(stderrBytes>1048576)this.fail();});
        this.child.stdout.setEncoding('utf8');
        this.child.stdout.on('data',(s:string)=>{
            this.buffer+=s;
            if(Buffer.byteLength(this.buffer)>1048576){this.fail();return;}
            let at:number;
            while((at=this.buffer.indexOf('\n'))>=0){const line=this.buffer.slice(0,at);this.buffer=this.buffer.slice(at+1);
                try{
                    const message=object(parseWire(line));
                    if(message.jsonrpc!=='2.0')throw Error();
                    if(message.id===undefined)continue;
                    const pending=this.pending.get(message.id);if(!pending)throw Error();
                    clearTimeout(pending.timer);this.pending.delete(message.id);
                    if(message.error)pending.reject(new PairError('mcp_protocol_error'));else pending.resolve(message.result);
                }catch{this.fail();}
            }
        });
    }
    private fail():void {
        if(this.dead)return;this.dead=true;
        for(const p of this.pending.values()){clearTimeout(p.timer);p.reject(new PairError('mcp_transport_failed'));}this.pending.clear();
        this.child.kill('SIGTERM');const kill=setTimeout(()=>this.child.kill('SIGKILL'),500);this.closed?.finally(()=>clearTimeout(kill));
    }
    async request(method:string,params:unknown={}):Promise<any>{
        if(this.dead)throw new PairError('mcp_transport_closed');const id=++this.next;
        const result=new Promise((resolve,reject)=>{const timer=setTimeout(()=>this.fail(),this.timeoutMs);this.pending.set(id,{resolve,reject,timer});});
        this.child.stdin.write(JSON.stringify({jsonrpc:'2.0',id,method,params})+'\n');return await result;
    }
    async initialize():Promise<void>{
        const result=await this.request('initialize',{protocolVersion:'2024-11-05',capabilities:{},clientInfo:{name:'weave-live-pair',version:'0.1.0'}});
        if(result.protocolVersion!=='2024-11-05')throw new PairError('unsupported_mcp_version');
        this.child.stdin.write(JSON.stringify({jsonrpc:'2.0',method:'notifications/initialized'})+'\n');
    }
    async call(name:string,args:Record<string,unknown>={}):Promise<any>{
        const result=object(await this.request('tools/call',{name,arguments:args}));
        if(result.isError||!Array.isArray(result.content)||result.content.length!==1||result.content[0].type!=='text')throw new PairError('mcp_tool_failed');
        try{return parseWire(result.content[0].text);}catch{throw new PairError('invalid_mcp_result');}
    }
    async close():Promise<void>{this.fail();await this.closed;}
}
export async function connect(control:string):Promise<Client>{
    const env=modelEnvironment();delete env.CODEX_HOME;
    const client=new StdioClient(process.execPath,[...nodeFlags(),fileURLToPath(new URL('../mailbox-demo-mcp/server.ts',import.meta.url))],{...env,WEAVE_DEMO_URL:ORIGIN,WEAVE_ACCOUNT_CONTROL:control});
    try{await client.initialize();const guide=await client.call('demo_guide');if(guide.origin!==ORIGIN||guide.readiness?.credentialConfigured!==true)throw new PairError('mcp_not_ready');return client;}
    catch(error){await client.close();throw error;}
}
export async function pages(client:Client,name:string):Promise<any[]> {
    const items:any[]=[],seen=new Set<string>();let cursor:string|undefined;
    for(let page=0;page<20;page++){
        const result=object(await client.call(name,{limit:10,...(cursor?{afterCursor:cursor}:{})}));
        if(!Array.isArray(result.items)||result.items.length>10)throw new PairError('invalid_page');items.push(...result.items);
        if(result.nextCursor===null)return items;
        if(typeof result.nextCursor!=='string'||!result.nextCursor||result.nextCursor.length>2048||seen.has(result.nextCursor))throw new PairError('invalid_cursor');
        cursor=result.nextCursor;seen.add(cursor);
    }
    throw new PairError('pagination_incomplete');
}
