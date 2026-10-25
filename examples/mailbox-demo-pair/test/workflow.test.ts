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
    function endpoint(role:'A'|'B') {const dir=join(root,role);if(!existsSync(dir))mkdirSync(dir,{mode:0o700});return new Endpoint(role,runId,dir,r.open(role),async(sequence:number,history:any[])=>{seen.push({role,sequence,history:structuredClone(history)});return {text:`Generated ${role} ${sequence}: ${history.at(-1)?.text??'first question'}`,threadId:randomUUID(),inputTokens:10,outputTokens:10};});}
    return {a:endpoint('A'),b:endpoint('B'),make:endpoint,root,r,seen,runId};
}
test('two endpoints recover private identities, review introductions, converse and verify receipts',async t=>{
    const {a,b,r,seen}=await pair(t);
    try{
        const ai=await a.setup(),bi=await b.setup();a.peer(bi);b.peer(ai);await a.introduce();await b.accept();
        for(let n=1;n<=6;n++){
            await (n%2?a:b).step(n);
            if(n===2){
                assert.equal(r.messages.filter(m=>m.state==='pending').length,1);
                await Promise.all([a.reconnect(),b.reconnect()]);
                assert.equal(r.messages.filter(m=>m.state==='pending').length,1);
            }
        }
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
test('a reconnect with changed private address ownership fails closed before processing',async t=>{
    const {a,b,r}=await pair(t);
    try{
        const ai=await a.setup(),bi=await b.setup();a.peer(bi);b.peer(ai);await a.introduce();await b.accept();
        await a.step(1);await b.step(2);
        const original=(a as any).open;
        (a as any).open=async()=>{
            const client=await original(),call=client.call;
            client.call=async(name:string,args:any)=>name==='list_addresses'
                ? {items:[{accountId:B,addressId:ai.addressId,isPublic:false}],nextCursor:null} : call(name,args);
            return client;
        };
        await assert.rejects(a.reconnect(),/reconnected_identity_mismatch/);
        assert.equal(r.calls.filter(x=>x.name==='register_account').length,4);
        assert.equal(r.calls.filter(x=>x.name==='send_message').length,2);
        assert.equal(r.messages.filter(m=>m.state==='pending').length,1);
    }finally{await a.close();await b.close();}
});
test('reconnect rejects a changed private address lifetime and closes the rejected transport',async t=>{
    const {a,b,r}=await pair(t);
    try{
        const ai=await a.setup(),bi=await b.setup();a.peer(bi);b.peer(ai);await a.introduce();await b.accept();
        await a.step(1);await b.step(2);
        const original=(a as any).open;let rejectedCloses=0;
        (a as any).open=async()=>{
            const client=await original(),call=client.call,close=client.close;
            client.call=async(name:string,args:any)=>{
                const response=await call(name,args);
                if(name!=='list_addresses')return response;
                return {...response,items:response.items.map((address:any)=>address.addressId===ai.addressId
                    ? {...address,effectiveTo:'2026-10-24T00:00:00.000Z'}:address)};
            };
            client.close=async()=>{rejectedCloses++;await close();};
            return client;
        };
        const writesBefore=r.calls.filter(x=>['send_message','ack_message','register_account','create_address','request_contact','decide_contact'].includes(x.name)).length;
        await assert.rejects(a.reconnect(),/reconnected_identity_mismatch/);
        assert.equal(rejectedCloses,1);
        assert.equal(r.calls.filter(x=>['send_message','ack_message','register_account','create_address','request_contact','decide_contact'].includes(x.name)).length,writesBefore);
        assert.equal(r.messages.filter(m=>m.state==='pending').length,1);
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

test('a cold worker boundary restores both authenticated histories without replaying a pending delivery',async t=>{
    const {a,b,make,r,seen}=await pair(t);
    const ai=await a.setup(),bi=await b.setup();a.peer(bi);b.peer(ai);await a.introduce();await b.accept();
    await a.step(1);await b.step(2);
    assert.equal(r.messages.filter(m=>m.state==='pending').length,1);
    await Promise.all([a.close(),b.close()]);
    const freshA=make('A'),freshB=make('B');
    try{
        await Promise.all([freshA.restore(),freshB.restore()]);
        assert.equal(r.calls.filter(x=>x.name==='send_message').length,2);
        assert.equal(r.messages.filter(m=>m.state==='pending').length,1);
        for(let sequence=3;sequence<=6;sequence++)await(sequence%2?freshA:freshB).step(sequence);
        await freshA.consumeFinal();
        const {verifyEvidence}=await import('../endpoint.ts');
        assert.equal(verifyEvidence(await freshA.evidence(),await freshB.evidence()).messages.length,6);
        assert.deepEqual(seen.filter(s=>s.sequence===3).map(s=>s.history.map((h:any)=>h.sequence)),[[1,2]]);
        assert.ok(r.messages.every(m=>m.state==='acknowledged'));
    }finally{await Promise.all([freshA.close(),freshB.close()]);}
});
test('cold restore rejects unresolved writes before any network access or replay',async t=>{
    const {a,b,make,r,root}=await pair(t);
    const ai=await a.setup(),bi=await b.setup();a.peer(bi);b.peer(ai);await a.introduce();await b.accept();
    await a.step(1);await b.step(2);await Promise.all([a.close(),b.close()]);
    const path=join(root,'B','restart-checkpoint.json'),checkpoint=JSON.parse(readFileSync(path,'utf8'));
    const intent=join(root,'B',`intent-${checkpoint.index}.json`),old=JSON.parse(readFileSync(intent,'utf8'));
    const {save}=await import('../runtime.ts');
    save(intent,{...old,status:'pending'});
    const writes=r.calls.length;const freshB=make('B');
    try{await assert.rejects(freshB.restore(),/restart_unresolved_write/);assert.equal(r.calls.length,writes);}
    finally{await freshB.close();}
});
test('cold restore refuses a different owner for the retained private address',async t=>{
    const {a,b,make,r}=await pair(t);
    const ai=await a.setup(),bi=await b.setup();a.peer(bi);b.peer(ai);await a.introduce();await b.accept();
    await a.step(1);await b.step(2);await Promise.all([a.close(),b.close()]);
    const freshA=make('A'),original=(freshA as any).open;
    (freshA as any).open=async()=>{const c=await original(),call=c.call;c.call=async(name:string,args:any)=>name==='list_addresses'
        ? {items:[{accountId:B,addressId:ai.addressId,isPublic:false}],nextCursor:null}:call(name,args);return c;};
    const writes=r.calls.filter(x=>['register_account','send_message','ack_message','request_contact','decide_contact','create_address'].includes(x.name)).length;
    try{await assert.rejects(freshA.restore(),/restart_identity_mismatch/);}
    finally{await freshA.close();}
    assert.equal(r.calls.filter(x=>['register_account','send_message','ack_message','request_contact','decide_contact','create_address'].includes(x.name)).length,writes);
});

test('final evidence rejects an address that changed after the message exchange',async t=>{
    for(const changed of [
        {name:'public exposure',patch:{isPublic:true}},
        {name:'new expiry bound',patch:{effectiveTo:'2026-10-25T00:00:00.000Z'}}
    ])await t.test(changed.name,async subtest=>{
        const {a,b,r,root}=await pair(subtest);
        try{
            const ai=await a.setup(),bi=await b.setup();a.peer(bi);b.peer(ai);
            await a.introduce();await b.accept();
            for(let n=1;n<=6;n++)await (n%2?a:b).step(n);
            await a.consumeFinal();
            // Simulate a remote address change *after* the six acknowledged messages.
            // The final evidence gate must read the current address rather than asserting a hard-coded true.
            const client=(a as any).client,call=client.call.bind(client);
            client.call=async(name:string,args:any)=>{
                const result=await call(name,args);
                if(name!=='list_addresses')return result;
                return {...result,items:result.items.map((address:any)=>address.addressId===ai.addressId
                    ? {...address,...changed.patch}:address)};
            };
            const writes=()=>r.calls.filter(x=>['send_message','ack_message','register_account','create_address','request_contact','decide_contact'].includes(x.name)).length;
            const before=writes();
            await assert.rejects(a.evidence(),/evidence_address_mismatch/);
            assert.equal(writes(),before,'final evidence validation must be read-only');
            assert.equal(existsSync(join(root,'A','evidence.json')),false,'must not save false private-address evidence');
        }finally{await Promise.all([a.close(),b.close()]);}
    });
});

test('final evidence refuses a revoked peer contact on either independent endpoint',async t=>{
    for(const role of ['A','B'] as const)await t.test(role,async subtest=>{
        const {a,b,r,root}=await pair(subtest);
        try{
            const ai=await a.setup(),bi=await b.setup();a.peer(bi);b.peer(ai);
            await a.introduce();await b.accept();
            for(let sequence=1;sequence<=6;sequence++)await (sequence%2?a:b).step(sequence);
            await a.consumeFinal();
            // Revoke only the tested endpoint's contact *after* the successful exchange.
            const endpoint=role==='A'?a:b,client=(endpoint as any).client;
            const original=client.call.bind(client);
            client.call=async(name:string,args:any)=>{
                const result=await original(name,args);
                return name==='get_contact'?{...result,canSend:false}:result;
            };
            const writes=()=>r.calls.filter(x=>['send_message','ack_message','register_account','create_address','request_contact','decide_contact'].includes(x.name)).length;
            const before=writes();
            await assert.rejects(endpoint.evidence(),/evidence_contact_mismatch/);
            assert.equal(writes(),before,'evidence revalidation must not create writes');
            assert.equal(existsSync(join(root,role,'evidence.json')),false,'revoked contact must not produce successful evidence');
        }finally{await Promise.all([a.close(),b.close()]);}
    });
});

test('final evidence rejects a replaced contact generation despite accepted contact and old ACKs',async t=>{
    for(const role of ['A','B'] as const)await t.test(role,async subtest=>{
        const {a,b,r,root}=await pair(subtest);
        try{
            const ai=await a.setup(),bi=await b.setup();a.peer(bi);b.peer(ai);
            await a.introduce();await b.accept();
            for(let sequence=1;sequence<=6;sequence++)await (sequence%2?a:b).step(sequence);
            await a.consumeFinal();
            // Model a revoked then reaccepted relationship: canSend is true, but this is a new channel.
            const endpoint=role==='A'?a:b,client=(endpoint as any).client;
            const original=client.call.bind(client);
            client.call=async(name:string,args:any)=>{
                const result=await original(name,args);
                return name==='get_channel'?{...result,generation:'2'}:result;
            };
            const writes=()=>r.calls.filter(x=>['send_message','ack_message','register_account','create_address','request_contact','decide_contact'].includes(x.name)).length;
            const before=writes();
            await assert.rejects(endpoint.evidence(),/evidence_channel_generation_mismatch/);
            assert.equal(writes(),before,'final generation proof must not write or replay messages');
            assert.equal(existsSync(join(root,role,'evidence.json')),false,'replaced channel must not produce success evidence');
        }finally{await Promise.all([a.close(),b.close()]);}
    });
});

test('cold restore refuses a replaced channel before processing or sending on either endpoint',async t=>{
    for(const role of ['A','B'] as const)await t.test(role,async subtest=>{
        const {a,b,make,r}=await pair(subtest);
        const ai=await a.setup(),bi=await b.setup();a.peer(bi);b.peer(ai);
        await a.introduce();await b.accept();await a.step(1);await b.step(2);
        await Promise.all([a.close(),b.close()]);
        const fresh=make(role),original=(fresh as any).open;
        let rejectedCloses=0;
        (fresh as any).open=async()=>{
            const client=await original(),call=client.call.bind(client),close=client.close.bind(client);
            client.call=async(name:string,args:any)=>{
                const result=await call(name,args);
                return name==='get_channel'?{...result,generation:'2'}:result;
            };
            client.close=async()=>{rejectedCloses++;await close();};
            return client;
        };
        const writes=()=>r.calls.filter(x=>['register_account','create_address','request_contact','decide_contact','send_message','ack_message'].includes(x.name)).length;
        const before=writes();
        try{
            await assert.rejects(fresh.restore(),/restart_channel_generation_mismatch/);
            assert.equal(writes(),before,'a changed channel must be rejected without writes or replay');
            assert.equal(rejectedCloses,1,'a rejected restored channel must close its new MCP transport');
        }finally{await fresh.close();}
    });
});
