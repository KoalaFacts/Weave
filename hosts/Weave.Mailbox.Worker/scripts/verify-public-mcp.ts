import {resolve} from 'node:path';
import {pathToFileURL} from 'node:url';

const endpoint='https://demo.weave-389.workers.dev/mcp';
const protocolVersion='2025-11-25';
const maxResponseBytes=256*1024;
type ObjectValue=Record<string,unknown>;
const object=(value:unknown):value is ObjectValue=>value!==null&&typeof value==='object'&&!Array.isArray(value);

async function boundedResponse(response:Response):Promise<unknown>{
    if(!response.body)throw Error('Published MCP response has no body.');
    const reader=response.body.getReader(),decoder=new TextDecoder('utf-8',{fatal:true});
    let text='',size=0;
    try{
        while(true){
            const {done,value}=await reader.read();
            if(done)break;
            size+=value.byteLength;
            if(size>maxResponseBytes)throw Error('Published MCP response exceeds size limit.');
            text+=decoder.decode(value,{stream:true});
        }
        text+=decoder.decode();
    }finally{
        await reader.cancel().catch(()=>{});
        reader.releaseLock();
    }
    try{return JSON.parse(text) as unknown;}catch{throw Error('Published MCP response is invalid JSON.');}
}

export async function verifyPublicMcp({fetchImpl=fetch}:{fetchImpl?:typeof fetch}={}){
    async function rpc(id:number,method:string,params:Record<string,unknown>={}):Promise<ObjectValue>{
        let response:Response;
        try{
            response=await fetchImpl(endpoint,{method:'POST',redirect:'error',cache:'no-store',signal:AbortSignal.timeout(15000),
                headers:{'Content-Type':'application/json',Accept:'application/json, text/event-stream','MCP-Protocol-Version':protocolVersion},
                body:JSON.stringify({jsonrpc:'2.0',id,method,params})});
        }catch{throw Error('Published MCP transport is unavailable.');}
        if(response.status!==200||!/^application\/json(?:;|$)/i.test(response.headers.get('content-type')??'')
            ||response.headers.get('cache-control')!=='no-store'||response.headers.get('x-content-type-options')!=='nosniff'){
            await response.body?.cancel();
            throw Error('Published MCP response failed protocol or cache validation.');
        }
        const data=await boundedResponse(response);
        if(!object(data)||data.jsonrpc!=='2.0'||data.id!==id||!object(data.result)||Object.hasOwn(data,'error')){
            throw Error('Published MCP JSON-RPC result is invalid.');
        }
        return data.result;
    }
    const init=await rpc(1,'initialize',{protocolVersion,capabilities:{},clientInfo:{name:'weave-release-verifier',version:'1.0.0'}});
    if(init.protocolVersion!==protocolVersion||!object(init.serverInfo)||init.serverInfo.name!=='weave-public'){
        throw Error('Published MCP initialize returned an unexpected protocol or server.');
    }
    const list=await rpc(2,'tools/list');
    if(!Array.isArray(list.tools))throw Error('Published MCP tool discovery is invalid.');
    for(const name of ['demo_guide','search_registry','lookup_address']){
        const matches=list.tools.filter((item:unknown):item is ObjectValue=>object(item)&&item.name===name);
        const item=matches[0];
        if(matches.length!==1||!item||!object(item.annotations)||item.annotations.readOnlyHint!==true
            ||!Array.isArray(item.securitySchemes)||item.securitySchemes.length!==1
            ||!object(item.securitySchemes[0])||item.securitySchemes[0].type!=='noauth'){
            throw Error('Published MCP public tool is missing or misconfigured.');
        }
    }
    const page=await rpc(3,'tools/call',{name:'search_registry',arguments:{limit:1}});
    if(page.isError===true||!Array.isArray(page.content)||page.content.length!==1||!object(page.content[0])
        ||page.content[0].type!=='text'||typeof page.content[0].text!=='string'){
        throw Error('Published MCP public directory call failed.');
    }
    let directory:unknown;
    try{directory=JSON.parse(page.content[0].text);}catch{throw Error('Published MCP directory returned invalid JSON.');}
    if(!object(directory)||!Array.isArray(directory.items)
        ||(directory.nextCursor!==null&&typeof directory.nextCursor!=='string')){
        throw Error('Published MCP directory returned an invalid page.');
    }
    return {endpoint,protocolVersion,publicTools:3,directoryRead:true,readOnly:true};
}

if(process.argv[1]&&import.meta.url===pathToFileURL(resolve(process.argv[1])).href){
    try{console.log('Verified published public MCP:',JSON.stringify(await verifyPublicMcp()));}
    catch(error){console.error((error as Error).message);process.exitCode=1;}
}
