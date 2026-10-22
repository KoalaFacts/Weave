import assert from 'node:assert/strict';
import {test} from 'node:test';
import {existsSync,mkdtempSync,mkdirSync,rmSync,readFileSync,readdirSync} from 'node:fs';
import {join} from 'node:path';
import {tmpdir} from 'node:os';
import {randomUUID} from 'node:crypto';

const A='11111111-1111-4111-8111-111111111111',B='22222222-2222-4222-8222-222222222222';
export function relay(){
    const ids={A,B},addresses:any[] = [],requests:any[] = [],messages:any[] = [],calls:any[]=[];let connected=false;
    return {calls,messages,requests,open:(role:'A'|'B')=>async()=>({close:async()=>{},call:async(name:string,args:any={})=>{
        calls.push({role,name,args});const own=ids[role],peer=ids[role==='A'?'B':'A'];
        switch(name){
        case 'register_account':return {accountId:own};
        case 'list_addresses':return {items:addresses.filter(a=>a.accountId===own),nextCursor:null};
        case 'create_address':{const a={...args,addressId:randomUUID(),accountId:own};addresses.push(a);return a;}
        case 'prepare_payload':return {messageId:randomUUID(),createdAt:new Date().toISOString(),expiresAt:new Date(Date.now()+3600000).toISOString(),payloadEncoding:'text/plain',bytes:Buffer.from(args.text).toString('base64')};
        case 'request_contact':{const r={...args,requestId:args.payload.messageId,requestMessageId:args.payload.messageId,generation:1,status:'pending'};requests.push(r);messages.push({senderAccountId:own,envelope:{recipientAccountId:peer,contactGeneration:1,payload:args.payload},state:'pending'});return {request:r,generation:1};}
        case 'list_contact_requests':return {items:requests,nextCursor:null};
        case 'decide_contact':connected=true;requests[0].status='accepted';return {isConnected:true,generation:1};
        case 'get_channel':return {generation:1};
        case 'get_contact':return {canSend:connected,connectionToken:'f'.repeat(64)};
        case 'send_message':messages.push({senderAccountId:own,envelope:{recipientAccountId:peer,contactGeneration:1,payload:args.payload},state:'pending'});return {messageId:args.payload.messageId,state:'pending'};
        case 'read_inbox':return {items:messages.filter(m=>m.envelope.recipientAccountId===own&&m.state==='pending'),nextCursor:null};
        case 'ack_message':{const m=messages.find(m=>m.envelope.recipientAccountId===own&&m.senderAccountId===args.senderAccountId&&m.envelope.payload.messageId===args.messageId);assert.ok(m);m.state='acknowledged';return {state:'acknowledged'};}
        case 'read_outbox':return {items:messages.filter(m=>m.senderAccountId===own).map(m=>({messageId:m.envelope.payload.messageId,recipientAccountId:m.envelope.recipientAccountId,state:m.state})),nextCursor:null};
        default:throw Error('unexpected test operation '+name);
        }
    }})};
}
async function pair(t:any){
    assert.ok(existsSync(new URL('../endpoint.ts',import.meta.url)),'Missing independent endpoint runtime');
    const {Endpoint}=await import('../endpoint.ts');
    const root=mkdtempSync(join(tmpdir(),'weave-endpoint-'));t.after(()=>rmSync(root,{recursive:true,force:true}));
    const r=relay(),runId=randomUUID(),seen:any[]=[];
    function endpoint(role:'A'|'B') {const dir=join(root,role);mkdirSync(dir,{mode:0o700});return new Endpoint(role,runId,dir,r.open(role),async(sequence:number,history:any[])=>{seen.push({role,sequence,history:structuredClone(history)});return {text:`Generated ${role} ${sequence}: ${history.at(-1)?.text??'first question'}`,threadId:randomUUID(),inputTokens:10,outputTokens:10};});}
    return {a:endpoint('A'),b:endpoint('B'),root,r,seen,runId};
}
test('two endpoints recover private identities, review introductions, converse and verify receipts',async t=>{
    const {a,b,r,seen}=await pair(t);
    try{
        const ai=await a.setup(),bi=await b.setup();a.peer(bi);b.peer(ai);await a.introduce();await b.accept();
        for(let n=1;n<=6;n++)await (n%2?a:b).step(n);
        await a.consumeFinal();const ae=await a.evidence(),be=await b.evidence();
        const {verifyEvidence}=await import('../endpoint.ts');
        assert.equal(verifyEvidence(ae,be).messages.length,6);assert.equal(seen.length,6);
        assert.equal(seen[1].history.length,1);assert.ok(seen[1].history[0].text.includes('Generated A 1'));
        assert.ok(r.calls.filter(x=>x.name==='create_address').every(x=>x.args.isPublic===false));
        assert.equal(r.calls.filter(x=>x.name==='register_account').length,4);
        assert.ok(r.messages.every(m=>m.state==='acknowledged'));
    }finally{await a.close();await b.close();}
});
test('recipient will not accept an introduction from a different run',async t=>{
    const {a,b,r}=await pair(t);try{const ai=await a.setup(),bi=await b.setup();a.peer(bi);b.peer(ai);await a.introduce();
        r.messages[0].envelope.payload.bytes=Buffer.from(JSON.stringify({type:'weave-pair-contact',runId:randomUUID()})).toString('base64');
        await assert.rejects(b.accept());assert.equal(r.calls.filter(x=>x.name==='decide_contact').length,0);
    }finally{await a.close();await b.close();}
});
test('a write failure preserves a pending exact payload without retry',async t=>{
    const {a,b,root}=await pair(t);try{const ai=await a.setup(),bi=await b.setup();a.peer(bi);b.peer(ai);await a.introduce();await b.accept();
        const original=(a as any).client.call.bind((a as any).client);let attempts=0;
        (a as any).client.call=async(name:string,args:any)=>{if(name==='send_message'){attempts++;throw Error('timeout');}return original(name,args);};
        await assert.rejects(a.step(1));assert.equal(attempts,1);
        const checkpoints=readdirSync(join(root,'A')).filter(f=>f.startsWith('intent-')).map(f=>JSON.parse(readFileSync(join(root,'A',f),'utf8')));
        const pending=checkpoints.find(c=>c.operation==='send_message');assert.equal(pending.status,'pending');assert.ok(pending.arguments.payload.messageId);assert.ok(!JSON.stringify(checkpoints).includes('f'.repeat(64)));
    }finally{await a.close();await b.close();}
});
test('incomplete pagination fails rather than being treated as no contact',async()=>{
    const {pages}=await import('../mcp.ts');let n=0;
    await assert.rejects(pages({call:async()=>({items:[],nextCursor:String(++n)}),close:async()=>{}},'read_inbox'),/pagination_incomplete/);assert.equal(n,20);
});
test('a changed account during fresh recovery stops before creating an address',async t=>{
    const {a,r,root}=await pair(t);let openings=0;const original=(a as any).open;
    (a as any).open=async()=>{const c=await original();if(++openings===2){const call=c.call;c.call=async(name:string,args:any)=>name==='register_account'?{accountId:randomUUID()}:call(name,args);}return c;};
    try{await assert.rejects(a.setup(),/account_changed/);assert.equal(r.calls.filter(x=>x.name==='create_address').length,0);assert.equal(JSON.parse(readFileSync(join(root,'A/identity.json'),'utf8')).accountId,A);}finally{await a.close();}
});
test('failed fresh authentication does not trigger another registration',async t=>{
    const {a,r}=await pair(t);let openings=0;const original=(a as any).open;
    (a as any).open=async()=>{const c=await original();if(++openings===2){const call=c.call;c.call=async(name:string,args:any)=>{if(name==='list_addresses')throw Error('401 fixture');return call(name,args);};}return c;};
    try{await assert.rejects(a.setup());assert.equal(r.calls.filter(x=>x.name==='register_account').length,1);assert.equal(r.calls.filter(x=>x.name==='create_address').length,0);}finally{await a.close();}
});
test('completion rejects fabricated receiver content even with acknowledged receipts',async t=>{
    const {a,b}=await pair(t);try{const ai=await a.setup(),bi=await b.setup();a.peer(bi);b.peer(ai);await a.introduce();await b.accept();for(let n=1;n<=6;n++)await(n%2?a:b).step(n);await a.consumeFinal();const ae=await a.evidence(),be=await b.evidence();be.received[0].text='fabricated';const {verifyEvidence}=await import('../endpoint.ts');assert.throws(()=>verifyEvidence(ae,be));}finally{await a.close();await b.close();}
});
