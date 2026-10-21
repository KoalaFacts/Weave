import {existsSync} from 'node:fs';
import {join} from 'node:path';
import {randomUUID} from 'node:crypto';
import {capture,ORIGIN,PairError,object,save,load} from './runtime.ts';

export type Op = (args:string[])=>Promise<string>;
const op:Op=args=>capture('op',args,{timeoutMs:30000});
const validId=(id:unknown):id is string=>typeof id==='string'&&/^[a-z0-9]{26}$/.test(id);
export function validateReference(ref:string):{vault:string;item:string} {
    const m=/^op:\/\/([a-z0-9]{26})\/([a-z0-9]{26})\/password$/.exec(ref);
    if(!m)throw new PairError('invalid_vault_reference');return {vault:m[1],item:m[2]};
}
export async function allocate(directory:string,vault:string,role:'A'|'B',execute:Op=op):Promise<string> {
    if(!validId(vault))throw new PairError('use_exact_vault_id');
    const path=join(directory,`vault-${role}.json`);
    if(existsSync(path)){
        const saved=load(path);if(saved.status!=='saved'||saved.vault!==vault||saved.origin!==ORIGIN)throw new PairError('vault_setup_requires_reconciliation');
        validateReference(saved.reference);return saved.reference;
    }
    const title=`Weave demo ${role} ${randomUUID()}`;
    save(path,{status:'creation_pending',vault,title,origin:ORIGIN});
    // op generates and durably saves the credential. Its JSON result is never printed or stored.
    let item:Record<string,any>;
    try{item=object(JSON.parse(await execute(['item','create','--category=Password',`--vault=${vault}`,`--title=${title}`,'--generate-password=64,letters,digits',`weave_origin[text]=${ORIGIN}`,'--format=json'])));}
    catch{throw new PairError('vault_creation_outcome_unknown');}
    if(!validId(item.id)||item.vault?.id!==vault)throw new PairError('vault_creation_result_invalid');
    const reference=`op://${vault}/${item.id}/password`;
    save(path,{status:'saved',vault,title,origin:ORIGIN,reference});return reference;
}
export async function resolveControl(reference:string,execute:Op=op):Promise<string> {
    const {vault,item}=validateReference(reference);
    const origin=(await execute(['read',`op://${vault}/${item}/weave_origin`])).trim();
    if(origin!==ORIGIN)throw new PairError('vault_origin_mismatch');
    const control=(await execute(['read',reference])).replace(/\r?\n$/,'');
    if(!/^[A-Za-z0-9]{64}$/.test(control))throw new PairError('invalid_control_from_vault');
    return control;
}
