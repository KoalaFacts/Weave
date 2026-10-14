import { getPlatformProxy } from 'wrangler';
import { initializeDatabase, createMissingSchema, planLatestSchema } from './latest-schema.mjs';
import { normalizeRecords } from './data-migrations/normalize-records.mjs';
import { nowUtc } from '../src/time.ts';

const command=process.argv[2];
if(!['initialize','plan','apply','convert'].includes(command))throw Error('Use initialize, plan, apply or convert. Stop local dev before changing storage. This command never uses remote bindings.');
const proxy=await getPlatformProxy({configPath:new URL('../wrangler.jsonc',import.meta.url).pathname,remoteBindings:false,envFiles:[]});
try{
    const db=proxy.env.db;
    if(command==='initialize')await initializeDatabase(db,nowUtc());
    else if(command==='convert')console.log(JSON.stringify(await normalizeRecords(db)));
    else if(command==='plan')console.log(JSON.stringify(await planLatestSchema(db),null,2));
    else console.log('Applied missing schema objects: '+await createMissingSchema(db));
}finally{await proxy.dispose();}
