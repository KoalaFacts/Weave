import test from 'node:test';
import assert from 'node:assert/strict';
import {WeaveClient,ClientError} from '../dist/client.js';

test('JavaScript callers cannot coerce absent or non-string credentials into headers',()=>{
  for (const capability of [undefined,null,123,true,{toString:()=> 'synthetic-envelope'}])
    assert.throws(()=>new WeaveClient({baseUrl:'https://example.com',workspace:'workspace',capability}),ClientError);
});
test('only plain parameter records are accepted, not Map or class instances',async()=>{
  const client=new WeaveClient({baseUrl:'https://example.invalid',workspace:'workspace',capability:'synthetic-envelope'});
  class Params {path='note.txt';}
  for (const parameters of [new Map([['path','note.txt']]),new Params(),new Date()])
    await assert.rejects(()=>client.invoke({invocationId:'14bd2b92c8a34fdd8c79cb62eab32a13',toolName:'files',method:'read_file',parameters}),e=>e.kind==='invalid-input');
});
