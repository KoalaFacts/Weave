import { atomic, type Env } from './database';
import { pruneIdempotency } from './mcp-idempotency.ts';
import { cleanup, type CleanupPolicy } from './limits.ts';

// D1 is also bounded by 48 statements/request: at most two 64-message transactions per scheduled round.
const BATCH_SIZE = 64, MAX_BATCHES = 2;
export async function sweep(env: Env,scheduled=false,policy: CleanupPolicy=cleanup): Promise<{messages:number;batches:number}> {
    const deadline=performance.now()+policy.milliseconds;
    let messages=0,batches=0;
    while(messages<policy.maximum&&batches<(scheduled?MAX_BATCHES:1)&&performance.now()<deadline){
        const first=batches===0,limit=Math.min(BATCH_SIZE,policy.maximum-messages);
        const count=await atomic(env.db,async tx=>{
            const now=Date.now(),previous=first?await tx.get('maintenance','','sweep'):null;
            if(first&&previous&&now-previous.at<60000)return 0;
            if(performance.now()>=deadline)return 0;
            const before=(BigInt(now-7*86400000)*10000n).toString(),nowTicks=(BigInt(now)*10000n).toString();
            const rows=(await tx.db.prepare(`SELECT CAST(m.seq AS TEXT) seq FROM mailbox_records m JOIN message_delivery d ON d.message_seq=m.seq WHERE
                (d.state='pending' AND m.expires_at<=CAST(? AS INTEGER)) OR
                (d.state IN ('acknowledged','expired','blocked') AND (m.payload IS NOT NULL OR d.terminal_at<=CAST(? AS INTEGER)))
                ORDER BY m.seq LIMIT ?`).bind(nowTicks,before,limit).all<{seq:string}>()).results;
            if(performance.now()>=deadline)return 0;
            if(rows.length){
                const ids=rows.map(row=>row.seq),marks=ids.map(()=>'?').join(',');
                tx.writes.push(tx.db.prepare(`UPDATE mailbox_records_legacy SET data=json_set(data,'$.payload',NULL,'$.size',0),payload_cleared_at=?
                    WHERE seq IN (${marks}) AND kind='message' AND json_extract(data,'$.payload') IS NOT NULL`).bind(nowTicks,...ids));
                tx.writes.push(tx.db.prepare(`UPDATE message_delivery SET state='expired',terminal_at=(SELECT expires_at FROM mailbox_records WHERE seq=message_seq),updated_at=?
                    WHERE message_seq IN (${marks}) AND state='pending'`).bind(nowTicks,...ids));
                tx.writes.push(tx.db.prepare(`UPDATE mailbox_records SET payload=NULL,payload_size=0,updated_at=? WHERE seq IN (${marks})`).bind(nowTicks,...ids));
                tx.writes.push(tx.db.prepare(`DELETE FROM mailbox_records WHERE seq IN (${marks}) AND seq IN
                    (SELECT message_seq FROM message_delivery WHERE state IN ('acknowledged','expired','blocked') AND terminal_at<=CAST(? AS INTEGER))`).bind(...ids,before));
            }
            if(first){
                pruneIdempotency(tx,BigInt(now)*10000n);
                tx.put('maintenance','','sweep',{at:now});
            }
            return rows.length;
        });
        messages+=count;batches++;
        if(count<limit)break;
    }
    return {messages,batches};
}
