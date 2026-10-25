/** Two independent, token-isolated MCP client processes exercising real remote HTTP.
 * This is a deterministic transport acceptance tool, NOT two model-driven agents.
 */
import assert from 'node:assert/strict';
import {fork, type ChildProcess} from 'node:child_process';
import {randomBytes, randomUUID, createHash} from 'node:crypto';
import {writeFile} from 'node:fs/promises';
import {fileURLToPath} from 'node:url';

const toolNames=['get_my_profile','ensure_my_address','list_my_contact_requests','list_my_contacts','get_my_channel',
    'list_my_inbox','list_my_outbox','request_contact','decide_contact','send_message','ack_message'];
const id=/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const origin=process.env.WEAVE_MCP_URL??'https://demo.weave-389.workers.dev/mcp';
const endpoint=new URL(origin);
if(endpoint.pathname!=='/mcp'||endpoint.search||endpoint.hash||endpoint.username||endpoint.password||
    !(endpoint.protocol==='https:'||endpoint.protocol==='http:'&&['localhost','127.0.0.1','[::1]'].includes(endpoint.hostname)))
    throw Error('WEAVE_MCP_URL must be an HTTPS /mcp resource or local loopback test endpoint.');
if(endpoint.hostname!=='demo.weave-389.workers.dev'&&
    !['localhost','127.0.0.1','[::1]'].includes(endpoint.hostname)&&
    process.env.WEAVE_ALLOW_CUSTOM_ORIGIN!=='1')
    throw Error('Non-demo remote MCP URLs require an explicit WEAVE_ALLOW_CUSTOM_ORIGIN=1 trust decision.');

type Reply={id:number,result?:unknown,error?:string};
type Rpc={id:number,name:string,args?:Record<string,unknown>};
type Client={pid:number;call:(name:string,args?:Record<string,unknown>)=>Promise<any>;close:()=>Promise<void>};
const secret=process.env.WEAVE_ACCESS_TOKEN;
async function remote(name:string,args:Record<string,unknown>={}):Promise<any>{
    const method=name==='initialize'||name==='tools/list'?name:'tools/call';
    const parameters=name==='initialize'?{protocolVersion:'2025-11-25',capabilities:{},clientInfo:{name:'weave-live-pair',version:'1'}}:
        name==='tools/list'?{}:{name,arguments:args};
    const response=await fetch(endpoint,{method:'POST',redirect:'error',signal:AbortSignal.timeout(12000),headers:{
        'Content-Type':'application/json','Accept':'application/json, text/event-stream','MCP-Protocol-Version':'2025-11-25',
        ...(method==='tools/call'?{Authorization:'Bearer '+secret}:{})
    },body:JSON.stringify({jsonrpc:'2.0',id:1,method,params:parameters})});
    if(response.headers.get('content-length')&&Number(response.headers.get('content-length'))>1024*1024)
        throw Error('MCP response exceeded the size limit');
    const body=await response.text();
    if(body.length>1024*1024)throw Error('MCP response exceeded the size limit');
    let rpc:any;try{rpc=JSON.parse(body);}catch{throw Error('MCP '+name+' returned malformed JSON (HTTP '+response.status+')');}
    if(!response.ok||rpc.error||rpc.result?.isError)throw Error('MCP '+name+' denied or unavailable (HTTP '+response.status+')');
    if(method!=='tools/call')return rpc.result;
    const value=rpc.result?.structuredContent ?? (rpc.result?.content?.[0]?.text);
    if(typeof value==='object'&&value!==null)return value;
    if(typeof value!=='string')throw Error('MCP '+name+' returned an invalid tool result');
    try{return JSON.parse(value);}catch{throw Error('MCP '+name+' returned invalid tool JSON');}
}
if(process.argv.includes('--agent')){
    if(!secret)throw Error('Agent missing its own access token');
    process.on('message',async msg=>{
        const req=msg as Rpc;
        if(!req||!Number.isSafeInteger(req.id)||typeof req.name!=='string')return;
        let reply:Reply;
        try {reply={id:req.id,result:await remote(req.name,req.args)};}
        catch {reply={id:req.id,error:'MCP '+req.name+' failed; preserve write IDs and inspect access separately'};}
        process.send?.(reply);
    });
}else{
    const tokenA=process.env.WEAVE_AGENT_A_ACCESS_TOKEN,tokenB=process.env.WEAVE_AGENT_B_ACCESS_TOKEN;
    if(process.env.WEAVE_LIVE_ACCEPTANCE!=='1'||!tokenA||!tokenB||tokenA===tokenB)
        throw Error('Explicit WEAVE_LIVE_ACCEPTANCE=1 and two distinct privately injected bearer tokens are required; no account is created automatically.');
    if(!process.env.WEAVE_EVIDENCE_PATH)throw Error('Provide WEAVE_EVIDENCE_PATH for an exclusive redacted receipt.');
    const script=fileURLToPath(import.meta.url);
    function start(role:string,token:string):Client{
        const child:ChildProcess=fork(script,['--agent'],{stdio:['ignore','ignore','ignore','ipc'],
            execArgv:process.execArgv.filter(flag=>flag==='--experimental-strip-types'),env:{
                PATH:process.env.PATH??'',SystemRoot:process.env.SystemRoot??'',WEAVE_MCP_URL:endpoint.href,
                WEAVE_ACCESS_TOKEN:token,WEAVE_AGENT_ROLE:role,
                WEAVE_ALLOW_CUSTOM_ORIGIN:process.env.WEAVE_ALLOW_CUSTOM_ORIGIN??''
            }});
        const waiting=new Map<number,{resolve:(v:unknown)=>void;reject:(e:Error)=>void;timeout:ReturnType<typeof setTimeout>}>();
        let next=0;
        child.on('message',(msg:Reply)=>{
            const pending=waiting.get(msg.id);if(!pending)return;
            waiting.delete(msg.id);clearTimeout(pending.timeout);
            if(msg.error)pending.reject(Error(msg.error));else pending.resolve(msg.result);
        });
        child.on('exit',()=>{for(const pending of waiting.values()){clearTimeout(pending.timeout);pending.reject(Error('Agent process exited during a remote call; write outcome may be unknown'));}waiting.clear();});
        return {pid:child.pid??0,call:(name,args={})=>new Promise((resolve,reject)=>{
            if(!child.connected){reject(Error('Agent process disconnected'));return;}
            const n=++next,timeout=setTimeout(()=>{
                waiting.delete(n);reject(Error('Agent request timeout: write outcome may be unknown'));
            },16000);
            waiting.set(n,{resolve,reject,timeout});child.send({id:n,name,args} satisfies Rpc);
        }),close:async()=>{
            if(child.exitCode!==null)return;
            await new Promise<void>(resolve=>{child.once('exit',()=>resolve());child.disconnect();
                const timer=setTimeout(()=>child.kill(),1500);timer.unref();});
        }};
    }
    function payload(value:string){
        const when=Date.now(),prefix=when.toString(16).padStart(12,'0'),random=randomBytes(10);
        const messageId=prefix.slice(0,8)+'-'+prefix.slice(8)+'-7'+random.toString('hex').slice(0,3)+'-'+(8+(random[2]&3)).toString(16)+random.toString('hex').slice(5,8)+'-'+random.toString('hex').slice(8,20);
        return {messageId,createdAt:new Date(when).toISOString(),expiresAt:new Date(when+3600000).toISOString(),
            payloadEncoding:'text/plain',bytes:Buffer.from(value).toString('base64')};
    }
    const evidence:{[key:string]:unknown}={kind:'scripted-remote-MCP-transport',notModelAgentProof:true,target:endpoint.href,
        startedAt:new Date().toISOString(),messages:[]};
    let a=start('A',tokenA),b=start('B',tokenB);
    try{
        assert.notEqual(a.pid,b.pid,'agents must run in distinct OS processes');
        for(const c of [a,b]){
            const init=await c.call('initialize');assert.equal(init.protocolVersion,'2025-11-25');
            const list=await c.call('tools/list');const names=new Set(list.tools.map((x:{name:string})=>x.name));
            for(const name of toolNames)assert.ok(names.has(name),'missing authenticated tool '+name+'; OAuth AS may not be configured');
        }
        const profileA=await a.call('get_my_profile'),profileB=await b.call('get_my_profile');
        assert.ok(id.test(profileA.id)&&id.test(profileB.id));assert.notEqual(profileA.id,profileB.id);
        evidence.accountHashes=[profileA.id,profileB.id].map(x=>createHash('sha256').update(x).digest('hex').slice(0,16));
        const addressA=randomUUID(),addressB=randomUUID(),nonce=randomBytes(8).toString('hex');
        await a.call('ensure_my_address',{addressId:addressA,label:'Remote pair '+nonce+' A'});
        await b.call('ensure_my_address',{addressId:addressB,label:'Remote pair '+nonce+' B'});
        const intro=payload('synthetic introduction '+nonce);
        const decision=await a.call('request_contact',{requestId:intro.messageId,requesterAddressId:addressA,
            recipientAccountId:profileB.id,recipientAddressId:addressB,payload:intro});
        async function find(c:Client,tool:string,predicate:(x:any)=>boolean){
            let afterCursor:undefined|string;
            for(let page=0;page<100;page++){
                const data=await c.call(tool,{limit:10,...(afterCursor?{afterCursor}:{})});
                if(!Array.isArray(data.items))throw Error('MCP pagination contract failed');
                const match=data.items.find(predicate);
                if(match)return match;
                if(!data.nextCursor)break;
                afterCursor=data.nextCursor;
            }
            throw Error('Expected owner-scoped record unavailable or pagination incomplete');
        }
        await find(b,'list_my_contact_requests',(x:any)=>x.requestId===intro.messageId);
        await b.call('decide_contact',{requestId:intro.messageId,requesterAccountId:profileA.id,
            expectedGeneration:String(decision.generation),status:'accepted'});
        const generation=String(decision.generation);
        await find(a,'list_my_contacts',(x:any)=>x.accountId===profileB.id&&x.canSend===true);
        await find(b,'list_my_contacts',(x:any)=>x.accountId===profileA.id&&x.canSend===true);
        const seenA=await a.call('get_my_channel',{peerAccountId:profileB.id}),seenB=await b.call('get_my_channel',{peerAccountId:profileA.id});
        assert.equal(String(seenA.generation),generation);assert.equal(String(seenB.generation),generation);
        const findInbox=(c:Client,messageId:string)=>find(c,'list_my_inbox',
            (x:any)=>x.envelope?.payload?.messageId===messageId);
        const findOutbox=(c:Client,messageId:string)=>find(c,'list_my_outbox',
            (x:any)=>x.messageId===messageId);
        const pending:{messageId:string;body:string;sender:'A'|'B'}[]=[];
        let lastPayload:ReturnType<typeof payload>|null=null;
        for(let i=0;i<6;i++){
            const sender=i%2===0?a:b,receiver=i%2===0?b:a;
            const dest=i%2===0?profileB.id:profileA.id;
            const content='weave-remote-pair-'+nonce+'-'+i,body=payload(content);
            if(i===5)lastPayload=body;
            const receipt=await sender.call('send_message',{recipientAccountId:dest,contactGeneration:generation,payload:body});
            assert.equal(receipt.messageId,body.messageId);
            if(i<2){pending.push({messageId:body.messageId,body:content,sender:i===0?'A':'B'});}
            else {
                const item=await findInbox(receiver,body.messageId);
                assert.equal(Buffer.from(item.envelope.payload.bytes,'base64').toString(),content);
                assert.equal(item.senderMailboxId,i%2===0?profileA.id:profileB.id);
                const ack=await receiver.call('ack_message',{messageId:body.messageId,senderAccountId:item.senderMailboxId});
                assert.equal(ack.state,'acknowledged');
                assert.equal((await findOutbox(sender,body.messageId)).state,'acknowledged');
            }
            (evidence.messages as unknown[]).push({sequence:i+1,direction:i%2===0?'A→B':'B→A',messageId:body.messageId,acknowledged:i>=2});
            if(i===1){
                await a.close();await b.close();a=start('A',tokenA);b=start('B',tokenB);
                assert.notEqual(a.pid,b.pid);
                assert.equal((await a.call('get_my_profile')).id,profileA.id);
                assert.equal((await b.call('get_my_profile')).id,profileB.id);
                for(const item of pending){
                    const receiving=item.sender==='A'?b:a,sending=item.sender==='A'?a:b;
                    const received=await findInbox(receiving,item.messageId);
                    assert.equal(Buffer.from(received.envelope.payload.bytes,'base64').toString(),item.body);
                    const ack=await receiving.call('ack_message',{messageId:item.messageId,senderAccountId:received.senderMailboxId});
                    assert.equal(ack.state,'acknowledged');
                    assert.equal((await findOutbox(sending,item.messageId)).state,'acknowledged');
                }
                (evidence.messages as Array<{acknowledged:boolean}>).forEach(x=>x.acknowledged=true);
                evidence.bothProcessesRestarted=true;
            }
        }
        assert.ok(lastPayload);
        const original=await b.call('send_message',{recipientAccountId:profileA.id,contactGeneration:generation,payload:lastPayload});
        assert.equal(original.state,'acknowledged','a duplicate must return the original final receipt, not enqueue');
        assert.equal(original.messageId,lastPayload.messageId);
        const outboxB=await b.call('list_my_outbox',{limit:10});
        assert.equal(outboxB.items.filter((x:any)=>x.messageId===lastPayload.messageId).length,1);
        evidence.retainedSendReplayedWithoutRedelivery=true;
        evidence.finishedAt=new Date().toISOString();evidence.completed=true;
        await writeFile(process.env.WEAVE_EVIDENCE_PATH!,JSON.stringify(evidence,null,2)+'\n',{flag:'wx',mode:0o600});
        process.stdout.write('Six remote MCP messages and ACKs confirmed through two isolated client processes; redacted evidence written.\n');
    }finally{await Promise.allSettled([a.close(),b.close()]);}
}
