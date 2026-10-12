import { atomic, type Env } from './database';
import { fail } from './wire';

export const CLEANUP_HARD_CAP = 100000;
// D1 is also bounded by 48 statements/request. Two 64-message transactions leave
// headroom; the 100k policy cap is not a promise to process 100k on this Worker.
const BATCH_SIZE = 64, MAX_BATCHES = 2;
export function cleanupPolicy(env: Env): {maximum:number;milliseconds:number} {
    const number=(text:string|undefined,fallback:number,max:number) => {
        if(text===undefined)return fallback;
        if(!/^\d+$/.test(text)||Number(text)>max)fail(503,'storage');
        return Number(text);
    };
    return {maximum:number(env.CLEANUP_MAX_MESSAGES,128,CLEANUP_HARD_CAP),milliseconds:number(env.CLEANUP_TIME_BUDGET_MS,2000,10000)};
}
export async function sweep(env: Env,scheduled=false): Promise<{messages:number;batches:number}> {
    const policy=cleanupPolicy(env),deadline=performance.now()+policy.milliseconds;
    let messages=0,batches=0;
    while(messages<policy.maximum&&batches<(scheduled?MAX_BATCHES:1)&&performance.now()<deadline){
        const first=batches===0,limit=Math.min(BATCH_SIZE,policy.maximum-messages);
        const count=await atomic(env.db,async tx=>{
            const now=Date.now(),previous=first?await tx.get('maintenance','','sweep'):null;
            if(first&&previous&&now-previous.at<60000)return 0;
            if(performance.now()>=deadline)return 0;
            const before=new Date(now-7*86400000).toISOString(),nowTicks=(BigInt(now)*10000n).toString();
            // Fetch only sequence IDs, never message bodies. A successful transaction removes
            // these rows from eligibility; reruns resume oldest eligible rows without skips.
            const rows=(await tx.db.prepare(`SELECT CAST(seq AS TEXT) seq FROM mailbox_records WHERE kind='message' AND (
                (json_extract(data,'$.receipt.state')='pending' AND CAST(json_extract(data,'$.expiresTicks') AS INTEGER)<=CAST(? AS INTEGER)) OR
                (json_extract(data,'$.receipt.state') IN ('acknowledged','expired','blocked') AND
                    (json_extract(data,'$.payload') IS NOT NULL OR julianday(json_extract(data,'$.receipt.terminalAt'))<=julianday(?))))
                ORDER BY seq LIMIT ?`).bind(nowTicks,before,limit).all<{seq:string}>()).results;
            if(performance.now()>=deadline)return 0;
            if(rows.length){
                const ids=rows.map(row=>row.seq),marks=ids.map(()=>'?').join(',');
                tx.writes.push(tx.db.prepare(`UPDATE mailbox_records SET data=json_set(data,'$.receipt.state','expired',
                    '$.receipt.terminalAt',json_extract(data,'$.receipt.expiresAt'),'$.payload',NULL,'$.size',0)
                    WHERE seq IN (${marks}) AND json_extract(data,'$.receipt.state')='pending'
                    AND CAST(json_extract(data,'$.expiresTicks') AS INTEGER)<=CAST(? AS INTEGER)`).bind(...ids,nowTicks));
                tx.writes.push(tx.db.prepare(`UPDATE mailbox_records SET data=json_set(data,'$.payload',NULL,'$.size',0)
                    WHERE seq IN (${marks}) AND json_extract(data,'$.receipt.state') IN ('acknowledged','expired','blocked')`).bind(...ids));
                tx.writes.push(tx.db.prepare(`DELETE FROM mailbox_records WHERE seq IN (${marks})
                    AND json_extract(data,'$.receipt.state') IN ('acknowledged','expired','blocked')
                    AND julianday(json_extract(data,'$.receipt.terminalAt'))<=julianday(?)`).bind(...ids,before));
            }
            if(first){
                tx.writes.push(tx.db.prepare(`UPDATE mailbox_records SET data=json_set(data,'$.terminalAt',json_extract(data,'$.summary.expiresAt'))
                    WHERE seq IN (SELECT seq FROM mailbox_records WHERE kind='request' AND json_extract(data,'$.summary.status') IN ('pending','needsAction')
                    AND json_extract(data,'$.terminalAt') IS NULL AND CAST(json_extract(data,'$.expiresTicks') AS INTEGER)<=CAST(? AS INTEGER) ORDER BY seq LIMIT 128)`).bind(nowTicks));
                const purge=`SELECT owner,id FROM mailbox_records WHERE kind='request' AND json_extract(data,'$.terminalAt') IS NOT NULL
                    AND julianday(json_extract(data,'$.terminalAt'))<=julianday(?) ORDER BY seq LIMIT 128`;
                tx.writes.push(tx.db.prepare(`UPDATE mailbox_records SET data=json_set(data,'$.currentRequest',NULL) WHERE kind='channel'
                    AND (json_extract(data,'$.currentRequest.requesterMailboxId'),json_extract(data,'$.currentRequest.requestId')) IN (${purge})`).bind(before));
                tx.writes.push(tx.db.prepare(`DELETE FROM mailbox_records WHERE kind='request' AND (owner,id) IN (${purge})`).bind(before));
                tx.put('maintenance','','sweep',{at:now});
            }
            return rows.length;
        });
        messages+=count;batches++;
        if(count<limit)break;
    }
    return {messages,batches};
}
