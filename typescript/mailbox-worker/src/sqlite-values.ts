import { sql, type AnyColumn } from 'drizzle-orm';
import { customType } from 'drizzle-orm/sqlite-core';

// D1 returns INTEGER as Number unless the SELECT explicitly casts it to TEXT.
// Bind exact integers as decimal strings; SQLite's INTEGER affinity preserves them.
export const exactInteger = customType<{data:bigint;driverData:string}>({
    dataType: () => 'integer',
    toDriver: value => value.toString(),
    fromDriver: value => {
        if (typeof value !== 'string' || !/^-?\d+$/.test(value)) throw Error('Exact integer SELECT must cast to TEXT.');
        return BigInt(value);
    }
});
export const opaqueBytes = customType<{data:Uint8Array;driverData:ArrayBuffer|number[]}>({
    dataType: () => 'blob',
    toDriver: value => Uint8Array.from(value).buffer,
    fromDriver: value => value instanceof ArrayBuffer ? new Uint8Array(value) : Uint8Array.from(value)
});
export const exact = (column: AnyColumn) => sql<string | null>`CAST(${column} AS TEXT)`;
