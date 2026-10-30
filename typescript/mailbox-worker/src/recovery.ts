import worker from './index';
import { type Env } from './database';
import { type Context } from './stream';

// Retain this built artifact/version before cutover. Recovery reads the current
// dedicated tables, including writes made after migration; it never restores
// a snapshot or routes requests to the archived JSON table.
export default {
    fetch(request:Request,env:Env,context:Context):Promise<Response>{
        return worker.fetch(request,{...env,RECOVERY_READ_ONLY:'true'},context);
    },
    async scheduled():Promise<void>{}
};
