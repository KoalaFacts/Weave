import assert from 'node:assert/strict';
import {test} from 'node:test';
import {createServer} from 'node:http';
import {spawn} from 'node:child_process';
import {mkdtemp,readFile,stat,rm} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {fileURLToPath} from 'node:url';

const alice='aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',bob='bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
const names=['get_my_profile','ensure_my_address','list_my_contact_requests','list_my_contacts','get_my_channel',
    'list_my_inbox','list_my_outbox','request_contact','decide_contact','send_message','ack_message'];
test('committed remote send with lost HTTP response retains an exact token-free recovery journal without retry',{skip:process.platform==='win32'&&'Requires POSIX owner-only file modes for private pair state'},async()=>{
    let requestId='',accepted=false,sendCount=0,committed:any=null;
    const server=createServer(async(req,res)=>{
        const parts:Buffer[]=[];for await(const part of req)parts.push(part);
        const rpc=JSON.parse(Buffer.concat(parts).toString());
        const owner=req.headers.authorization==='Bearer fake-a'?alice:req.headers.authorization==='Bearer fake-b'?bob:null;
        const {name,arguments:args={}}=rpc.params??{};
        let value:any;
        if(rpc.method==='initialize')value={protocolVersion:'2025-11-25'};
        else if(rpc.method==='tools/list')value={tools:names.map(name=>({name}))};
        else if(!owner){res.statusCode=401;res.end('unauthorized');return;}
        else if(name==='get_my_profile')value={id:owner};
        else if(name==='list_my_contacts')value={items:accepted?[{accountId:owner===alice?bob:alice,canSend:true}]:[],nextCursor:null};
        else if(name==='ensure_my_address')value={addressId:args.addressId,accountId:owner,isPublic:false};
        else if(name==='request_contact'){requestId=args.payload.messageId;value={generation:'1'};}
        else if(name==='list_my_contact_requests')value={items:owner===bob&&requestId?[{requestId,status:'pending'}]:[],nextCursor:null};
        else if(name==='decide_contact'){assert.equal(owner,bob);assert.equal(args.requestId,requestId);accepted=true;value={status:'accepted'};}
        else if(name==='get_my_channel'){assert.ok(accepted);value={generation:'1',isBlockedByMailbox:false,isBlockedByPeer:false};}
        else if(name==='send_message'){
            assert.ok(accepted);assert.equal(args.contactGeneration,'1');
            sendCount++;committed={owner,recipient:args.recipientAccountId,generation:args.contactGeneration,payload:args.payload};
            req.socket.destroy();return;
        }else throw Error('Unexpected call after intentionally lost send receipt: '+name);
        res.setHeader('Content-Type','application/json');
        res.end(JSON.stringify({jsonrpc:'2.0',id:rpc.id,result:rpc.method==='tools/call'?{content:[{type:'text',text:JSON.stringify(value)}]}:value}));
    });
    await new Promise<void>(resolve=>server.listen(0,'127.0.0.1',resolve));
    const port=(server.address() as {port:number}).port,temp=await mkdtemp(join(tmpdir(),'weave-unknown-send-')),
        path=join(temp,'result.json');
    try{
        const script=fileURLToPath(new URL('../../../examples/remote-mcp-pair/run.ts',import.meta.url));
        const result=await new Promise<{status:number|null;output:string}>(resolve=>{
            const child=spawn(process.execPath,[...(process.versions.node.startsWith('22.')?['--experimental-strip-types']:[]),script],{
                env:{...process.env,WEAVE_MCP_URL:'http://127.0.0.1:'+port+'/mcp',WEAVE_LIVE_ACCEPTANCE:'1',
                    WEAVE_EVIDENCE_PATH:path,WEAVE_AGENT_A_ACCESS_TOKEN:'fake-a',WEAVE_AGENT_B_ACCESS_TOKEN:'fake-b'}});
            let output='';child.stderr.on('data',x=>output+=x.toString());
            child.on('exit',status=>resolve({status,output}));
        });
        assert.notEqual(result.status,0,'lost response must not be considered completed');
        assert.equal(sendCount,1,'a committed unknown send must not be dispatched again');
        assert.ok(committed?.payload?.messageId,'fixture must commit before dropping transport');
        const journalPath=path+'.journal',contents=await readFile(journalPath,'utf8');
        assert.equal((await stat(journalPath)).mode&0o777,0o600);
        const lines=contents.trim().split('\n').map(x=>JSON.parse(x));
        const intent=lines.find(x=>x.phase==='prepared'&&x.name==='send_message');
        assert.ok(intent,'original send must be durably recorded before network dispatch');
        assert.equal(intent.args.recipientAccountId,committed.recipient);
        assert.equal(intent.args.contactGeneration,committed.generation);
        assert.deepEqual(intent.args.payload,committed.payload,'the original exact envelope must be recoverable');
        assert.ok(lines.some(x=>x.phase==='unknown'&&x.operationId===intent.operationId),
            'lost response must be marked unknown, not retried or declared failure without effect');
        assert.doesNotMatch(contents+result.output,/fake-a|fake-b/);
        try{const final=JSON.parse(await readFile(path,'utf8'));assert.notEqual(final.completed,true);}
        catch(error){if((error as NodeJS.ErrnoException).code!=='ENOENT'&&!(error instanceof SyntaxError))throw error;}
    }finally{server.closeAllConnections();server.close();await rm(temp,{recursive:true,force:true});}
});
