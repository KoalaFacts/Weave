import {WeaveOperator} from '../../sdk/typescript/dist/operator.js';
import {ClientError} from '../../sdk/typescript/dist/client.js';
const [operation,baseUrl,workspace] = process.argv.slice(2);
try {
  let bytes=0,chunks=[];for await (const chunk of process.stdin) {bytes+=chunk.length;if(bytes>1_048_576)throw new ClientError('invalid-input');chunks.push(chunk);}
  const body=JSON.parse(Buffer.concat(chunks).toString('utf8'));
  const client=new WeaveOperator({baseUrl,workspace,capability:process.env.WEAVE_CAPABILITY});
  const reply=operation==='review'?await client.review(body):await client.decide(body.invocation,body.planDigest,body.decision);
  console.log(JSON.stringify(reply));
} catch(error) {console.error(JSON.stringify({kind:error instanceof ClientError?error.kind:'invalid-input'}));process.exitCode=2;}
