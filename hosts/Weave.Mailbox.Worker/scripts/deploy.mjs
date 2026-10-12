import { readFile, writeFile, mkdtemp, rm } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { spawnSync } from 'node:child_process';

const directory=resolve(dirname(fileURLToPath(import.meta.url)),'..');
const expectedSubdomain='weave-389';
export function deploymentConfig(base,env,root=directory){
    if(base.name!=='demo'||base.d1_databases?.length!==1||base.d1_databases[0].binding!=='db')throw Error('Expected existing Worker demo and binding db.');
    if(!/^[a-f0-9]{32}$/i.test(env.CLOUDFLARE_ACCOUNT_ID??''))throw Error('CLOUDFLARE_ACCOUNT_ID must identify the existing account.');
    if(!/^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i.test(env.WEAVE_D1_DATABASE_ID??'')||env.WEAVE_D1_DATABASE_ID==='00000000-0000-0000-0000-000000000000')throw Error('WEAVE_D1_DATABASE_ID must identify the existing database.');
    if(!/^[a-zA-Z0-9_-]+$/.test(env.WEAVE_D1_DATABASE_NAME??''))throw Error('WEAVE_D1_DATABASE_NAME is required.');
    const registration=env.WEAVE_REGISTRATION_ENABLED||'false';
    if(!['true','false'].includes(registration))throw Error('WEAVE_REGISTRATION_ENABLED must be true or false.');
    // Business sweeps stay disabled for this demo, including with inherited environment values.
    const vars={...base.vars,REGISTRATION_ENABLED:registration,CLEANUP_MAX_MESSAGES:'0',CLEANUP_TIME_BUDGET_MS:'0'};
    if(env.WEAVE_HOST){
        let url;try{url=new URL(env.WEAVE_HOST);}catch{throw Error('WEAVE_HOST must be an HTTPS origin.');}
        if(url.protocol!=='https:'||url.username||url.password||url.pathname!=='/'||url.search||url.hash)throw Error('WEAVE_HOST must be an HTTPS origin.');
        vars.WEAVE_HOST=url.origin;
    }
    return {...base,account_id:env.CLOUDFLARE_ACCOUNT_ID,main:resolve(root,base.main),workers_dev:true,vars,
        d1_databases:[{...base.d1_databases[0],database_id:env.WEAVE_D1_DATABASE_ID,database_name:env.WEAVE_D1_DATABASE_NAME,migrations_dir:resolve(root,base.d1_databases[0].migrations_dir)}]};
}
export function noPendingMigrations(output){return /(?:^|\n)(?:✅ )?No migrations to apply!\s*(?:\n|$)/u.test(output);}
export async function deploy({env=process.env,fetchImpl=fetch,run,root=directory}={}){
    const base=JSON.parse(await readFile(join(root,'wrangler.jsonc'),'utf8')),config=deploymentConfig(base,env,root);
    if(!env.CLOUDFLARE_API_TOKEN)throw Error('CLOUDFLARE_API_TOKEN is required; its value is never written to config.');
    const apiRoot='https://api.cloudflare.com/client/v4/accounts/'+config.account_id;
    async function inspect(path,label,state='no mutation attempted'){
        let response;try{response=await fetchImpl(apiRoot+path,{headers:{Authorization:'Bearer '+env.CLOUDFLARE_API_TOKEN},redirect:'error'});}catch{throw Error(label+' lookup failed; '+state+'.');}
        if(!response.ok)throw Error(label+' lookup failed (HTTP '+response.status+'); '+state+'.');
        let body;try{body=await response.json();}catch{throw Error(label+' lookup returned invalid JSON; '+state+'.');}if(body?.success!==true||!body.result)throw Error(label+' lookup returned an invalid result; '+state+'.');return body.result;
    }
    const subdomain=await inspect('/workers/subdomain','Account subdomain');
    if(subdomain.subdomain!==expectedSubdomain)throw Error('Account does not own the authorized weave-389.workers.dev subdomain.');
    const worker=await inspect('/workers/scripts/demo/settings','Existing Worker demo');
    const binding=worker.bindings?.find(binding=>binding.name==='db'&&binding.type==='d1');
    if(binding?.id!==config.d1_databases[0].database_id)throw Error('Existing Worker db binding does not match the configured D1 ID.');
    if(worker.bindings.some(binding=>!['plain_text','json','secret_text'].includes(binding.type)&&!(binding.type==='d1'&&binding.name==='db')))throw Error('Unexpected existing resource bindings; refusing to overwrite them.');
    const database=await inspect('/d1/database/'+config.d1_databases[0].database_id,'Existing D1 database');
    if(database.uuid!==config.d1_databases[0].database_id||database.name!==config.d1_databases[0].database_name)throw Error('Existing D1 identity does not match configuration.');
    const temporary=await mkdtemp(join(root,'.deploy-'));
    try{
        const configPath=join(temporary,'wrangler.json');await writeFile(configPath,JSON.stringify(config,null,2),{mode:0o600});
        const execute=run??(args=>spawnSync(process.execPath,[join(root,'node_modules/wrangler/bin/wrangler.js'),...args],{cwd:root,env:{...env,CI:'true',NO_COLOR:'1',WRANGLER_SEND_METRICS:'false'},encoding:'utf8',maxBuffer:16*1024*1024}));
        function command(args){const result=execute([...args,'--config',configPath]);if(result.stdout)process.stdout.write(result.stdout);if(result.stderr)process.stderr.write(result.stderr);if(result.status!==0)throw Error('Wrangler '+args.slice(0,3).join(' ')+' failed; stopping before the next operation.');return result.stdout??'';}
        command(['d1','migrations','list','db','--remote']);
        command(['d1','migrations','apply','db','--remote']);
        if(!noPendingMigrations(command(['d1','migrations','list','db','--remote'])))throw Error('Pending migration state is not empty; deployment stopped.');
        command(['deploy','--keep-vars']);
        const deployed=await inspect('/workers/scripts/demo/settings','Worker cleanup settings','deployment already completed; cleanup settings unverified');
        for(const name of ['CLEANUP_MAX_MESSAGES','CLEANUP_TIME_BUDGET_MS']){
            if(!Array.isArray(deployed.bindings)||!deployed.bindings.some(binding=>binding?.name===name&&binding.type==='plain_text'&&binding.text==='0'))throw Error('Deployment completed, but '+name+' is not confirmed disabled; no automatic rollback attempted.');
        }
        console.log('Verified deployed demo business cleanup: CLEANUP_MAX_MESSAGES=0, CLEANUP_TIME_BUDGET_MS=0.');
    }finally{await rm(temporary,{recursive:true,force:true});}
}
if(process.argv[1]&&import.meta.url===pathToFileURL(resolve(process.argv[1])).href){
    try{await deploy();}catch(error){console.error(error.message);process.exitCode=1;}
}
