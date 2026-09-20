import test from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {spawn} from 'node:child_process';
import {readFile} from 'node:fs/promises';
import {fileURLToPath} from 'node:url';
const root=fileURLToPath(new URL('../../',import.meta.url));
const vectors=JSON.parse(await readFile(new URL('../../protocol/governed-tools/requests.json',import.meta.url),'utf8'));
const source=vectors[0].request,id=source.invocationId,credential='synthetic-sensitive-envelope';
async function server(t,handler) {
  const value=createServer(handler);await new Promise(resolve=>value.listen(0,'127.0.0.1',resolve));
  t.after(()=>{value.closeAllConnections();return new Promise(resolve=>value.close(resolve));});
  return `http://127.0.0.1:${value.address().port}`;
}
function run(language,url,body=source,timeout=2000) {
  const binary=language==='typescript'?process.execPath:root+'clients/rust/target/debug/weave-client';
  const args=language==='typescript'?[root+'tests/client-probes/typescript.mjs']:[];
  args.push('invoke',url,'workspace','files',body.invocationId);
  return new Promise((resolve,reject)=>{
    const env={...process.env,WEAVE_CAPABILITY:credential,WEAVE_REQUEST_TIMEOUT_MS:String(timeout)};delete env.WEAVE_GLOBAL_BEARER;
    const child=spawn(binary,args,{env,stdio:['pipe','pipe','pipe']});
    let output='',error='';const timer=setTimeout(()=>{child.kill();reject(new Error('client probe deadline exceeded'));},10000);
    child.stdout.on('data',value=>{output+=value;if(output.length>9_000_000)child.kill();});
    child.stderr.on('data',value=>{error+=value;if(error.length>10000)child.kill();});
    child.on('error',reason=>{clearTimeout(timer);reject(reason);});
    child.on('close',code=>{clearTimeout(timer);resolve({code,output,error});});
    child.stdin.on('error',()=>{});child.stdin.end(JSON.stringify(body));
  });
}
async function requestBody(req){const chunks=[];for await(const chunk of req)chunks.push(chunk);return JSON.parse(Buffer.concat(chunks).toString());}
function respond(res,value,status=200){res.writeHead(status,{'content-type':'application/json'}).end(JSON.stringify(value));}
function success(request){return {success:true,invocationId:request.invocationId,toolName:'files',output:'ok',duration:'00:00:00',outcome:'Succeeded',outcomeRecorded:true,isReplay:false};}
for(const language of ['typescript','rust']) {
  for(const vector of vectors) test(`${language}: shared wire vector ${vector.name}`,async t=>{
    let calls=0;
    const url=await server(t,async(req,res)=>{calls++;const body=await requestBody(req);
      assert.equal(req.headers['x-weave-capability'],credential);
      assert.deepEqual({...body,rawInput:body.rawInput??null},{...vector.request,rawInput:vector.request.rawInput??null});respond(res,success(body));});
    const result=await run(language,url,vector.request);assert.equal(result.code,0,result.error);assert.equal(calls,1);
  });
  test(`${language}: redirect never forwards credential`,async t=>{
    let forwards=0,calls=0;
    const destination=await server(t,(req,res)=>{forwards++;respond(res,success(source));});
    const url=await server(t,async(req,res)=>{await requestBody(req);calls++;res.writeHead(307,{location:destination}).end();});
    const result=await run(language,url);assert.equal(result.code,2);assert.equal(forwards,0);assert.equal(calls,1);assert.equal(JSON.parse(result.error).invocationId,id);
  });
  test(`${language}: response loss is unconfirmed and never retried`,async t=>{
    let effects=0;
    const url=await server(t,async(req)=>{await requestBody(req);effects++;req.socket.destroy();});
    const result=await run(language,url);assert.equal(result.code,2);assert.equal(result.output,'');
    assert.deepEqual(JSON.parse(result.error),{kind:'transport',invocationId:id,delivery:'unconfirmed'});assert.equal(effects,1);
  });
  test(`${language}: deadline never retries a request`,async t=>{
    let calls=0;
    const url=await server(t,async(req)=>{await requestBody(req);calls++;});
    const result=await run(language,url,source,250);assert.equal(result.code,2);assert.equal(JSON.parse(result.error).delivery,'unconfirmed');assert.equal(calls,1);
  });
  test(`${language}: oversized response is bounded`,async t=>{
    const url=await server(t,async(req,res)=>{await requestBody(req);res.writeHead(200,{'content-type':'application/json'});res.end(' '.repeat(8_388_609));});
    const result=await run(language,url);assert.equal(result.code,2);assert.equal(JSON.parse(result.error).kind,'protocol');
  });
  test(`${language}: error responses do not reflect arbitrary server credential text`,async t=>{
    const url=await server(t,async(req,res)=>{await requestBody(req);respond(res,{errorCode:'forbidden',debugCredential:credential},403);});
    const result=await run(language,url);assert.equal(result.code,0,result.error);
    assert.deepEqual(JSON.parse(result.output),{status:403,body:{errorCode:'forbidden'}});assert.ok(!result.output.includes(credential));
  });
}
