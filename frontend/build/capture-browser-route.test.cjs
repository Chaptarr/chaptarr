const assert = require('node:assert/strict');
const { test } = require('node:test');
const { spawnSync } = require('node:child_process');
const { setTimeout: delay } = require('node:timers/promises');
const createRouteCapture = require('./capture-browser-route.cjs');

test('captures arriving before a waiter keep FIFO order', async() => {
  const capture = createRouteCapture('early request');
  capture.capture('first');
  capture.capture('second');
  assert.equal(await capture.next(), 'first');
  assert.equal(await capture.next(), 'second');
});

test('waiters are fulfilled by their actual captured routes in order', async() => {
  const capture = createRouteCapture('later request');
  const first = capture.next();
  const second = capture.next();
  capture.capture('first');
  capture.capture('second');
  assert.deepEqual(await Promise.all([first, second]), ['first', 'second']);
});

test('a timed-out waiter cannot steal the next captured route', async() => {
  const capture = createRouteCapture('missing estimate', 20);
  // A live browser owns the process lifetime, not the missing-route deadline.
  await Promise.all([assert.rejects(capture.next(), /20 ms waiting for missing estimate/), delay(25)]);
  const next = capture.next();
  capture.capture('usable');
  assert.equal(await next, 'usable');
});

test('an early browser failure is not held open by a pending route', () => {
  const result = spawnSync(process.execPath, ['-e', `
    require(${JSON.stringify(require.resolve('./capture-browser-route.cjs'))})('unused route').next();
    console.error('original browser failure');
    process.exitCode = 1;
  `], { timeout: 2000, encoding: 'utf8' });
  assert.equal(result.error, undefined);
  assert.equal(result.status, 1);
  assert.equal(result.stderr.trim(), 'original browser failure');
});
