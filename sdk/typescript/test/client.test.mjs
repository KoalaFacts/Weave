import test from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {readFile} from 'node:fs/promises';
import {WeaveClient, ClientError} from '../dist/client.js';

const vectors = JSON.parse(await readFile(new URL('../../../protocol/governed-tools/requests.json', import.meta.url), 'utf8'));
const id = vectors[0].request.invocationId;
const reply = (request, extra = {}) => ({success: true, invocationId: request.invocationId, toolName:'files', output:'ok', duration:'00:00:00', outcome:'Succeeded', outcomeRecorded:true, isReplay:false, ...extra});
async function fixture(t, handler) {
  const server = createServer(handler);
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => {server.closeAllConnections(); return new Promise(resolve => server.close(resolve));});
  const url = `http://127.0.0.1:${server.address().port}`;
  return {url, client: new WeaveClient({baseUrl:url, workspace:'workspace', capability:'synthetic-envelope', timeoutMs:2000})};
}
function json(res, status, value) {res.writeHead(status, {'content-type':'application/json'}).end(JSON.stringify(value));}
async function read(req) {const chunks=[]; for await (const chunk of req) chunks.push(chunk); return JSON.parse(Buffer.concat(chunks).toString());}
for (const vector of vectors) test(`exact round trip: ${vector.name}`, async t => {
  const {client} = await fixture(t, async (req,res) => {
    assert.equal(req.headers['x-weave-capability'], 'synthetic-envelope');
    const body=await read(req); assert.deepEqual(body, vector.request); json(res,200,reply(body));
  });
  const result=await client.invoke(vector.request);
  assert.equal(result.status,200); assert.equal(result.body.invocationId,vector.request.invocationId);
});
test('invalid IDs, route injection and non-string parameters fail before send', async t => {
  let calls=0;
  const {client} = await fixture(t, (req,res)=> {calls++; json(res,200,{});});
  for (const bad of [{invocationId:''},{invocationId:'0'.repeat(32)},{toolName:'../admin'},{parameters:{path:42}},{parseWarning:'ignored?'}])
    await assert.rejects(()=>client.invoke({...vectors[0].request,...bad}),e=>e instanceof ClientError && e.kind==='invalid-input');
  assert.equal(calls,0);
});
test('request is owned before asynchronous I/O', async t => {
  const original=structuredClone(vectors[0].request), input=structuredClone(original);
  const {client}=await fixture(t,async(req,res)=>{const body=await read(req);assert.deepEqual(body,original);json(res,200,reply(body));});
  const call=client.invoke(input);input.parameters.path='changed.txt';input.rawInput='changed';await call;
});
test('HTTP 202 and 409 retain server state, never count as execution success', async t => {
  let calls=0;
  const {client}=await fixture(t,async(req,res)=>{const body=await read(req);calls++;json(res,calls===1?202:409,reply(body,{success:false,outcome:calls===1?null:'OutcomeUnknown',errorCode:calls===1?'approval-pending':'outcome-unknown'}));});
  assert.equal((await client.invoke(vectors[0].request)).status,202);
  const unknown=await client.invoke(vectors[0].request);assert.equal(unknown.body.outcome,'OutcomeUnknown');assert.equal(calls,2);
});
test('redirect is not followed and credential is not forwarded', async t => {
  let forwarded=0;
  const target=await fixture(t,(req,res)=>{forwarded++;json(res,200,reply(vectors[0].request));});
  const {client}=await fixture(t,(req,res)=>res.writeHead(307,{location:target.url}).end());
  await assert.rejects(()=>client.invoke(vectors[0].request),ClientError);assert.equal(forwarded,0);
});
test('response loss produces an unconfirmed error with retained ID and no retry', async t => {
  let effects=0;
  const {client}=await fixture(t,async(req)=>{await read(req);effects++;req.socket.destroy();});
  await assert.rejects(()=>client.invoke(vectors[0].request),e=>e.kind==='transport' && e.invocationId===id && e.delivery==='unconfirmed');
  assert.equal(effects,1);
});
test('abort does not invent a new ID or retry', async t => {
  let started;
  const received=new Promise(resolve=>{started=resolve;});
  let effects=0;
  const {client}=await fixture(t,async(req)=>{await read(req);effects++;started();});
  const abort=new AbortController(); const call=client.invoke(vectors[0].request,{signal:abort.signal});await received;abort.abort();
  await assert.rejects(()=>call,e=>e.kind==='transport'&&e.invocationId===id);assert.equal(effects,1);
});
test('malformed and mismatched response never becomes success', async t=> {
  let calls=0;
  const {client}=await fixture(t,(req,res)=>{calls++;json(res,200,calls===1?{success:true}:reply({...vectors[0].request,invocationId:'f'.repeat(32)}));});
  await assert.rejects(()=>client.invoke(vectors[0].request),e=>e.kind==='protocol');
  await assert.rejects(()=>client.invoke(vectors[0].request),e=>e.kind==='protocol');
});
test('insecure/credential-bearing origins and invalid credentials are rejected',()=> {
  for (const baseUrl of ['http://remote.example','https://user:pass@example.com','https://example.com/?q=x','https://example.com/../admin'])
    assert.throws(()=>new WeaveClient({baseUrl,workspace:'workspace',capability:'synthetic-envelope'}),ClientError);
  assert.throws(()=>new WeaveClient({baseUrl:'https://example.com',workspace:'workspace',capability:'bad\r\nheader'}),ClientError);
});
test('runtime configuration rejects non-string workspace rather than coercing it',()=> {
  assert.throws(()=>new WeaveClient({baseUrl:'https://example.com',workspace:123,capability:'synthetic-envelope'}),ClientError);
});
test('durable admission denial preserves NotDispatched, not a transport failure',async t=>{
  const {client}=await fixture(t,(req,res)=>json(res,503,reply(vectors[0].request,{success:false,outcome:'NotDispatched',errorCode:'journal-write-failed'})));
  const result=await client.invoke(vectors[0].request);assert.equal(result.body.outcome,'NotDispatched');assert.equal(result.status,503);
});
