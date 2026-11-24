import assert from 'node:assert/strict';
import test from 'node:test';
import { fail, problem } from '../src/wire.ts';

const logged = (t: test.TestContext) => t.mock.method(console, 'error', () => {});
const text = (m: ReturnType<typeof logged>) => m.mock.calls.map(call => call.arguments.map(String).join(' ')).join('\n');

test('problem_D1Error_503StorageLogsClassAndD1Message', async t => {
    const log = logged(t);
    const response = problem(new Error('D1_ERROR: Exceeded maximum daily rows written'));
    assert.equal(response.status, 503);
    assert.equal((await response.json() as {code: string}).code, 'storage');
    assert.equal(log.mock.callCount(), 1);
    assert.deepEqual(JSON.parse(text(log)), {event: 'storage-unavailable', errorClass: 'Error', message: 'D1_ERROR: Exceeded maximum daily rows written'});
});

test('problem_OtherError_LogsClassButNeverItsMessage', async t => {
    const log = logged(t);
    const response = problem(new SyntaxError('Unexpected token, "secret-body-marker" is not valid JSON'));
    assert.equal(response.status, 503);
    assert.deepEqual(JSON.parse(text(log)), {event: 'storage-unavailable', errorClass: 'SyntaxError', message: null});
    assert.equal(text(log).includes('secret-body-marker'), false);
});

test('problem_LongD1Message_IsBounded', t => {
    const log = logged(t);
    problem(new Error('D1_ERROR: ' + 'x'.repeat(1000)));
    assert.equal((JSON.parse(text(log)) as {message: string}).message.length, 300);
});

test('problem_NonError_LogsItsType', t => {
    const log = logged(t);
    problem('thrown string with secret-marker');
    assert.deepEqual(JSON.parse(text(log)), {event: 'storage-unavailable', errorClass: 'string', message: null});
});

test('problem_ExpectedProblem_IsNotLogged', t => {
    const log = logged(t);
    let thrown: unknown;
    try { fail(404, 'unavailable'); } catch (error) { thrown = error; }
    assert.equal(problem(thrown).status, 404);
    assert.equal(log.mock.callCount(), 0);
});

test('problem_D1PrefixInsideAnotherMessage_NotLogged', t => {
    const log = logged(t);
    problem(new SyntaxError('Unexpected token in "D1_ERROR: secret-body-marker"'));
    assert.deepEqual(JSON.parse(text(log)), {event: 'storage-unavailable', errorClass: 'SyntaxError', message: null});
});

test('problem_D1TypeError_LogsClassOnly', t => {
    const log = logged(t);
    problem(new Error("D1_TYPE_ERROR: Type 'bigint' not supported for value 'secret-value-marker'"));
    assert.deepEqual(JSON.parse(text(log)), {event: 'storage-unavailable', errorClass: 'Error', message: null});
});

test('problem_WrappedD1Error_LogsTheCauseMessageOnly', t => {
    const log = logged(t);
    problem(new Error('Failed query: select ... params: secret-param-marker', {cause: new Error('D1_ERROR: no such table: x')}));
    assert.deepEqual(JSON.parse(text(log)), {event: 'storage-unavailable', errorClass: 'Error', message: 'D1_ERROR: no such table: x'});
    assert.equal(text(log).includes('secret-param-marker'), false);
});
