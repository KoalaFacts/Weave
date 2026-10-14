import { readFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
const origin='https://demo.weave-389.workers.dev';
export async function verifyAgentEntry({expectedGuide,fetchImpl=fetch}={}){
    if(typeof expectedGuide!=='string'||!expectedGuide)throw Error('Expected guide contents are required.');
    async function read(path,type){
        const response=await fetchImpl(origin+path,{method:'GET',redirect:'error',cache:'no-store',signal:AbortSignal.timeout(15000)});
        if(!response.ok||!response.headers.get('content-type')?.startsWith(type)||response.headers.get('cache-control')!=='no-store'||response.headers.get('x-content-type-options')!=='nosniff'){
            await response.body?.cancel();throw Error('Published agent entry response failed validation: '+path);
        }
        let size=0;const chunks=[];
        for await(const chunk of response.body){size+=chunk.length;if(size>262144)throw Error('Published entry exceeds verification limit.');chunks.push(chunk);}
        return Buffer.concat(chunks).toString('utf8');
    }
    const actual=await read('/agent-start.md','text/markdown');
    if(actual!==expectedGuide)throw Error('Published guide does not match this checked-out commit.');
    const html=await read('/','text/html');
    for(const marker of ['href="/agent-start.md"','id="agentStart"','id="invitationCard"'])if(!html.includes(marker))throw Error('Published homepage is missing agent entry: '+marker);
    return {origin,guideSha256:createHash('sha256').update(actual).digest('hex'),readOnly:true};
}
if(process.argv[1]&&import.meta.url===pathToFileURL(resolve(process.argv[1])).href){
    try{
        const expectedGuide=await readFile(new URL('../src/agent-start.md',import.meta.url),'utf8');
        console.log('Verified published Agent Start guide and homepage:',JSON.stringify(await verifyAgentEntry({expectedGuide})));
    }catch(error){console.error(error.message);process.exitCode=1;}
}
