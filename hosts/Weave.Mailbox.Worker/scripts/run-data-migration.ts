import { copyFile, mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import type { StorageDatabase } from './latest-schema.ts';

interface MigrationOptions {now?:bigint;checkpoint?:(phase:string)=>Promise<void>;}
interface MigrationResult {
    alreadyCompleted:boolean;historyDigest:string|null;
    counts?:Record<string,number>;sourceDigest?:string;highWater?:string;
}
const root=join(dirname(fileURLToPath(import.meta.url)),'..');

export async function normalizeRecords(db:StorageDatabase,options?:MigrationOptions):Promise<MigrationResult> {
    const directory=await mkdtemp(join(root,'.historical-migration-'));
    try {
        await mkdir(join(directory,'scripts/data-migrations'),{recursive:true});
        await mkdir(join(directory,'src'));
        // The executed source hashes itself using its original relative paths.
        // Restore those names temporarily; never rewrite its bytes or ledger.
        const files=[
            ['scripts/data-migrations/normalize-records.ts','scripts/data-migrations/normalize-records.mjs'],
            ['scripts/data-migrations/legacy-records.ts','scripts/data-migrations/legacy-records.mjs'],
            ['src/time.ts','src/time.ts'],['src/credentials.ts','src/credentials.ts'],
            ['src/schema.ts','src/schema.ts'],['src/sqlite-values.ts','src/sqlite-values.ts']
        ];
        for(const [source,target] of files)await copyFile(join(root,source),join(directory,target));
        await writeFile(join(directory,'scripts/latest-schema.mjs'),
            'export {createMissingSchema,latestSchemaStatements} from '+JSON.stringify(new URL('./latest-schema.ts',import.meta.url).href)+';\n');
        const historical=await import(pathToFileURL(join(directory,'scripts/data-migrations/normalize-records.mjs')).href);
        return await historical.normalizeRecords(db,options);
    } finally {
        await rm(directory,{recursive:true,force:true});
    }
}
