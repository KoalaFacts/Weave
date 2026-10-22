import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync } from 'node:fs';

test('executable live pair support exists rather than instructions only', async () => {
    assert.ok(existsSync(new URL('../runtime.ts', import.meta.url)), 'Missing executable pair runtime');
    const { modelEnvironment, parseGeneration, validateOrigin } = await import('../runtime.ts');
    const env = modelEnvironment({PATH:'/bin',HOME:'/home/user',WEAVE_ACCOUNT_CONTROL:'private',OP_SERVICE_ACCOUNT_TOKEN:'vault-token'});
    assert.deepEqual(env, {PATH:'/bin',HOME:'/home/user'});
    assert.equal(parseGeneration('9223372036854775807'), '9223372036854775807');
    assert.throws(() => parseGeneration(9007199254740992));
    assert.throws(() => validateOrigin('https://evil.example'));
});

test('runtime rejects non-demo destinations and lossy or invalid generations', async () => {
    const {validateOrigin,parseGeneration}=await import('../runtime.ts');
    for (const s of ['http://demo.weave-389.workers.dev','https://demo.weave-389.workers.dev/path','https://x@demo.weave-389.workers.dev','https://demo.weave-389.workers.dev/?x=y']) assert.throws(()=>validateOrigin(s));
    for (const g of [0,-1,1.5,'01','9223372036854775808','x',null]) assert.throws(()=>parseGeneration(g));
    assert.equal(parseGeneration(7),'7');
});

test('process failures and timeouts never return untrusted stderr', async () => {
    const {capture}=await import('../runtime.ts');
    await assert.rejects(capture(process.execPath,['-e','console.error("secret-value");process.exit(3)']), e=>e instanceof Error && e.message==='process_failed');
    await assert.rejects(capture(process.execPath,['-e','setInterval(()=>{},1000)'],{timeoutMs:40}),e => (e instanceof Error) && e.message === 'process_timeout');
    await assert.rejects(capture(process.execPath,['-e','process.stdout.write("x".repeat(5000))'],{maxBytes:1024}),e => (e instanceof Error) && e.message === 'process_output_limit');
});

test('bounded execution uses stdin and an argument array, never a shell', async () => {
    const {capture}=await import('../runtime.ts');
    const result=await capture(process.execPath,['-e','process.stdin.pipe(process.stdout)'],{input:'payload; $(echo unsafe)'});
    assert.equal(result,'payload; $(echo unsafe)');
});

test('large wire integers are preserved as decimal strings',async()=>{
    const {parseWire}=await import('../runtime.ts');assert.equal(parseWire('{"generation":9223372036854775807}').generation,'9223372036854775807');
});
