const assert = require('node:assert/strict');

module.exports = async(page, expectedErrors) => {
  for (const [status, supportsCancellation, count] of [
    ['queued', false, 1], ['started', false, 0], ['started', true, 1],
    ['completed', true, 0], ['failed', true, 0]
  ]) {
    await page.evaluate((options) => window.renderAuthorSearchFixture('task-row', 0, 0, options), { status, supportsCancellation });
    assert.equal(await page.locator('button[title]').count(), count, `${status}, supportsCancellation=${supportsCancellation}`);
  }
  await page.clock.install();
  let snapshot = [];
  let failSnapshot = false;
  let holdSnapshot = false;
  let heldRoute = null;
  let requests = 0;
  let nextId = 80000;
  const commandHandler = async(route) => {
    requests++;
    assert.equal(route.request().method(), 'GET', 'Message recovery must not start commands');
    if (holdSnapshot) {
      heldRoute = route;
      return;
    }
    if (failSnapshot) {
      expectedErrors.push({ url: route.request().url(), status: 503 });
    }
    await route.fulfill({ status: failSnapshot ? 503 : 200, json: snapshot });
  };
  await page.route('**/api/v1/command', commandHandler);
  const messages = () => page.evaluate(() => window.readSidebarMessages());
  const send = (command) => page.evaluate((value) => window.sendSearchCommand(value), command);
  const progress = (id) => page.evaluate((commandId) => window.sendBookProgress({
    commandId, message: 'Checking Info for Thief: An epic quest adventure'
  }), id);
  const start = async(name = 'RefreshAuthor', status = 'started') => {
    await page.evaluate(() => window.renderAuthorSearchFixture('command-messages', 0, 0));
    const command = {
      id: ++nextId, name, status, message: 'Refreshing authors',
      body: { sendUpdatesToClient: true }
    };
    await send(command);
    await progress(command.id);
    assert.equal((await messages()).length, 2);
    await page.getByText('Checking Info for Thief: An epic quest adventure', { exact: true }).waitFor();
    return command;
  };
  const reconnect = async() => {
    await page.evaluate(() => window.reconnectSearchFixture());
    await page.waitForFunction(() => !window.readSearchCommands().isFetching);
  };
  try {
    const running = await start('BookSearch');
    const cancellationError = 'This task can\'t be cancelled once it has started.';
    const cancellationRoute = `**/api/v1/command/${running.id}`;
    await page.route(cancellationRoute, async(route) => {
      assert.equal(route.request().method(), 'DELETE');
      expectedErrors.push({ url: route.request().url(), status: 409 });
      await route.fulfill({ status: 409, json: { message: cancellationError } });
    });
    await page.evaluate((id) => window.cancelSearchCommand(id), running.id);
    await page.getByText(cancellationError, { exact: true }).waitFor();
    assert.equal((await page.evaluate(() => window.readSearchCommands())).items[0].status, 'started');
    await page.unroute(cancellationRoute);
    for (const name of ['RssSync', 'BookSearch', 'RefreshAuthor', 'BulkRefreshAuthor', 'RefreshUnmappedFiles', 'RetryUnmappedMatch']) {
      for (const status of ['completed', 'failed', 'cancelled', 'aborted', 'orphaned']) {
        const command = await start(name);
        await send({ ...command, status, message: `Finished: ${status}` });
        assert.deepEqual((await messages()).map(({ id, hideAfter }) => ({ id, hideAfter })), [
          { id: command.id, hideAfter: 4 }
        ], `${name} ${status}: only the timed result remains`);
        await progress(command.id);
        await send(command);
        assert.equal((await messages()).length, 1, 'Late progress cannot revive a finished command');
        assert.equal((await page.evaluate(() => window.readSearchCommands())).items[0].status, status);
      }
    }

    const silent = await start();
    await send({ ...silent, status: 'completed', message: null });
    assert.deepEqual(await messages(), [], 'A terminal update without a message clears both progress lines');

    for (const status of ['queued', 'started', 'paused']) {
      const command = await start('RefreshAuthor', status);
      snapshot = [command];
      await reconnect();
      assert.equal((await messages()).length, 2, `${status} progress must remain visible`);
      await page.clock.fastForward(6 * 60 * 1000);
      assert.equal((await messages()).length, 2, 'Active work must not disappear on a five-minute timer');
      assert.equal((await page.evaluate(() => window.readSearchCommands())).items.length, 1);
    }

    for (const status of ['completed', 'failed', 'cancelled', 'aborted', 'orphaned', 'expired']) {
      const command = await start();
      snapshot = status === 'expired' ? [] : [{ ...command, status }];
      await reconnect();
      assert.deepEqual(await messages(), [], `Reconnect clears ${status} progress`);
    }

    const retained = await start();
    failSnapshot = true;
    await reconnect();
    assert.equal((await messages()).length, 2, 'Failed refresh is not evidence that work ended');
    failSnapshot = false;
    await page.evaluate(() => window.showSidebarMessage({
      id: 'unrelated', name: 'Notice', message: 'Unrelated notice', type: 'info', hideAfter: 0
    }));
    snapshot = [];
    await reconnect();
    assert.deepEqual((await messages()).map(({ id }) => id), ['unrelated'], 'Only command-owned progress is reconciled');

    const stale = await start();
    holdSnapshot = true;
    let requested = page.waitForRequest('**/api/v1/command');
    await page.evaluate(() => window.reconnectSearchFixture());
    await requested;
    await send({ ...stale, status: 'completed', message: 'Refresh finished' });
    await heldRoute.fulfill({ json: [stale] });
    await page.waitForFunction(() => !window.readSearchCommands().isFetching);
    assert.equal((await page.evaluate(() => window.readSearchCommands())).items[0].status, 'completed',
      'An older active snapshot cannot undo completion received during the request');
    assert.equal((await messages()).length, 1);
    await progress(stale.id);
    assert.equal((await messages()).length, 1);

    await page.evaluate(() => window.renderAuthorSearchFixture('command-messages', 0, 0));
    requested = page.waitForRequest('**/api/v1/command');
    await page.evaluate(() => window.reconnectSearchFixture());
    await requested;
    const newer = { ...retained, id: ++nextId };
    await send(newer);
    await progress(newer.id);
    await heldRoute.fulfill({ json: [] });
    await page.waitForFunction(() => !window.readSearchCommands().isFetching);
    assert.equal((await messages()).length, 2, 'A command started during a snapshot is not expired by it');
    holdSnapshot = false;

    await page.evaluate((id) => window.removeSearchCommand(id), newer.id);
    assert.deepEqual(await messages(), [], 'Removing a command clears all its progress');

    const timed = await start('RssSync');
    await send({ ...timed, status: 'completed', message: 'Timed completion' });
    for (let i = 0; i < 5; i++) {
      await page.clock.runFor(750);
      await send({ ...timed, id: ++nextId, status: 'started', body: { suppressMessages: true } });
    }
    await page.getByText('Timed completion', { exact: true }).waitFor();
    assert.equal((await messages()).length, 1, 'RSS completion stays readable for four seconds');
    await page.clock.runFor(500);
    assert.deepEqual(await messages(), [], 'Unrelated command traffic must not reset the completion timer');
    await page.getByText('Timed completion', { exact: true }).waitFor({ state: 'detached' });

    await page.evaluate(() => window.showSidebarMessage({
      id: 'unmounted', name: 'Notice', message: 'Unmounted notice', type: 'info', hideAfter: 0.1
    }));
    await page.evaluate(() => window.unmountAuthorSearchFixture());
    await page.clock.runFor(250);
    assert.equal((await messages()).length, 1, 'Unmount cancels the dismissal timer');

    let searchResult = null;
    const releaseRoute = '**/api/v1/release?**';
    await page.route(releaseRoute, (route) => route.fulfill({ json: searchResult }));
    try {
      for (const [queried, failed, expected] of [
        [2, 2, 'All indexers failed'],
        [3, 1, 'No results (1 of 3 indexers failed)'],
        [2, 0, 'No results found'],
        [0, 0, 'No results found']
      ]) {
        searchResult = { releases: [], hiddenReleases: [], indexersQueried: queried, indexersFailed: failed };
        await page.evaluate(() => window.renderAuthorSearchFixture('book-tab', 0, 0));
        await page.getByText(expected, { exact: true }).waitFor();
        const state = await page.evaluate(() => window.readReleaseResults());
        assert.equal(state.indexersQueried, queried);
        assert.equal(state.indexersFailed, failed);
      }
    } finally {
      await page.unroute(releaseRoute);
    }

    console.log(`Sidebar command-message browser check passed: terminal states, reconnect/expiry, late events, snapshot races, failed fetch, timer churn and unmount (${requests} read-only command refreshes).`);
  } finally {
    await page.unroute('**/api/v1/command', commandHandler);
    await page.evaluate(() => window.unmountAuthorSearchFixture());
  }
};
