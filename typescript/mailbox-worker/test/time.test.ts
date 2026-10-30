import assert from 'node:assert/strict';
import { test } from 'node:test';
import { apiTime, epochSeconds } from '../src/time.ts';

test('epochSeconds_TickBoundaries_FloorTowardNegativeInfinity', () => {
    assert.equal(epochSeconds(-1n), -1);
    assert.equal(epochSeconds(0n), 0);
    assert.equal(epochSeconds(9_999_999n), 0);
    assert.equal(epochSeconds(10_000_000n), 1);
    assert.equal(epochSeconds(-10_000_000n), -1);
    assert.equal(epochSeconds(-10_000_001n), -2);
});

test('apiTime_Ticks_MillisecondFormUnlessSubMillisecond', () => {
    assert.equal(apiTime(null), null);
    assert.equal(apiTime(0n), '1970-01-01T00:00:00.000Z');
    assert.equal(apiTime('17672256000000000'), '2026-01-01T00:00:00.000Z');
    assert.equal(apiTime(17672256000000001n), '2026-01-01T00:00:00.0000001Z');
});
