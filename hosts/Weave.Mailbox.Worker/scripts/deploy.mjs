import { readFile, writeFile, mkdtemp, rm } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { spawnSync } from 'node:child_process';

const directory=resolve(dirname(fileURLToPath(import.meta.url)),'..');
const expectedSubdomain='weave-389';
export function deploymentConfig(base,env,root=directory){
    if(base.name!=='demo'||base.d1_databases?.length!==1||base.d1_databases[0].binding!=='db')throw Error('Expected existing Worker demo and binding db.');
    if(!/^[a-f0-9]{32}$/i.test(env.CLOUDFLARE_ACCOUNT_ID??''))throw Error('CLOUDFLARE_ACCOUNT_ID must identify the existing account.');
    // Business sweeps stay disabled for this demo, including with inherited environment values.
    const vars={...base.vars,CLEANUP_MAX_MESSAGES:'0',CLEANUP_TIME_BUDGET_MS:'0'};
    return {...base,account_id:env.CLOUDFLARE_ACCOUNT_ID,main:resolve(root,base.main),workers_dev:true,vars,
        d1_databases:[{...base.d1_databases[0],migrations_dir:resolve(root,base.d1_databases[0].migrations_dir)}]};
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
    const bindings=Array.isArray(worker.bindings)?worker.bindings.filter(binding=>binding?.name==='db'):[];
    const binding=bindings[0];
    if(bindings.length!==1||binding?.type!=='d1'||!/^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i.test(binding.id??'')||binding.id==='00000000-0000-0000-0000-000000000000')throw Error('Expected one valid existing D1 binding named db.');
    if(worker.bindings.some(binding=>!['plain_text','json','secret_text'].includes(binding?.type)&&!(binding?.type==='d1'&&binding.name==='db')))throw Error('Unexpected existing resource bindings; refusing to overwrite them.');
    const database=await inspect('/d1/database/'+binding.id,'Existing D1 database');
    if(database.uuid!==binding.id||typeof database.name!=='string'||!/^[a-zA-Z0-9_-]+$/.test(database.name))throw Error('Existing D1 identity does not match its Worker binding or has an invalid name.');
    config.d1_databases[0].database_id=database.uuid;
    config.d1_databases[0].database_name=database.name;
    const temporary=await mkdtemp(join(root,'.deploy-'));
    try{
        const configPath=join(temporary,'wrangler.json');await writeFile(configPath,JSON.stringify(config,null,2),{mode:0o600});
        const execute=run??(args=>spawnSync(process.execPath,[join(root,'node_modules/wrangler/bin/wrangler.js'),...args],{cwd:root,env:{...env,CI:'true',NO_COLOR:'1',WRANGLER_SEND_METRICS:'false'},encoding:'utf8',maxBuffer:16*1024*1024}));
        function command(args){const result=execute([...args,'--config',configPath]);if(result.stdout)process.stdout.write(result.stdout);if(result.stderr)process.stderr.write(result.stderr);if(result.status!==0)throw Error('Wrangler '+args.slice(0,3).join(' ')+' failed; stopping before the next operation.');return result.stdout??'';}
        command(['d1','migrations','list','db','--remote']);
        command(['d1','migrations','apply','db','--remote']);
        if(!noPendingMigrations(command(['d1','migrations','list','db','--remote'])))throw Error('Pending migration state is not empty; deployment stopped.');
        command(['deploy','--keep-vars']);
        const deployed=await inspect('/workers/scripts/demo/settings','Worker settings','deployment already completed; settings unverified');
        for(const name of ['CLEANUP_MAX_MESSAGES','CLEANUP_TIME_BUDGET_MS']){
            if(!Array.isArray(deployed.bindings)||!deployed.bindings.some(binding=>binding?.name===name&&binding.type==='plain_text'&&binding.text==='0'))throw Error('Deployment completed, but '+name+' is not confirmed disabled; no automatic rollback attempted.');
        }
        for(const name of ['REGISTRATION_ENABLED','REGISTRATION_MAX_ACCOUNTS','REGISTRATION_PER_IP_PER_HOUR','REGISTRATION_GLOBAL_PER_HOUR']){
            const bindings=deployed.bindings.filter(binding=>binding?.name===name);
            if(bindings.length!==1||bindings[0].type!=='plain_text'||bindings[0].text!==config.vars[name])throw Error('Deployment completed, but '+name+' is not confirmed against configuration; no automatic rollback attempted.');
        }
        console.log('Verified deployed demo business cleanup: CLEANUP_MAX_MESSAGES=0, CLEANUP_TIME_BUDGET_MS=0.');
        console.log('Verified deployed demo registration and account limits match the checked-in configuration.');
    }finally{await rm(temporary,{recursive:true,force:true});}
}
if(process.argv[1]&&import.meta.url===pathToFileURL(resolve(process.argv[1])).href){
    try{await deploy();}catch(error){console.error(error.message);process.exitCode=1;}
}
