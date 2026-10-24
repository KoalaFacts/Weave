import {fileURLToPath} from 'node:url';
import {join,resolve} from 'node:path';
import {Endpoint} from './endpoint.ts';
import {connect} from './mcp.ts';
import {resolveControl} from './vault.ts';
import {generate} from './model.ts';
import {capture,load,PairError,object,validateOrigin} from './runtime.ts';

async function serve():Promise<void>{
    const directory=resolve(process.argv[2]),config=object(load(join(directory,'agent.json')));
    validateOrigin(config.origin);
    if(!['A','B'].includes(config.role))throw new PairError('invalid_agent_role');
    const abort=new AbortController();
    const endpoint=new Endpoint(config.role,config.runId,directory,async()=>connect(await resolveControl(config.reference,
        args=>capture('op',args,{timeoutMs:30000,signal:abort.signal})),abort.signal),
        (sequence,history)=>generate(config.model,config.role,sequence,history,directory,abort.signal));
    let busy=false,stopping=false;
    let active:Promise<void>|undefined;
    const stop=async()=>{if(stopping)return;stopping=true;abort.abort();await Promise.allSettled([active,endpoint.close()]);process.exit(0);};
    process.on('SIGTERM',()=>void stop());process.on('SIGINT',()=>void stop());process.on('disconnect',()=>void stop());
    const reply=(message:unknown)=>{if(process.connected)process.send?.(message);};
    process.on('message',raw=>{
        const message=object(raw);
        if(busy||stopping){reply({id:message.id,error:'agent_busy'});return;}
        busy=true;
        active=(async()=>{
        try{
            let result:unknown={};
            switch(message.command){
            case 'probe':{const evidence=await generate(config.model,config.role,0,[],directory,abort.signal);result={threadId:evidence.threadId};break;}
            case 'setup':result=await endpoint.setup();break;
            case 'reconnect':await endpoint.reconnect();break;
            case 'peer':endpoint.peer(message.value);break;
            case 'introduce':await endpoint.introduce();break;
            case 'accept':await endpoint.accept();break;
            case 'step':await endpoint.step(message.value);break;
            case 'consumeFinal':await endpoint.consumeFinal();break;
            case 'evidence':result=await endpoint.evidence();break;
            default:throw new PairError('unknown_agent_command');
            }
            reply({id:message.id,result});
        }catch(error){reply({id:message.id,error:error instanceof PairError?error.message:'agent_failed'});}
        finally{busy=false;}
        })();
    });
}
if(process.argv[1]&&resolve(process.argv[1])===fileURLToPath(import.meta.url))serve().catch(()=>{process.stderr.write('agent_start_failed\n');process.exitCode=1;});
