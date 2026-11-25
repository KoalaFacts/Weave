import { build } from 'esbuild';
import type { Env } from '../src/database.ts';
// The production cleanup module with an enabled policy. The deployed policy (`cleanup` in src/limits.ts) is off,
// so tests that exercise the cleanup engine pass their own instead of a Worker setting.
const built=await build({entryPoints:['src/cleanup.ts'],bundle:true,write:false,platform:'node',format:'esm',logLevel:'silent'});
export const {sweep}=await import('data:text/javascript;base64,'+Buffer.from(built.outputFiles[0].contents).toString('base64')) as typeof import('../src/cleanup.ts');
export const enabledCleanup={maximum:128,milliseconds:2000};
/** One scheduled cleanup round with the enabled policy, as the Worker's cron would run it if cleanup were on. */
export const scheduledCleanup=(db:Env['db'],policy=enabledCleanup)=>sweep({db},true,policy);
