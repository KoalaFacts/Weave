import assert from 'node:assert/strict';
import { test } from 'node:test';
import { Page } from './browser/chrome.mjs';

function protocolPage(exceptionDetails) {
    const calls = [];
    let globals = 0;
    const browser = { async command(method, params, sessionId) {
        calls.push({ method, params, sessionId });
        if (method === 'Runtime.evaluate') {
            assert.equal(params.expression, 'globalThis', 'Caller data must not become evaluated source.');
            return { result: { objectId: 'global-' + ++globals } };
        }
        if (method === 'Runtime.callFunctionOn') return { result: { value: undefined }, ...(exceptionDetails ? { exceptionDetails } : {}) };
        assert.equal(method, 'Runtime.releaseObject');
        return {};
    } };
    return { page: new Page(browser, 'protocol', 'session'), calls };
}

test('browser fill transmits IDs and values as CDP data and reacquires its global object', async () => {
    const { page, calls } = protocolPage();
    const pairs = [
        ['id-";globalThis.injected=true;//', '</script>\n` ${globalThis.injected=true}\u2028\u2029'],
        ['another-\\-id', 'different value with \'quotes\'']
    ];
    for (const pair of pairs) await page.fill(...pair);
    const invocations = calls.filter(call => call.method === 'Runtime.callFunctionOn');
    assert.equal(invocations.length, 2);
    assert.equal(invocations[0].params.functionDeclaration, invocations[1].params.functionDeclaration);
    for (const [index, invocation] of invocations.entries()) {
        assert.deepEqual(invocation.params.arguments, pairs[index].map(value => ({ value })));
        assert.equal(invocation.params.objectId, 'global-' + (index + 1));
        assert.equal(invocation.sessionId, 'session');
    }
    assert.deepEqual(calls.filter(call => call.method === 'Runtime.releaseObject').map(call => call.params.objectId), ['global-1', 'global-2']);
});

test('browser function exception is reported and its remote global handle is released', async () => {
    const { page, calls } = protocolPage({ text: 'Synthetic browser exception' });
    await assert.rejects(page.fill('message', 'test'), /Synthetic browser exception/);
    assert.equal(calls.at(-1).method, 'Runtime.releaseObject');
    assert.equal(calls.at(-1).params.objectId, 'global-1');
});
