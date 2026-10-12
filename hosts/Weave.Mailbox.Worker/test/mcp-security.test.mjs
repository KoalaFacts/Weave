import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createServer } from 'node:http';
import { once } from 'node:events';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { mcpClient, value } from './mcp-client.mjs';
import { start, secret, payload } from './harness.mjs';
async function httpServer(handler){const server=createServer(handler);server.listen(0,'127.0.0.1');await once(server,'listening');return {url:'http://127.0.0.1:'+server.address().port,async close(){server.closeAllConnections();await new Promise(resolve=>server.close(resolve));}};}
test('MCP fixed origin, exact int64 and public credential omission survive the real HTTP boundary',async()=>{
    const control=secret(),requests=[];
    const server=await httpServer(async(req,res)=>{let body='';for await(const chunk of req)body+=chunk;requests.push({url:req.url,control:req.headers['x-weave-mailbox-control'],body});res.setHeader('Content-Type','application/json');res.end('{"generation":9007199254740993,"reflected":'+JSON.stringify(control)+','+JSON.stringify(control)+':"secret key"}');}),client=mcpClient(server.url,control);
    try{
        await client.initialize();const found=await client.call('search_registry');assert.ok(found.content[0].text.includes('9007199254740993'));assert.ok(!found.content[0].text.includes(control));assert.equal(requests[0].control,undefined);
        await client.call('send_message',{recipientMailboxId:'recipient',contactGeneration:'9007199254740993',payload:payload('exact')});assert.ok(requests[1].body.includes('"contactGeneration":9007199254740993'));assert.equal(requests[1].control,control);
        for(const args of [{q:'x',control},{q:'x',url:'https://other.test'},{limit:11},{q:'x'.repeat(129)}])assert.equal((await client.call('search_registry',args)).isError,true);
        assert.equal((await client.call('send_message',{recipientMailboxId:'recipient',contactGeneration:'9223372036854775808',payload:payload()})).isError,true);
        assert.equal(requests.length,2);assert.ok(!client.logs.includes(control));
    }finally{await client.close();await server.close();}
});
test('MCP does not follow redirects, retry unknown writes or expose upstream error bodies',async()=>{
    let mode='redirect',calls=0,forwarded=0;const control=secret();
    const target=await httpServer((_q,r)=>{forwarded++;r.end('unexpected');});
    const server=await httpServer((req,res)=>{calls++;req.resume();if(mode==='redirect'){res.writeHead(302,{Location:target.url});res.end();}else if(mode==='disconnect')req.socket.destroy();else{res.writeHead(500,{'Content-Type':'application/json'});res.end(JSON.stringify({stack:'PRIVATE SQL details',control}));}}),client=mcpClient(server.url,control);
    try{await client.initialize();for(mode of ['redirect','disconnect','failure']){const result=await client.call('register_account');assert.equal(result.isError,true);assert.ok(!result.content[0].text.includes(control));assert.ok(!result.content[0].text.includes('PRIVATE'));}assert.equal(calls,3);assert.equal(forwarded,0);}finally{await client.close();await server.close();await target.close();}
});
test('MCP authentication accepts long controls and rejects wrong controls and foreign object parameters',async()=>{
    const h=await start(),control='synthetic-control-'.repeat(40),a=mcpClient(String(await h.mf.ready),control),wrong=mcpClient(String(await h.mf.ready),secret());
    try{await a.initialize();await wrong.initialize();const account=value(await a.call('register_account'));assert.ok(account.agentId);assert.equal((await wrong.call('list_addresses')).isError,true);for(const name of ['read_inbox','read_outbox','list_addresses'])assert.equal((await a.call(name,{accountId:'another-account'})).isError,true);assert.ok(!a.logs.includes(control));}finally{await a.close();await wrong.close();await h.close();}
});
test('MCP malformed and oversized frames are bounded and stdio stays JSON-RPC only',()=>{
    const env={...process.env,WEAVE_DEMO_URL:'http://127.0.0.1:1'};delete env.WEAVE_MAILBOX_CONTROL;
    const child=spawnSync(process.execPath,[fileURLToPath(new URL('../../../examples/mailbox-demo-mcp/server.mjs',import.meta.url))],{env,encoding:'utf8',input:'not-json\n'+'x'.repeat(262145)+'\n'+JSON.stringify({jsonrpc:'2.0',id:7,method:'ping'})+'\n',timeout:5000});
    assert.equal(child.status,0);assert.equal(child.stderr,'');const replies=child.stdout.trim().split('\n').map(JSON.parse);assert.equal(replies[0].error.code,-32700);assert.equal(replies[1].error.code,-32600);assert.deepEqual(replies[2],{jsonrpc:'2.0',id:7,result:{}});
});
test('MCP waits for slow stdout consumers instead of dispatching unbounded large responses',async()=>{
    const {spawn}=await import('node:child_process');let calls=0,first,all;
    const firstCall=new Promise(resolve=>{first=resolve}),allCalls=new Promise(resolve=>{all=resolve});
    const server=await httpServer((req,res)=>{req.resume();calls++;first();if(calls===20)all();res.setHeader('Content-Type','application/json');res.end(JSON.stringify({data:'x'.repeat(300000)}));});
    const child=spawn(process.execPath,[fileURLToPath(new URL('../../../examples/mailbox-demo-mcp/server.mjs',import.meta.url))],{env:{...process.env,WEAVE_DEMO_URL:server.url,WEAVE_MAILBOX_CONTROL:secret()},stdio:['pipe','pipe','pipe']});
    const exited=once(child,'exit');let stderr='';child.stderr.on('data',b=>{stderr+=b});
    try{
        const messages=[{jsonrpc:'2.0',id:0,method:'initialize'},{jsonrpc:'2.0',method:'notifications/initialized'},...Array.from({length:20},(_,i)=>({jsonrpc:'2.0',id:i+1,method:'tools/call',params:{name:'read_inbox',arguments:{}}}))];
        child.stdin.end(messages.map(JSON.stringify).join('\n')+'\n');await firstCall;
        await new Promise(resolve=>setTimeout(resolve,200));assert.equal(calls,1,'HTTP dispatch must stop behind the first unread large MCP response');
        child.stdout.resume();await allCalls;const [code]=await exited;assert.equal(code,0);assert.equal(stderr,'');
    }finally{if(child.exitCode===null)child.kill();await server.close();}
});
test('MCP redacts the actual HTTP header value when the runtime strips surrounding whitespace',async()=>{
    const actual=secret(),control='  '+actual+'  ';
    const server=await httpServer((req,res)=>{const received=req.headers['x-weave-mailbox-control'];assert.equal(received,actual);res.setHeader('Content-Type','application/json');res.end(JSON.stringify({[received]:'reflected header',value:received}));}),client=mcpClient(server.url,control);
    try{await client.initialize();const result=await client.call('read_inbox');assert.equal(result.isError,false);assert.ok(!result.content[0].text.includes(actual));assert.ok(!client.logs.includes(actual));}finally{await client.close();await server.close();}
});
