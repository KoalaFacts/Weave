import { spawn } from 'node:child_process';
import { start } from './harness.mjs';
const h = await start();
try {
    const url = await h.mf.ready;
    const child = spawn('dotnet',['run','--project','../../tests/Weave.Worker.Compatibility/Weave.Worker.Compatibility.csproj','-c','Release','--no-build','--',url.toString(),'test/control-vectors.json'],{stdio:'inherit'});
    const code = await new Promise((resolve,reject)=>{child.on('error',reject);child.on('exit',resolve);});
    if(code !== 0) process.exitCode=1;
} finally {await h.close();}
