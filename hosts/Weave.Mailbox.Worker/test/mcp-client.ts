import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { once } from 'node:events';
import { fileURLToPath } from 'node:url';
export function mcpClient(url,control){
    const env={...process.env,WEAVE_DEMO_URL:url};delete env.WEAVE_MAILBOX_CONTROL;if(control!==undefined)env.WEAVE_MAILBOX_CONTROL=control;
    const child=spawn(process.execPath,[fileURLToPath(new URL('../../../examples/mailbox-demo-mcp/server.ts',import.meta.url))],{env,stdio:['pipe','pipe','pipe']});
    let id=0,stderr='',stdout='';const pending=new Map();child.stderr.on('data',b=>{stderr+=b});child.stdout.on('data',b=>{stdout+=b});
    createInterface({input:child.stdout}).on('line',line=>{const response=JSON.parse(line),wait=pending.get(response.id);if(wait){clearTimeout(wait.timer);pending.delete(response.id);wait.resolve(response);}});
    child.on('exit',()=>{for(const wait of pending.values()){clearTimeout(wait.timer);wait.reject(Error('MCP exited before replying: '+stderr));}pending.clear();});
    function request(method,params={}){return new Promise((resolve,reject)=>{const key=++id,timer=setTimeout(()=>{pending.delete(key);reject(Error('MCP response timed out'));},10000);pending.set(key,{resolve,reject,timer});child.stdin.write(JSON.stringify({jsonrpc:'2.0',id:key,method,params})+'\n');});}
    return {request,call:async(name,args={})=>(await request('tools/call',{name,arguments:args})).result,
        async initialize(){const reply=await request('initialize',{protocolVersion:'2024-11-05',capabilities:{},clientInfo:{name:'weave-local-test',version:'1'}});child.stdin.write(JSON.stringify({jsonrpc:'2.0',method:'notifications/initialized'})+'\n');return reply;},
        get logs(){return stdout+stderr},async close(){if(child.exitCode!==null)return;const done=once(child,'exit');child.stdin.end();const timer=setTimeout(()=>child.kill(),3000);try{await done;}finally{clearTimeout(timer);}}};
}
export function value(result){if(result?.isError)throw Error(result.content[0].text);return JSON.parse(result.content[0].text);}
