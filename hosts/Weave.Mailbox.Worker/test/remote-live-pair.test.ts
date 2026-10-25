import assert from 'node:assert/strict';
import {test} from 'node:test';
import {createServer} from 'node:http';
import {spawn} from 'node:child_process';
import {mkdtemp,readFile,stat,rm,writeFile} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {fileURLToPath} from 'node:url';

const alice='aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',bob='bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
const names=['get_my_profile','ensure_my_address','list_my_contact_requests','list_my_contacts','get_my_channel','list_my_inbox','list_my_outbox','request_contact','decide_contact','send_message','ack_message'];
const msg=new Map<string,{from:string;to:string;payload:any;state:string}>();
let introduction:any=null,accepted=false,sendCount=0;
test('two isolated real HTTP MCP clients preserve two pending messages over both process restarts; six ACKs; no credential leakage',async()=>{
    msg.clear();introduction=null;accepted=false;sendCount=0;
    const exactGeneration='9007199254740993';
    // Eight retained B receipts put this run's third B send on the next page.
    for(let n=0;n<8;n++){const messageId='existing-b-receipt-'+n;
        msg.set(messageId,{from:bob,to:alice,payload:{messageId},state:'acknowledged'});}
    const server=createServer(async(req,res)=>{
        const parts:Buffer[]=[];for await(const part of req)parts.push(part);
        const rpc=JSON.parse(Buffer.concat(parts).toString());let value:any;
        const owner=req.headers.authorization==='Bearer fake-a'?alice:req.headers.authorization==='Bearer fake-b'?bob:null;
        const {name,arguments:args={}}=rpc.params??{};
        if(rpc.method==='initialize')value={protocolVersion:'2025-11-25'};
        else if(rpc.method==='tools/list')value={tools:names.map(name=>({name}))};
        else if(!owner){res.statusCode=401;res.end(JSON.stringify({error:'unauthorized'}));return;}
        else if(name==='get_my_profile')value={id:owner};
        else if(name==='ensure_my_address')value={addressId:args.addressId,accountId:owner,isPublic:false};
        else if(name==='request_contact'){introduction={...args,requesterAccountId:owner,status:'pending'};value={generation:exactGeneration};}
        else if(name==='list_my_contact_requests')value={items:owner===bob&&introduction?[{requestId:introduction.requestId,status:introduction.status}]:[],nextCursor:null};
        else if(name==='decide_contact'){assert.equal(owner,bob);accepted=true;introduction.status='accepted';value={status:'accepted'};}
        else if(name==='get_my_channel'){assert.ok(accepted);value={generation:exactGeneration,isBlockedByMailbox:false,isBlockedByPeer:false};}
        else if(name==='list_my_contacts')value={items:accepted?[{accountId:owner===alice?bob:alice,canSend:true}]:[],nextCursor:null};
        else if(name==='send_message'){
            assert.ok(accepted);assert.equal(args.contactGeneration,exactGeneration);
            const existing=msg.get(args.payload.messageId);
            if(!existing){sendCount++;msg.set(args.payload.messageId,{from:owner,to:args.recipientAccountId,payload:args.payload,state:'pending'});}
            value={messageId:args.payload.messageId,state:msg.get(args.payload.messageId)?.state};
        }else if(name==='list_my_inbox')value={items:[...msg.values()].filter(x=>x.to===owner&&x.state==='pending').map(x=>({senderMailboxId:x.from,envelope:{payload:x.payload}})),nextCursor:null};
        else if(name==='ack_message'){
            const item=msg.get(args.messageId);assert.ok(item);assert.equal(item.to,owner);item.state='acknowledged';value={messageId:args.messageId,state:'acknowledged'};
        }else if(name==='list_my_outbox'){
            const rows=[...msg].filter(([_,x])=>x.from===owner).map(([messageId,x])=>({messageId,state:x.state}));
            const offset=args.afterCursor===undefined?0:Number(args.afterCursor),size=args.limit??10;
            assert.ok(Number.isSafeInteger(offset)&&offset>=0);
            assert.ok(Number.isSafeInteger(size)&&size>=1&&size<=10);
            value={items:rows.slice(offset,offset+size),nextCursor:offset+size<rows.length?String(offset+size):null};
        }
        else throw Error('Unknown remote tool '+name);
        res.setHeader('Content-Type','application/json');
        let wire=JSON.stringify({jsonrpc:'2.0',id:rpc.id,result:rpc.method==='tools/call'?{content:[{type:'text',text:JSON.stringify(value)}]}:value});
        // Real Worker serializes int64 generations as unquoted JSON numbers. Send
        // the number literally, not via JavaScript Number/stringify (which rounds).
        if(name==='request_contact'||name==='get_my_channel'){
            const quoted='\\"generation\\":\\"'+exactGeneration+'\\"';
            assert.ok(wire.includes(quoted),'fixture must emit an exact numeric int64');
            wire=wire.replace(quoted,'\\"generation\\":'+exactGeneration);
        }
        res.end(wire);
    });
    await new Promise<void>(resolve=>server.listen(0,'127.0.0.1',resolve));
    const port=(server.address() as {port:number}).port;
    const temp=await mkdtemp(join(tmpdir(),'weave-pair-')),evidence=join(temp,'result.json');
    try{
        const script=fileURLToPath(new URL('../../../examples/remote-mcp-pair/run.ts',import.meta.url));
        const exit=await new Promise<{status:number|null;stdout:string;stderr:string}>(resolve=>{
            const child=spawn(process.execPath,[...(process.versions.node.startsWith('22.')?['--experimental-strip-types']:[]),script],{
                env:{...process.env,WEAVE_MCP_URL:'http://127.0.0.1:'+port+'/mcp',WEAVE_LIVE_ACCEPTANCE:'1',
                    WEAVE_EVIDENCE_PATH:evidence,WEAVE_AGENT_A_ACCESS_TOKEN:'fake-a',WEAVE_AGENT_B_ACCESS_TOKEN:'fake-b'}});
            let stdout='',stderr='';child.stdout.on('data',x=>stdout+=x.toString());child.stderr.on('data',x=>stderr+=x.toString());
            child.on('exit',status=>resolve({status,stdout,stderr}));
        });
        assert.equal(exit.status,0,exit.stderr);assert.equal(sendCount,6);
        const data=JSON.parse(await readFile(evidence,'utf8'));
        assert.equal(data.completed,true);assert.equal(data.bothProcessesRestarted,true);assert.equal(data.retainedSendReplayedWithoutRedelivery,true);
        assert.equal(data.notModelAgentProof,true);assert.equal(data.messages.length,6);
        assert.ok(data.messages.every((x:any)=>x.acknowledged));
        assert.equal((await stat(evidence)).mode&0o777,0o600);
        const serialized=exit.stdout+exit.stderr+JSON.stringify(data);
        assert.doesNotMatch(serialized,/fake-a|fake-b|weave-remote-pair-[0-9a-f]{16}/);
    }finally{server.close();await rm(temp,{recursive:true,force:true});}
});

test('remote service without configured OAuth never creates addresses or sends messages',async()=>{
    let privateCalls=0;
    const server=createServer(async(req,res)=>{
        const chunks:Buffer[]=[];for await(const part of req)chunks.push(part);
        const rpc=JSON.parse(Buffer.concat(chunks).toString());
        if(rpc.method==='tools/call')privateCalls++;
        const result=rpc.method==='initialize'?{protocolVersion:'2025-11-25'}:{tools:[{name:'demo_guide'},{name:'search_registry'}]};
        res.setHeader('Content-Type','application/json');res.end(JSON.stringify({jsonrpc:'2.0',id:rpc.id,result}));
    });
    await new Promise<void>(resolve=>server.listen(0,'127.0.0.1',resolve));
    const port=(server.address() as {port:number}).port,temp=await mkdtemp(join(tmpdir(),'weave-deny-')),
        path=join(temp,'not-created.json');
    try{
        const script=fileURLToPath(new URL('../../../examples/remote-mcp-pair/run.ts',import.meta.url));
        const run=await new Promise<{status:number|null;output:string}>(resolve=>{
            const child=spawn(process.execPath,[...(process.versions.node.startsWith('22.')?['--experimental-strip-types']:[]),script],{
                env:{...process.env,WEAVE_MCP_URL:'http://127.0.0.1:'+port+'/mcp',WEAVE_LIVE_ACCEPTANCE:'1',
                    WEAVE_EVIDENCE_PATH:path,WEAVE_AGENT_A_ACCESS_TOKEN:'fake-a',WEAVE_AGENT_B_ACCESS_TOKEN:'fake-b'}});
            let output='';child.stderr.on('data',x=>output+=x.toString());
            child.on('exit',status=>resolve({status,output}));
        });
        assert.notEqual(run.status,0);assert.match(run.output,/missing authenticated tool/);
        assert.equal(privateCalls,0);await assert.rejects(readFile(path));
    }finally{server.close();await rm(temp,{recursive:true,force:true});}
});

for(const corrupted of ['id','version','content-type']){
    test('remote HTTP client rejects invalid MCP '+corrupted+' before any further operation',async()=>{
        const seen:string[]=[];
        const server=createServer(async(req,res)=>{
            const parts:Buffer[]=[];for await(const part of req)parts.push(part);
            const rpc=JSON.parse(Buffer.concat(parts).toString());
            seen.push(rpc.method);
            const wrongId=corrupted==='id'&&rpc.method==='initialize';
            res.setHeader('Content-Type',corrupted==='content-type'?'text/plain':'application/json');
            res.end(JSON.stringify({jsonrpc:corrupted==='version'?'1.0':'2.0',
                id:wrongId?rpc.id+1:rpc.id,
                result:rpc.method==='initialize'?{protocolVersion:'2025-11-25'}:{tools:[]}}));
        });
        await new Promise<void>(resolve=>server.listen(0,'127.0.0.1',resolve));
        const port=(server.address() as {port:number}).port,temp=await mkdtemp(join(tmpdir(),'weave-invalid-rpc-'));
        const path=join(temp,'result.json');
        try{
            const script=fileURLToPath(new URL('../../../examples/remote-mcp-pair/run.ts',import.meta.url));
            const result=await new Promise<{status:number|null;error:string}>(resolve=>{
                const child=spawn(process.execPath,[...(process.versions.node.startsWith('22.')?['--experimental-strip-types']:[]),script],{
                    env:{...process.env,WEAVE_MCP_URL:'http://127.0.0.1:'+port+'/mcp',WEAVE_LIVE_ACCEPTANCE:'1',
                        WEAVE_EVIDENCE_PATH:path,WEAVE_AGENT_A_ACCESS_TOKEN:'fake-a',WEAVE_AGENT_B_ACCESS_TOKEN:'fake-b'}});
                let error='';child.stderr.on('data',chunk=>error+=chunk.toString());
                child.on('exit',status=>resolve({status,error}));
            });
            assert.notEqual(result.status,0);
            assert.deepEqual(seen,['initialize'],'invalid MCP transport responses must not advance the handshake');
            assert.doesNotMatch(result.error,/fake-a|fake-b/);
            await assert.rejects(readFile(path));
        }finally{server.close();await rm(temp,{recursive:true,force:true});}
    });
}

test('remote client stops reading an oversized unterminated HTTP response without waiting for timeout',async()=>{
    let requests=0;
    const server=createServer(async(req,res)=>{
        const chunks:Buffer[]=[];for await(const part of req)chunks.push(part);
        requests++;
        res.setHeader('Content-Type','application/json');
        res.write('x'.repeat(1024*1024+4096));
        // Give a whole-response text() implementation time to wait. A streaming
        // implementation must reject the oversized first chunk before this ends.
        const timer=setTimeout(()=>res.end(),5000);
        res.on('close',()=>clearTimeout(timer));
    });
    await new Promise<void>(resolve=>server.listen(0,'127.0.0.1',resolve));
    const port=(server.address() as {port:number}).port,temp=await mkdtemp(join(tmpdir(),'weave-stream-bound-'));
    const path=join(temp,'result.json');
    try{
        const script=fileURLToPath(new URL('../../../examples/remote-mcp-pair/run.ts',import.meta.url));
        const startedAt=Date.now();
        const result=await new Promise<{status:number|null;error:string}>(resolve=>{
            const child=spawn(process.execPath,[...(process.versions.node.startsWith('22.')?['--experimental-strip-types']:[]),script],{
                env:{...process.env,WEAVE_MCP_URL:'http://127.0.0.1:'+port+'/mcp',WEAVE_LIVE_ACCEPTANCE:'1',
                    WEAVE_EVIDENCE_PATH:path,WEAVE_AGENT_A_ACCESS_TOKEN:'fake-a',WEAVE_AGENT_B_ACCESS_TOKEN:'fake-b'}});
            let error='';child.stderr.on('data',chunk=>error+=chunk.toString());
            child.on('exit',status=>resolve({status,error}));
        });
        assert.ok(Date.now()-startedAt<3500,'the client must enforce its byte bound while the body is still streaming');
        assert.notEqual(result.status,0);
        assert.equal(requests,1);
        assert.doesNotMatch(result.error,/fake-a|fake-b/);
        await assert.rejects(readFile(path));
    }finally{
        server.closeAllConnections();server.close();
        await rm(temp,{recursive:true,force:true});
    }
});

for(const mode of ['already-connected','occupied-evidence']){
    test('remote acceptance preflight refuses '+mode+' before creating private addresses',async()=>{
        let writes=0;
        const server=createServer(async(req,res)=>{
            const chunks:Buffer[]=[];for await(const part of req)chunks.push(part);
            const rpc=JSON.parse(Buffer.concat(chunks).toString());
            const owner=req.headers.authorization==='Bearer fake-a'?alice:req.headers.authorization==='Bearer fake-b'?bob:null;
            const name=rpc.params?.name;let value:any;
            if(rpc.method==='initialize')value={protocolVersion:'2025-11-25'};
            else if(rpc.method==='tools/list')value={tools:names.map(name=>({name}))};
            else if(name==='get_my_profile')value={id:owner};
            else if(name==='list_my_contacts')value={items:mode==='already-connected'?[{accountId:owner===alice?bob:alice,canSend:true}]:[],nextCursor:null};
            else {writes++;res.statusCode=409;res.setHeader('Content-Type','application/json');
                res.end(JSON.stringify({jsonrpc:'2.0',id:rpc.id,error:{code:-32000,message:'unexpected remote write'}}));return;}
            res.setHeader('Content-Type','application/json');
            res.end(JSON.stringify({jsonrpc:'2.0',id:rpc.id,result:rpc.method==='tools/call'?{content:[{type:'text',text:JSON.stringify(value)}]}:value}));
        });
        await new Promise<void>(resolve=>server.listen(0,'127.0.0.1',resolve));
        const port=(server.address() as {port:number}).port,temp=await mkdtemp(join(tmpdir(),'weave-preflight-')),
            path=join(temp,'result.json');
        try{
            if(mode==='occupied-evidence')await writeFile(path,'existing evidence',{flag:'wx',mode:0o600});
            const script=fileURLToPath(new URL('../../../examples/remote-mcp-pair/run.ts',import.meta.url));
            const result=await new Promise<{status:number|null;output:string}>(resolve=>{
                const child=spawn(process.execPath,[...(process.versions.node.startsWith('22.')?['--experimental-strip-types']:[]),script],{
                    env:{...process.env,WEAVE_MCP_URL:'http://127.0.0.1:'+port+'/mcp',WEAVE_LIVE_ACCEPTANCE:'1',
                        WEAVE_EVIDENCE_PATH:path,WEAVE_AGENT_A_ACCESS_TOKEN:'fake-a',WEAVE_AGENT_B_ACCESS_TOKEN:'fake-b'}});
                let output='';child.stderr.on('data',x=>output+=x.toString());
                child.on('exit',status=>resolve({status,output}));
            });
            assert.notEqual(result.status,0);
            assert.equal(writes,0,'preflight must refuse before any remote write');
            assert.doesNotMatch(result.output,/fake-a|fake-b/);
            if(mode==='occupied-evidence')assert.equal(await readFile(path,'utf8'),'existing evidence');
            else await assert.rejects(readFile(path));
        }finally{server.closeAllConnections();server.close();await rm(temp,{recursive:true,force:true});}
    });
}
