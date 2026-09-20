import {WeaveClient,ClientError} from '../../sdk/typescript/dist/client.js';
const [operation,baseUrl,workspace,tool,id] = process.argv.slice(2);
try {
  if (!['invoke','status','approval'].includes(operation) || !id) throw new ClientError('invalid-input');
  const client=new WeaveClient({baseUrl,workspace,capability:process.env.WEAVE_CAPABILITY,
    ...(process.env.WEAVE_GLOBAL_BEARER?{globalBearer:process.env.WEAVE_GLOBAL_BEARER}:{}),
    timeoutMs:Number(process.env.WEAVE_REQUEST_TIMEOUT_MS??30000)});
  let reply;
  if (operation==='invoke') {
    let bytes=0,chunks=[];for await (const chunk of process.stdin) {bytes+=chunk.length;if(bytes>1_048_576)throw new ClientError('invalid-input');chunks.push(chunk);}
    const request=JSON.parse(Buffer.concat(chunks).toString('utf8'));
    if (request.invocationId?.toLowerCase()!==id.toLowerCase() || request.toolName!==tool)throw new ClientError('invalid-input');
    reply=await client.invoke(request);
  } else reply=operation==='status'?await client.getInvocation(tool,id):await client.getApproval(tool,id);
  console.log(JSON.stringify(reply));
} catch(error) {
  const safe=error instanceof ClientError?error:new ClientError('invalid-input');
  console.error(JSON.stringify({kind:safe.kind,invocationId:safe.invocationId??null,delivery:safe.delivery}));process.exitCode=2;
}
