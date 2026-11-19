const assert = require('node:assert/strict');

module.exports = async(page, strings, makeRelease) => {
  const snapshots = new Map();
  const commands = new Map();
  let starts = 0;
  let stops = 0;
  let reads = 0;
  const handler = async(route) => {
    const request = route.request();
    const url = new URL(request.url());
    if (url.pathname.includes('/command')) {
      const id = Number(url.pathname.split('/').pop());
      return route.fulfill({ json: id ? commands.get(id) : [...commands.values()] });
    }
    const query = url.searchParams;
    if (request.method() === 'DELETE') {
      stops++;
      const snapshot = [...snapshots.values()].find((item) => item.searchId === query.get('searchId'));
      snapshot.isComplete = true;
      snapshot.isCancelled = true;
      commands.get(snapshot.commandId).status = 'cancelled';
      return route.fulfill({ json: { isStopping: false } });
    }
    const side = query.get('mediaType');
    let snapshot = snapshots.get(side);
    if (query.get('existing') === 'true') {
      reads++;
      return snapshot ? route.fulfill({ json: snapshot }) : route.fulfill({ status: 204, body: '' });
    }
    if (!snapshot || snapshot.isComplete) {
      snapshot = { searchId: query.get('searchId'), authorId: 10, mediaType: side, commandId: ++starts,
        bookResults: [], isComplete: false };
      snapshots.set(side, snapshot);
      commands.set(snapshot.commandId, { id: snapshot.commandId, name: 'InteractiveAuthorSearch', status: 'started',
        body: { searchId: snapshot.searchId, authorId: 10, mediaType: side, sendUpdatesToClient: true } });
    }
    return route.fulfill({ json: snapshot });
  };
  await page.route('**/api/v1/release?*', handler);
  await page.route('**/api/v1/command{,/*}', handler);
  const state = () => page.evaluate(() => window.readAuthorSearchState());
  const open = async(side = 'audiobook', preserve = false) => {
    await page.evaluate((options) => window.renderAuthorSearchFixture('grouped-tabs', 2, 2, { selectedMediaType: options.side }, options.preserve), { side, preserve });
    await page.getByRole('tab', { name: 'Search tab', exact: true }).click();
    await page.waitForFunction(() => !window.readAuthorSearchState().isAttaching);
  };
  const emit = async(side, final = false) => {
    const snapshot = snapshots.get(side);
    snapshot.bookResults = [{ bookId: side === 'ebook' ? 2 : 1, title: `${side} book`, status: 'complete',
      releases: [{ ...makeRelease({ guid: `${side}-release`, title: `${side} retained release`, bookId: side === 'ebook' ? 2 : 1, rank: 1, preferred: true }),
        expiresAt: new Date(Date.now() + 30 * 60 * 1000).toISOString(), searchId: snapshot.searchId, searchedBookId: side === 'ebook' ? 2 : 1 }], hiddenReleases: [] }];
    snapshot.isComplete = final;
    if (final) {
      commands.get(snapshot.commandId).status = 'completed';
    }
    await page.evaluate((body) => window.sendAuthorSearchSignalR(body), final ? { ...snapshot, bookResults: [] } : snapshot);
    if (final) {
      await page.waitForFunction(() => window.readAuthorSearchState().isComplete);
    }
  };
  try {
    await open();
    assert.equal(starts, 0, 'Opening only reads snapshots');
    await page.getByRole('button', { name: 'Search', exact: true }).click();
    await page.waitForFunction(() => window.readAuthorSearchState().isAccepted);
    await emit('audiobook');
    const original = (await state()).searchId;
    await page.evaluate(() => window.renderAuthorSearchFixture('filter-warning', 0, 0));
    await open();
    assert.equal((await state()).searchId, original);
    assert.equal((await state()).bookResults[0].releases.length, 1);
    assert.equal(starts, 1);
    assert.equal(stops, 0);
    await open('ebook', true);
    assert.equal((await state()).searchId, null);
    await page.getByRole('button', { name: 'Search', exact: true }).click();
    await page.waitForFunction(() => window.readAuthorSearchState().isAccepted);
    await emit('ebook');
    await open('audiobook', true);
    assert.equal((await state()).searchId, original);
    assert.equal(starts, 2, 'The two media sides have independent searches');
    await page.reload();
    await page.waitForFunction(() => typeof window.renderAuthorSearchFixture === 'function');
    await open();
    assert.equal((await state()).searchId, original);
    assert.equal(stops, 0, 'Reload does not send Stop');

    const other = await page.context().newPage();
    try {
      await other.route('**/api/v1/release?*', handler);
      await other.route('**/api/v1/command{,/*}', handler);
      await other.goto(page.url());
      await other.waitForFunction(() => typeof window.renderAuthorSearchFixture === 'function');
      await other.evaluate(() => window.renderAuthorSearchFixture('grouped-tabs', 2, 2));
      await other.getByRole('tab', { name: 'Search tab', exact: true }).click();
      await other.waitForFunction(() => window.readAuthorSearchState().isAccepted);
      assert.equal(await other.evaluate(() => window.readAuthorSearchState().searchId), original);
      assert.equal(await other.evaluate(() => window.readAuthorSearchState().bookResults[0].releases.length), 1);
      assert.equal(starts, 2, 'A separate tab attaches without searching again');
      const attached = await other.evaluate(async() => (await fetch('/api/v1/release?authorId=10&mediaType=audiobook&searchId=15845e15-61c4-4cab-8734-e50312087190')).json());
      assert.equal(attached.searchId, original);
      assert.equal(starts, 2, 'Concurrent Start requests also attach');
    } finally {
      await other.close();
    }
    await emit('audiobook', true);
    await open();
    assert.equal((await state()).isComplete, true);
    assert.equal((await state()).bookResults[0].releases[0].guid, 'audiobook-release');
    await page.getByRole('button', { name: 'Search', exact: true }).click();
    await page.waitForFunction((id) => window.readAuthorSearchState().searchId !== id && window.readAuthorSearchState().isAccepted, original);
    assert.equal(starts, 3, 'Explicit Search after completion starts fresh');
    await emit('audiobook');
    await page.getByRole('button', { name: 'Stop search', exact: true }).click();
    await page.waitForFunction(() => window.readAuthorSearchState().isCancelled);
    assert.equal(stops, 1);

    const expired = snapshots.get('audiobook');
    expired.bookResults[0].releases[0].expiresAt = new Date(Date.now() + 2000).toISOString();
    await open();
    const download = page.getByTitle(strings.SearchResultsExpired, { exact: true });
    assert.equal(await download.isDisabled(), true);
    assert.equal(await download.getAttribute('title'), strings.SearchResultsExpired);
    snapshots.delete('audiobook');
    await open();
    assert.equal((await state()).bookResults.length, 0, 'A fresh visit after cache expiry has no rows');
    assert.equal(starts, 3, 'Cache expiry never reruns an indexer search');
    assert.ok(reads >= 8);

    await page.evaluate(() => window.renderAuthorSearchFixture('command-messages', 0, 0));
    await page.evaluate(() => window.sendSearchCommand({ id: 300, name: 'InteractiveAuthorSearch', status: 'started',
      message: 'Searching Terry Pratchett (eBooks): 2 of 55 books', body: { authorName: 'Terry Pratchett', booksDone: 2, booksTotal: 55, authorId: 718, mediaType: 'ebook', sendUpdatesToClient: true } }));
    const link = page.getByRole('link', { name: 'Searching Terry Pratchett (eBooks): 2 of 55 books' });
    await link.waitFor();
    assert.equal(await link.getAttribute('href'), '/author/718?tab=search&mediaType=ebook');
    await page.evaluate(() => window.sendSearchCommand({ id: 300, name: 'InteractiveAuthorSearch', status: 'started',
      message: 'Processing release 100 of 200', body: { authorName: 'Terry Pratchett', booksDone: 2, booksTotal: 55,
        authorId: 718, mediaType: 'ebook', sendUpdatesToClient: true } }));
    await link.waitFor();
    assert.equal(await link.getAttribute('href'), '/author/718?tab=search&mediaType=ebook', 'Inner progress cannot replace the count or link');
    await link.click();
    assert.deepEqual(await page.evaluate(() => {
      const { pathname, search } = window.readMessageLocation();
      return { pathname, search };
    }),
    { pathname: '/author/718', search: '?tab=search&mediaType=ebook' });
    await page.evaluate(() => window.sendSearchCommand({ id: 301, name: 'InteractiveAuthorSearch', status: 'queued',
      body: { authorId: 719, mediaType: 'ebook', sendUpdatesToClient: true } }));
    await page.getByRole('link', { name: strings.AuthorSearchQueued }).waitFor();
    await page.evaluate(() => window.sendSearchCommand({ id: 300, name: 'InteractiveAuthorSearch', status: 'completed',
      body: { authorName: 'Terry Pratchett', authorId: 718, mediaType: 'ebook', sendUpdatesToClient: true,
        resultsExpireAt: new Date(Date.now() + 30 * 60000).toISOString() } }));
    const finished = page.getByRole('link', { name: 'Search ended for Terry Pratchett (eBooks). View results' });
    await finished.waitFor();
    assert.ok((await page.evaluate(() => window.readSidebarMessages())).find((row) => row.id === 300).hideAfter > 1790);
    await finished.click();
    await finished.waitFor({ state: 'detached' });
    console.log('Retained searches: leave/return, reload, both formats, final snapshot, manual rerun, queued Stop, expired download, sidebar link passed.');
  } finally {
    await page.unroute('**/api/v1/release?*', handler);
    await page.unroute('**/api/v1/command{,/*}', handler);
  }
};
