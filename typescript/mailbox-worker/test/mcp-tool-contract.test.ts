import assert from 'node:assert/strict';
import {test} from 'node:test';
import {build} from 'esbuild';
import {remoteMcp,type PrivateAccess} from '../src/remote-mcp.ts';
import {start,payload} from './harness.ts';
import type {Env} from '../src/database.ts';
// Evaluate the bundled production module, as the other private MCP tests do.
const built=await build({entryPoints:['src/remote-private.ts'],bundle:true,write:false,platform:'node',format:'esm',logLevel:'silent'});
const {privateMcpTools,privateScope,executePrivateMcp}=await import('data:text/javascript;base64,'+
    Buffer.from(built.outputFiles[0].contents).toString('base64')) as typeof import('../src/remote-private.ts');

type Schema=Record<string,any>;
/** Minimal JSON Schema check for the keywords these contracts use. */
function violations(value:unknown,schema:Schema,path='$'):string[]{
    if(schema.anyOf)return schema.anyOf.some((option:Schema)=>violations(value,option,path).length===0)?[]:[path+' matches no anyOf option'];
    const types=schema.type===undefined?[]:Array.isArray(schema.type)?schema.type:[schema.type];
    const kind=value===null?'null':Array.isArray(value)?'array':Number.isInteger(value)?'integer':typeof value;
    if(types.length&&!types.some((t:string)=>t===kind||(t==='number'&&kind==='integer')))return [path+' is '+kind+', expected '+types.join('|')];
    const errors:string[]=[];
    if(typeof value==='string'){
        if(schema.pattern&&!new RegExp(schema.pattern).test(value))errors.push(path+' does not match '+schema.pattern);
        if(schema.maxLength!==undefined&&value.length>schema.maxLength)errors.push(path+' too long');
        if(schema.enum&&!schema.enum.includes(value))errors.push(path+' not in enum');
    }
    if(typeof value==='number'&&((schema.minimum!==undefined&&value<schema.minimum)||(schema.maximum!==undefined&&value>schema.maximum)))errors.push(path+' out of range');
    if(Array.isArray(value)&&schema.items)value.forEach((item,i)=>errors.push(...violations(item,schema.items,path+'['+i+']')));
    if(kind==='object'){
        const record=value as Record<string,unknown>;
        for(const key of schema.required??[])if(!Object.hasOwn(record,key))errors.push(path+'.'+key+' is required');
        for(const [key,child] of Object.entries(record)){
            if(schema.properties?.[key])errors.push(...violations(child,schema.properties[key],path+'.'+key));
            else if(schema.additionalProperties===false)errors.push(path+'.'+key+' is not allowed');
        }
    }
    return errors;
}
const headers={'Content-Type':'application/json',Accept:'application/json, text/event-stream'};
const origin='https://demo.test';
const call=(name:string,args:Record<string,unknown>={})=>new Request(origin+'/mcp',{method:'POST',
    headers:{...headers,Authorization:'Bearer synthetic'},body:JSON.stringify({jsonrpc:'2.0',id:7,method:'tools/call',params:{name,arguments:args}})});

test('every MCP tool publishes a closed input schema and an object output schema',async()=>{
    const listed=await remoteMcp(new Request(origin+'/mcp',{method:'POST',headers,body:JSON.stringify({jsonrpc:'2.0',id:1,method:'tools/list'})}),
        async()=>new Response('{}'));
    const publicTools=(await listed.json()).result.tools as Schema[];
    assert.deepEqual(publicTools.map(t=>t.name),['demo_guide','search_registry','lookup_address']);
    for(const tool of [...publicTools,...privateMcpTools as Schema[]]){
        assert.equal(tool.inputSchema.type,'object',tool.name);
        assert.equal(tool.inputSchema.additionalProperties,false,tool.name+' must reject unknown arguments');
        for(const key of tool.inputSchema.required)assert.ok(tool.inputSchema.properties[key],tool.name+' requires a declared '+key);
        assert.equal(tool.outputSchema?.type,'object',tool.name+' must declare an output schema');
    }
});

test('real D1 results of every private tool conform to their published output schemas',async()=>{
    const h=await start();
    try{
        const env:Env={db:h.db,REGISTRATION_ENABLED:'true',REGISTRATION_MAX_ACCOUNTS:'100',
            REGISTRATION_PER_IP_PER_HOUR:'10',REGISTRATION_GLOBAL_PER_HOUR:'100',CLEANUP_MAX_MESSAGES:'0',CLEANUP_TIME_BUDGET_MS:'0'};
        const a=await h.participant(),b=await h.participant();
        const seen=new Set<string>();
        const run=async(owner:string,name:string,args:Record<string,unknown>={})=>{
            const access:PrivateAccess={tools:privateMcpTools,scope:privateScope,authorize:async()=>owner,
                invoke:(account,tool,input)=>executePrivateMcp(env,origin,account,tool,input)};
            const frame=await (await remoteMcp(call(name,args),async()=>new Response('{}'),access)).json();
            assert.notEqual(frame.result?.isError,true,name+': '+JSON.stringify(frame.result?.content));
            const schema=(privateMcpTools as Schema[]).find(t=>t.name===name)!.outputSchema;
            assert.deepEqual(violations(frame.result.structuredContent,schema),[],name);
            seen.add(name);
            return frame.result.structuredContent;
        };
        await run(a.accountId,'get_my_profile');
        await run(a.accountId,'ensure_my_address',{addressId:'0199aaaa-0000-4000-8000-000000000001',label:'A'});
        await run(a.accountId,'list_my_addresses');
        const intro=payload('contract-introduction');
        const requested=await run(a.accountId,'request_contact',{requesterAddressId:a.cardId,recipientAccountId:b.accountId,
            recipientAddressId:b.cardId,payload:intro});
        assert.equal(requested.generation,'1','generations are exact decimal strings in structured results');
        await run(b.accountId,'list_my_contact_requests');
        await run(b.accountId,'decide_contact',{requestId:intro.messageId,requesterAccountId:a.accountId,
            expectedGeneration:requested.generation,status:'accepted'});
        await run(a.accountId,'list_my_contacts');
        await run(a.accountId,'get_my_channel',{peerAccountId:b.accountId});
        const message=payload('contract-message');
        await run(a.accountId,'send_message',{recipientAccountId:b.accountId,contactGeneration:requested.generation,payload:message});
        const inbox=await run(b.accountId,'list_my_inbox');
        assert.ok(inbox.items.length>0);
        await run(b.accountId,'ack_message',{messageId:message.messageId,senderAccountId:a.accountId});
        await run(a.accountId,'list_my_outbox');
        assert.deepEqual([...seen].sort(),(privateMcpTools as Schema[]).map(t=>t.name).sort(),'every private tool was exercised');
    }finally{await h.close();}
});

test('public tool results conform to their output schemas and argument errors name the rule',async()=>{
    const page={items:[{addressId:'45d95b98-9559-4f26-96df-f7cb09c4ecfa',accountId:'5596f55b-65df-4970-a599-1bba3a5cd4cf',
        label:'Public',isPublic:true,effectiveFrom:null,effectiveTo:null,directUri:null}],nextCursor:null};
    const read=async(url:URL)=>Response.json(url.pathname==='/v1/directory'?page:page.items[0]);
    const listed=await (await remoteMcp(new Request(origin+'/mcp',{method:'POST',headers,
        body:JSON.stringify({jsonrpc:'2.0',id:1,method:'tools/list'})}),read)).json();
    for(const [name,args] of [['demo_guide',{}],['search_registry',{limit:1}],['lookup_address',{addressId:page.items[0].addressId}]] as const){
        const frame=await (await remoteMcp(new Request(origin+'/mcp',{method:'POST',headers,
            body:JSON.stringify({jsonrpc:'2.0',id:2,method:'tools/call',params:{name,arguments:args}})}),read)).json();
        const schema=listed.result.tools.find((t:Schema)=>t.name===name).outputSchema;
        assert.deepEqual(violations(frame.result.structuredContent,schema),[],name);
    }
    const tooMany=await (await remoteMcp(new Request(origin+'/mcp',{method:'POST',headers,
        body:JSON.stringify({jsonrpc:'2.0',id:3,method:'tools/call',params:{name:'search_registry',arguments:{limit:100}}})}),read)).json();
    assert.equal(tooMany.error.code,-32602);
    assert.match(tooMany.error.message,/limit: must be an integer from 1 to 10/);
});

test('private paging limit errors name the field instead of a bare 400',async()=>{
    const h=await start();
    try{
        const env:Env={db:h.db,REGISTRATION_ENABLED:'true',REGISTRATION_MAX_ACCOUNTS:'100',
            REGISTRATION_PER_IP_PER_HOUR:'10',REGISTRATION_GLOBAL_PER_HOUR:'100',CLEANUP_MAX_MESSAGES:'0',CLEANUP_TIME_BUDGET_MS:'0'};
        const a=await h.participant();
        const response=await executePrivateMcp(env,origin,a.accountId,'list_my_inbox',{limit:100});
        assert.equal(response.status,400);
        assert.deepEqual(await response.json(),{status:400,title:'Invalid request',code:'invalid',
            field:'limit',reason:'must be an integer from 1 to 10'});
    }finally{await h.close();}
});
