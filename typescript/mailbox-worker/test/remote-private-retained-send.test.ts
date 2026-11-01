import assert from 'node:assert/strict';
import {test} from 'node:test';
import {build} from 'esbuild';
import {start,payload} from './harness.ts';
import type {Env} from '../src/database.ts';

// Match the production Worker bundler-resolution boundary, as in boundaries.test.ts.
const built=await build({entryPoints:['src/remote-private.ts'],bundle:true,write:false,platform:'node',format:'esm',logLevel:'silent'});
const {executePrivateMcp}=await import('data:text/javascript;base64,'+Buffer.from(built.outputFiles[0].contents).toString('base64')) as typeof import('../src/remote-private.ts');
const origin='https://demo.test';

for(const replace of [false,true])for(const acknowledge of [false,true]){
    test(`real D1 retained MCP send survives ${replace?'replaced':'removed'} contact with ${acknowledge?'acknowledged':'pending'} delivery`,async()=>{
        const h=await start();
        try{
            const env:Env={db:h.db,REGISTRATION_ENABLED:'true',REGISTRATION_MAX_ACCOUNTS:'100',REGISTRATION_PER_IP_PER_HOUR:'10',
                REGISTRATION_GLOBAL_PER_HOUR:'100',CLEANUP_MAX_MESSAGES:'0',CLEANUP_TIME_BUDGET_MS:'0'};
            const a=await h.participant(),b=await h.participant(),c=await h.participant();
            const accept=async()=>{
                const intro=payload('request');
                const requested=await executePrivateMcp(env,origin,a.accountId,'request_contact',{
                    requesterAddressId:a.cardId,recipientAccountId:b.accountId,recipientAddressId:b.cardId,payload:intro
                });
                assert.equal(requested.status,200,await requested.clone().text());
                const state=await requested.json();
                const accepted=await executePrivateMcp(env,origin,b.accountId,'decide_contact',{
                    requestId:intro.messageId,requesterAccountId:a.accountId,expectedGeneration:String(state.generation),status:'accepted'
                });
                assert.equal(accepted.status,200,await accepted.clone().text());
                return String(state.generation);
            };
            const originalGeneration=await accept(),ordinary=payload('retained body');
            const input={recipientAccountId:b.accountId,contactGeneration:originalGeneration,payload:ordinary};
            const sent=await executePrivateMcp(env,origin,a.accountId,'send_message',input);
            assert.equal(sent.status,200,await sent.clone().text());
            if(acknowledge){
                const receipt=await executePrivateMcp(env,origin,b.accountId,'ack_message',{messageId:ordinary.messageId,senderAccountId:a.accountId});
                assert.equal(receipt.status,200,await receipt.clone().text());
                assert.equal((await receipt.json()).state,'acknowledged');
            }
            await h.ok('/v1/contacts/accounts/'+b.accountId,a,'DELETE');
            if(replace)assert.notEqual(await accept(),originalGeneration);
            // The canonical HTTP boundary still exposes the retained outcome.
            const retained=await h.ok('/v1/outbox/'+ordinary.messageId,a);
            assert.equal(retained.state,acknowledge?'acknowledged':'blocked');
            const snapshot=()=>h.db.prepare(`SELECT m.seq,m.payload_size,hex(m.payload) body,d.state,CAST(d.terminal_at AS TEXT) terminal_at,d.fingerprint
                FROM mailbox_records m JOIN message_delivery d ON d.message_seq=m.seq
                WHERE m.sender_account_id=? AND m.message_id=?`).bind(a.accountId,ordinary.messageId).first();
            const before=await snapshot();
            const countBefore=await h.db.prepare('SELECT count(*) n FROM mailbox_records').first('n');
            const duplicate=await executePrivateMcp(env,origin,a.accountId,'send_message',input);
            assert.equal(duplicate.status,200,'an unchanged committed send must return its retained receipt');
            assert.deepEqual(await duplicate.json(),retained);
            const again=await executePrivateMcp(env,origin,a.accountId,'send_message',input);
            assert.equal(again.status,200);assert.deepEqual(await again.json(),retained);
            for(const changed of [
                {...input,payload:{...ordinary,bytes:Buffer.from('changed body').toString('base64')}},
                {...input,contactGeneration:String(BigInt(originalGeneration)+1n)},
                {...input,recipientAccountId:c.accountId}
            ])assert.equal((await executePrivateMcp(env,origin,a.accountId,'send_message',changed)).status,409,'same ID cannot change payload, generation or recipient');
            assert.equal((await executePrivateMcp(env,origin,c.accountId,'send_message',input)).status,404,'another owner cannot retrieve or replay the retained send');
            assert.deepEqual(await snapshot(),before,'no retained body, receipt or fingerprint may be rewritten');
            assert.equal(await h.db.prepare('SELECT count(*) n FROM mailbox_records').first('n'),countBefore,'retry must not enqueue another message');
            const inbox=await executePrivateMcp(env,origin,b.accountId,'list_my_inbox',{});
            assert.equal(inbox.status,200);
            assert.ok(!(await inbox.json()).items.some((item:{envelope:{payload:{messageId:string}}})=>item.envelope.payload.messageId===ordinary.messageId),'terminal body must never reappear');
        }finally{await h.close();}
    });
}

