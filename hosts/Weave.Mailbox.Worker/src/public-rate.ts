import { atomic, type Env } from './database';
import { fail, hash } from './wire';

// Shared by modern registry and legacy public-card routes; no credential or raw IP is stored.
// Cloudflare supplies CF-Connecting-IP at the deployed ingress; local callers can spoof it.
export async function publicReadRate(env: Env,request: Request): Promise<void> {
    const source=await hash('public-read:'+ (request.headers.get('CF-Connecting-IP')??'unknown'));
    await atomic(env.db,async tx=>{
        const now=Date.now();let retryAfter=0;
        for(const [id,maximum] of [['global',2000],[source,120]] as const){
            const previous=await tx.get('public-rate','',id),current=previous&&now-previous.at<60000;
            const count=current?previous.count:0;
            if(count>=maximum)retryAfter=Math.max(retryAfter,Math.max(1,Math.ceil((previous.at+60000-now)/1000)));
            else tx.put('public-rate','',id,{at:current?previous.at:now,count:count+1});
        }
        if(retryAfter)fail(429,'capacity',retryAfter);
        tx.writes.push(tx.db.prepare("DELETE FROM mailbox_records WHERE seq IN (SELECT seq FROM mailbox_records WHERE kind='public-rate' AND json_extract(data,'$.at')<? ORDER BY seq LIMIT 128)").bind(now-120000));
    });
}
