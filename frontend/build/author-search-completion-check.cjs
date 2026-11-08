const assert = require('node:assert/strict');

module.exports = async(page, strings, makeRelease, expectedErrors) => {
  let snapshot = null;
  let command = null;
  let starts = 0;
  let reads = 0;
  let hold = false;
  let held = null;
  let heldStop = null;
  const handler = async(route) => {
    const url = new URL(route.request().url());
    if (route.request().method() === 'DELETE') {
      heldStop = route;
      return;
    }
    if (url.pathname.includes('/command')) {
      return route.fulfill({ json: url.pathname.endsWith('/command') ? [command].filter(Boolean) : command });
    }
    if (url.searchParams.get('existing') === 'true') {
      reads++;
      if (hold) {
        held = route;
        return;
      }
      return snapshot ? route.fulfill({ json: snapshot }) : route.fulfill({ status: 204, body: '' });
    }
    starts++;
    snapshot = { searchId: url.searchParams.get('searchId'), authorId: 10, mediaType: 'audiobook', commandId: starts, isComplete: false, bookResults: [] };
    command = { id: starts, name: 'InteractiveAuthorSearch', status: 'started', body: { ...snapshot, sendUpdatesToClient: true } };
    return route.fulfill({ json: snapshot });
  };
  await page.route('**/api/v1/release?*', handler);
  await page.route('**/api/v1/command{,/*}', handler);
  const state = () => page.evaluate(() => window.readAuthorSearchState());
  const emit = (body) => page.evaluate((value) => window.sendAuthorSearchSignalR(value), body);
  const waitForHeld = async() => {
    const deadline = Date.now() + 5000;
    while (Date.now() < deadline) {
      if (held) {
        return;
      }
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
    assert.fail('Expected snapshot request');
  };
  const start = async() => {
    snapshot = null;
    command = null;
    await page.evaluate(() => window.renderAuthorSearchFixture('grouped-tabs', 2, 0));
    await page.getByRole('tab', { name: 'Search tab', exact: true }).click();
    await page.waitForFunction(() => !window.readAuthorSearchState().isAttaching);
    await page.getByRole('button', { name: 'Search', exact: true }).click();
    await page.waitForFunction(() => window.readAuthorSearchState().isAccepted);
    snapshot.bookResults = [{ bookId: 1, title: 'Mort', status: 'complete', hiddenReleases: [],
      releases: [makeRelease({ guid: 'streamed', title: 'Mort streamed release', bookId: 1, rank: 1 })] }];
    await emit(snapshot);
  };
  const finish = (status) => {
    command.status = status;
    snapshot = { ...snapshot, isComplete: true, isCancelled: status === 'cancelled', isFailed: status === 'failed',
      bookResults: [{ ...snapshot.bookResults[0], releases: [...snapshot.bookResults[0].releases,
        makeRelease({ guid: 'missed', title: 'Mort missed release', bookId: 1, rank: 2 })] }] };
    return { ...snapshot, bookResults: [] };
  };
  try {
    for (const status of ['completed', 'cancelled', 'failed']) {
      await start();
      const before = reads;
      const notice = finish(status);
      hold = true;
      await emit(notice);
      await page.waitForFunction(() => window.readAuthorSearchState().isAttaching);
      // Wait for the HTTP request, not a fixed delay.
      await waitForHeld();
      await emit(notice);
      await page.evaluate((value) => window.sendSearchCommand(value), command);
      assert.equal(reads, before + 1, 'Completion and command updates share one snapshot request');
      assert.equal((await state()).isFetching, false);
      assert.equal((await state()).isComplete, false, 'A notice must not claim the rows arrived');
      assert.equal((await state()).bookResults[0].releases.length, 1);
      hold = false;
      await held.fulfill({ json: snapshot });
      held = null;
      await page.waitForFunction(() => window.readAuthorSearchState().isComplete);
      assert.equal((await state()).bookResults[0].releases.length, 2);
      assert.equal((await state()).isCancelled, status === 'cancelled');
      assert.equal(!!(await state()).error, status === 'failed');
      await emit(notice);
      await emit({ ...notice, authorId: 11 });
      await emit({ ...notice, mediaType: 'ebook' });
      await emit({ ...notice, searchId: 'old-search' });
      assert.equal(reads, before + 1, 'Completed and unrelated searches do not fetch again');
    }

    await start();
    hold = true;
    await page.evaluate(() => window.reconnectSearchFixture());
    await waitForHeld();
    const running = structuredClone(snapshot);
    const before = reads;
    await emit(finish('completed'));
    hold = false;
    await held.fulfill({ json: running });
    held = null;
    await page.waitForFunction(() => window.readAuthorSearchState().isComplete);
    assert.equal(reads, before + 1, 'An older in-flight snapshot is refreshed once after the completion notice');
    assert.equal((await state()).bookResults[0].releases.length, 2);

    await start();
    hold = true;
    await emit(finish('completed'));
    await waitForHeld();
    expectedErrors.push({ url: held.request().url(), status: 503 });
    hold = false;
    await held.fulfill({ status: 503, json: { message: 'Unavailable' } });
    held = null;
    await page.waitForFunction(() => !window.readAuthorSearchState().isAttaching);
    await page.getByText(strings.AuthorSearchIncomplete, { exact: true }).waitFor();
    assert.equal((await state()).isFetching, false);
    assert.equal((await state()).bookResults[0].releases.length, 1);
    const startsBeforeRecovery = starts;
    await page.evaluate(() => window.reconnectSearchFixture());
    await page.waitForFunction(() => window.readAuthorSearchState().isComplete);
    assert.equal(starts, startsBeforeRecovery, 'Recovery only reads the saved snapshot');
    await start();
    await page.getByRole('button', { name: 'Stop search', exact: true }).click();
    const stopDeadline = Date.now() + 5000;
    while (Date.now() < stopDeadline) {
      if (heldStop) {
        break;
      }
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
    assert.ok(heldStop, 'Expected Stop request');
    await emit(finish('cancelled'));
    await page.waitForFunction(() => window.readAuthorSearchState().isComplete);
    const readsBeforeStopReply = reads;
    await Promise.all([
      page.waitForResponse((response) => response.request().method() === 'DELETE'),
      heldStop.fulfill({ json: { isStopping: false } })
    ]);
    heldStop = null;
    await page.waitForFunction(() => !window.readAuthorSearchState().isAttaching);
    assert.equal(reads, readsBeforeStopReply, 'A delayed Stop reply must not refetch an already received final snapshot');
    console.log('Compact completion: one fetch, other scopes ignored, success/failure/cancel, stale reads, fetch failure and recovery passed.');
  } finally {
    if (held) {
      await held.abort();
    }
    if (heldStop) {
      await heldStop.abort();
    }
    await page.unroute('**/api/v1/release?*', handler);
    await page.unroute('**/api/v1/command{,/*}', handler);
  }
};
