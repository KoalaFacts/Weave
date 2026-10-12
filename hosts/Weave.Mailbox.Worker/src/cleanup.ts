import { atomic, type Env } from './database';

// Opportunistic, bounded cleanup; no remote scheduler or background resources required.
// It commits independently of the following user operation, so capacity can recover.
export async function sweep(env: Env): Promise<void> {
    await atomic(env.db,async tx => {
        const now = Date.now(), previous = await tx.get('maintenance','','sweep');
        if (previous && now-previous.at < 60000) return;
        const before = new Date(now-7*86400000).toISOString(), nowTicks = (BigInt(now)*10000n).toString();
        tx.writes.push(tx.db.prepare(`UPDATE mailbox_records SET data=json_set(data,'$.receipt.state','expired',
            '$.receipt.terminalAt',json_extract(data,'$.receipt.expiresAt'),'$.payload',NULL,'$.size',0)
            WHERE seq IN (SELECT seq FROM mailbox_records WHERE kind='message' AND json_extract(data,'$.receipt.state')='pending'
                AND CAST(json_extract(data,'$.expiresTicks') AS INTEGER)<=CAST(? AS INTEGER) ORDER BY seq LIMIT 128)`).bind(nowTicks));
        tx.writes.push(tx.db.prepare(`DELETE FROM mailbox_records WHERE seq IN (SELECT seq FROM mailbox_records WHERE kind='message'
            AND json_extract(data,'$.receipt.terminalAt') IS NOT NULL AND julianday(json_extract(data,'$.receipt.terminalAt'))<=julianday(?) ORDER BY seq LIMIT 128)`).bind(before));
        tx.writes.push(tx.db.prepare(`UPDATE mailbox_records SET data=json_set(data,'$.terminalAt',json_extract(data,'$.summary.expiresAt'))
            WHERE seq IN (SELECT seq FROM mailbox_records WHERE kind='request' AND json_extract(data,'$.summary.status') IN ('pending','needsAction')
            AND json_extract(data,'$.terminalAt') IS NULL AND CAST(json_extract(data,'$.expiresTicks') AS INTEGER)<=CAST(? AS INTEGER) ORDER BY seq LIMIT 128)`).bind(nowTicks));
        // Clear only references to the same terminal requests selected for purge.
        const purge = `SELECT owner,id FROM mailbox_records WHERE kind='request' AND json_extract(data,'$.terminalAt') IS NOT NULL
            AND julianday(json_extract(data,'$.terminalAt'))<=julianday(?) ORDER BY seq LIMIT 128`;
        tx.writes.push(tx.db.prepare(`UPDATE mailbox_records SET data=json_set(data,'$.currentRequest',NULL) WHERE kind='channel'
            AND (json_extract(data,'$.currentRequest.requesterMailboxId'),json_extract(data,'$.currentRequest.requestId')) IN (${purge})`).bind(before));
        tx.writes.push(tx.db.prepare(`DELETE FROM mailbox_records WHERE kind='request' AND (owner,id) IN (${purge})`).bind(before));
        tx.put('maintenance','','sweep',{at:now});
    });
}
