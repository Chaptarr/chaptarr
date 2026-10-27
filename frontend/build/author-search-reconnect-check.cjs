const assert = require('node:assert/strict');

module.exports = async(page, strings, makeRelease, expectedErrors, unloadStops) => {
  let command = null;
  let listed = true;
  let lookupStatus = 200;
  let searchCount = 0;
  let lookupCount = 0;
  let holdLookup = false;
  let heldLookup = null;
  const commandHandler = async(route) => {
    const isList = new URL(route.request().url()).pathname.endsWith('/command');
    if (!isList) {
      lookupCount++;
      if (holdLookup) {
        heldLookup = route;
        return;
      }
    }
    const json = isList ? [] : command;
    if (isList && listed) {
      json.push(command);
    }
    if (!isList && lookupStatus >= 400) {
      expectedErrors.push({ url: route.request().url(), status: lookupStatus });
    }
    await route.fulfill({ status: isList ? 200 : lookupStatus, json });
  };
  const releaseHandler = async(route) => {
    if (route.request().method() === 'DELETE') {
      await route.fulfill({ json: { isStopping: false } });
      return;
    }
    const query = new URL(route.request().url()).searchParams;
    command = {
      id: ++searchCount,
      name: 'InteractiveAuthorSearch',
      status: 'started',
      body: { searchId: query.get('searchId'), authorId: 10, mediaType: 'audiobook', sendUpdatesToClient: true, suppressMessages: true }
    };
    await route.fulfill({ json: { ...command.body, commandId: command.id, bookResults: [], isComplete: false } });
  };
  await page.route('**/api/v1/command{,/*}', commandHandler);
  await page.route('**/api/v1/release?*', releaseHandler);
  const state = () => page.evaluate(() => window.readAuthorSearchState());
  const send = (body) => page.evaluate((value) => window.sendAuthorSearchSignalR(value), body);
  const reconnect = async() => {
    await page.evaluate(() => window.reconnectSearchFixture());
    await page.waitForFunction(() => window.readSearchCommands().isPopulated && !window.readSearchCommands().isFetching);
  };
  const start = async() => {
    await page.evaluate(() => window.renderAuthorSearchFixture('grouped-tabs', 2, 0));
    await page.getByRole('tab', { name: 'Search tab', exact: true }).click();
    await page.getByRole('button', { name: 'Search', exact: true }).click();
    await page.waitForFunction(() => window.readAuthorSearchState().commandId != null);
    const scope = { searchId: command.body.searchId, authorId: 10, mediaType: 'audiobook' };
    const rows = [
      { bookId: 1, title: 'Retained book', status: 'complete', releases: [makeRelease({ guid: 'retained', title: 'Retained release', bookId: 1, rank: 1, preferred: true })], hiddenReleases: [] },
      { bookId: 2, title: 'Missing book', status: 'pending', releases: [], hiddenReleases: [] }
    ];
    await send({ ...scope, bookResults: rows });
    return { scope, rows };
  };
  try {
    for (const status of ['completed', 'failed', 'cancelled', 'aborted', 'orphaned']) {
      const { scope, rows } = await start();
      command.status = status;
      const before = searchCount;
      await reconnect();
      await page.waitForFunction(() => !window.readAuthorSearchState().isFetching);
      assert.equal(searchCount, before, 'Reconnect must never start another search');
      assert.equal((await state()).isComplete, false, 'Command completion is not a final result snapshot');
      assert.deepEqual((await state()).bookResults, rows);
      await page.getByText(strings.AuthorSearchIncomplete, { exact: true }).waitFor();
      assert.equal(await page.getByRole('button', { name: 'Search', exact: true }).isEnabled(), true);
      await send({ ...scope, bookResults: [{ ...rows[0], status: 'pending', releases: [] }] });
      assert.equal((await state()).bookResults[0].releases[0].guid, 'retained', 'A delayed roster cannot erase completed results');
      assert.equal((await state()).isFetching, false, 'A delayed partial cannot restart the spinner');
      await send({ ...scope, bookResults: rows.map((row) => ({ ...row, status: 'complete' })), isComplete: true });
      assert.equal((await state()).isComplete, true, 'A late final snapshot is still accepted');
      assert.equal((await state()).error, null);
      await page.evaluate((value) => window.sendSearchCommand(value), command);
      assert.equal((await state()).error, null, 'A status event arriving after final results must not undo success');
    }

    const { scope, rows } = await start();
    for (const status of ['started', 'queued', 'paused']) {
      command.status = status;
      await reconnect();
      assert.equal((await state()).isFetching, true, `${status} is not terminal`);
    }
    for (const body of [{ ...command.body, mediaType: 'ebook' }, { ...command.body, authorId: 11 }, { ...command.body, searchId: 'old-search' }]) {
      await page.evaluate((value) => window.sendSearchCommand(value), { ...command, status: 'completed', body });
      assert.equal((await state()).isFetching, true, 'Other scopes cannot finish this search');
    }
    listed = false;
    command.status = 'completed';
    const beforeLookup = lookupCount;
    await reconnect();
    await page.waitForFunction(() => !window.readAuthorSearchState().isFetching);
    assert.equal(lookupCount, beforeLookup + 1, 'A trimmed command must use the existing command-by-ID endpoint once');
    await page.getByRole('button', { name: 'Search', exact: true }).click();
    await page.waitForFunction((old) => window.readAuthorSearchState().searchId !== old && window.readAuthorSearchState().commandId != null, scope.searchId);
    const newer = await state();
    await send({ ...scope, bookResults: rows, isComplete: true });
    assert.deepEqual(await state(), newer, 'Old final results cannot overwrite a deliberate new search');

    listed = true;
    await start();
    command.status = 'completed';
    await page.getByRole('button', { name: 'Stop search', exact: true }).click();
    await page.waitForFunction(() => !window.readAuthorSearchState().isFetching);
    await page.getByText(strings.AuthorSearchIncomplete, { exact: true }).waitFor();

    await start();
    listed = false;
    lookupStatus = 503;
    await reconnect();
    assert.equal((await state()).isFetching, true, 'A failed status request cannot declare work finished');
    lookupStatus = 404;
    await reconnect();
    await page.waitForFunction(() => !window.readAuthorSearchState().isFetching);
    assert.equal((await state()).error, true, 'An expired command has incomplete results, not successful empty results');
    lookupStatus = 200;
    listed = true;
    await start();
    const oldCommand = { ...command, status: 'failed' };
    holdLookup = true;
    listed = false;
    const lookupRequested = page.waitForRequest((request) => new URL(request.url()).pathname === `/api/v1/command/${oldCommand.id}`);
    await reconnect();
    await lookupRequested;
    await page.evaluate((value) => window.sendSearchCommand(value), oldCommand);
    await page.waitForFunction(() => !window.readAuthorSearchState().isFetching);
    holdLookup = false;
    listed = true;
    await page.getByRole('button', { name: 'Search', exact: true }).click();
    await page.waitForFunction((id) => window.readAuthorSearchState().commandId !== id && window.readAuthorSearchState().commandId != null, oldCommand.id);
    const currentSearch = await state();
    assert.ok(heldLookup, 'The fallback must really be in flight');
    await heldLookup.fulfill({ json: oldCommand });
    await page.evaluate(() => window.reconnectSearchFixture());
    await page.waitForFunction(() => !window.readSearchCommands().isFetching);
    assert.deepEqual(await state(), currentSearch, 'An old in-flight command lookup cannot end the newer search');
    await start();
    await page.evaluate(() => window.renderAuthorSearchFixture('grouped-tabs', 2, 0, { selectedMediaType: 'ebook' }, true));
    await reconnect();
    assert.equal((await state()).searchId, null, 'Reconnect cannot resurrect a search cleared by leaving its media side');
    const { scope: leavingScope } = await start();
    assert.equal((await state()).isFetching, true, 'Reload witness must have an active search');
    await page.reload();
    await page.waitForFunction(() => typeof window.renderAuthorSearchFixture === 'function');
    await new Promise((resolve, reject) => {
      const deadline = Date.now() + 5000;
      const check = () => {
        if (unloadStops.some((request) => request.searchId === leavingScope.searchId)) {
          resolve();
        } else if (Date.now() >= deadline) {
          reject(new Error('The HTTP server did not receive the reload Stop request'));
        } else {
          setTimeout(check, 25);
        }
      };
      check();
    });
    assert.deepEqual(unloadStops.filter((request) => request.searchId === leavingScope.searchId),
      [{ searchId: leavingScope.searchId, apiKey: 'fixture' }], 'Reload must deliver exactly one authenticated, search-scoped Stop');
    console.log('Reload sends a scoped Stop using the existing authenticated endpoint.');
  } finally {
    await page.unroute('**/api/v1/command{,/*}', commandHandler);
    await page.unroute('**/api/v1/release?*', releaseHandler);
  }
};
