export const nowUtc = () => BigInt(Date.now()) * 10000n;
export const exactInt64 = (value: string | bigint): bigint => {
    if (typeof value !== 'bigint' && (typeof value !== 'string' || !/^-?\d+$/.test(value))) throw RangeError('Expected an exact decimal integer.');
    const result=BigInt(value);
    if (result < -9223372036854775808n || result > 9223372036854775807n) throw RangeError('Integer exceeds SQLite int64.');
    return result;
};
export function utcTicks(value: string): bigint {
    const match=/^(\d{4})-(\d\d)-(\d\d)T(\d\d):(\d\d):(\d\d)(?:\.(\d{1,7}))?(Z|[+-]\d\d:\d\d)$/.exec(value);
    if (!match) throw RangeError('Invalid timestamp.');
    const [year,month,day,hour,minute,second]=match.slice(1,7).map(Number);
    const leap=year%4===0 && (year%100!==0 || year%400===0);
    const days=[31,leap?29:28,31,30,31,30,31,31,30,31,30,31];
    const zone=match[8];
    if(year<1||month<1||month>12||day<1||day>days[month-1]||hour>23||minute>59||second>59 ||
        zone!=='Z'&&(Number(zone.slice(1,3))>14||Number(zone.slice(4))>59||Number(zone.slice(1,3))===14&&Number(zone.slice(4))!==0)) throw RangeError('Invalid timestamp.');
    const milliseconds=Date.parse(value);
    if(!Number.isFinite(milliseconds))throw RangeError('Invalid timestamp.');
    return exactInt64(BigInt(milliseconds)*10000n+BigInt((match[7]??'').padEnd(7,'0').slice(3)));
}
export function isoUtc(value: bigint | string): string {
    const ticks=exactInt64(value),seconds=ticks/10000000n-(ticks<0n&&ticks%10000000n!==0n?1n:0n);
    const fraction=ticks-seconds*10000000n;
    // Date is used only at the wire boundary; the full 100ns remainder stays exact.
    const prefix=new Date(Number(seconds*1000n)).toISOString().slice(0,19);
    return prefix+'.'+fraction.toString().padStart(7,'0')+'Z';
}
export function epochSeconds(ticks: bigint = nowUtc()): number {
    const seconds=ticks/10000000n;
    return Number(ticks<0n&&ticks%10000000n!==0n?seconds-1n:seconds);
}
