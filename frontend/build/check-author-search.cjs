/* Render the real grouped search UI; every browser request is intercepted locally. */
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const webpack = require('webpack');
const { chromium } = require('playwright');

const root = path.resolve(__dirname, '../..');
const output = fs.mkdtempSync(path.join(os.tmpdir(), 'chaptarr-search-ui-'));
const config = require('./webpack.config')({ production: true });

config.entry = { fixture: path.join(__dirname, 'author-search-fixture.js') };
config.output = { ...config.output, path: output, filename: 'fixture.js' };
config.optimization = { minimize: false };
config.plugins = config.plugins.filter((plugin) => ['DefinePlugin', 'MiniCssExtractPlugin'].includes(plugin.constructor.name));
// Isolate the toolbar action from unrelated detail-page rendering and requests.
const detailView = path.join(output, 'detail-view.js');
fs.writeFileSync(detailView, `const React = require(${JSON.stringify(require.resolve('react'))}); module.exports = ({onSearchPress}) => React.createElement('button', {onClick:onSearchPress}, 'Search monitored');`);
config.plugins.push(new webpack.NormalModuleReplacementPlugin(/^\.\/AuthorDetails$/, (resource) => {
  if (resource.context.endsWith(path.join('Author', 'Details'))) {
    resource.request = detailView;
  }
}));
for (const rule of config.module.rules) {
  if (Array.isArray(rule.use)) {
    rule.use = rule.use.filter((loader) => !String(typeof loader === 'string' ? loader : loader.loader).includes('css-modules-typescript-loader'));
  }
}

function makeRelease({ guid, title, bookId, rank, preferred = false }) {
  return {
    rank,
    guid,
    protocol: 'torrent',
    age: 0,
    ageHours: 0,
    ageMinutes: 0,
    publishDate: '2026-09-19T00:00:00Z',
    title,
    infoUrl: `https://example.test/${encodeURIComponent(guid)}`,
    indexer: 'Mock Indexer',
    indexerId: 17,
    size: 1024,
    seeders: 1,
    leechers: 0,
    quality: { quality: { id: 1, name: 'Unknown' }, revision: { real: 0, version: 1, isRepack: false } },
    customFormats: [],
    customFormatScore: 0,
    indexerFlags: 0,
    narrator: 'Mock Reader',
    authorId: 10,
    bookId,
    rejections: [],
    downloadAllowed: true,
    isGrabbing: false,
    isGrabbed: false,
    isPreferredChoice: preferred,
    releaseWeight: rank
  };
}

function makeBookResult(bookId, title, status, releases, hiddenReleases = []) {
  return { bookId, title, mediaType: 'audiobook', status, releases, hiddenReleases };
}

(async() => {
  const compiler = webpack(config);
  await new Promise((resolve, reject) => compiler.run((error, stats) => {
    compiler.close(() => {});
    if (error || stats.hasErrors()) {
      reject(error || new Error(stats.toString({ all: false, errors: true })));
    } else {
      console.log('Browser fixture webpack compilation completed.');
      resolve();
    }
  }));

  console.log('Launching Chromium for the mocked browser lifecycle.');
  const browser = await chromium.launch();
  try {
    const css = fs.readdirSync(path.join(output, 'Content')).find((name) => name.endsWith('.css'));
    const strings = JSON.parse(fs.readFileSync(path.join(root, 'src/NzbDrone.Core/Localization/Core/en.json')));
    const page = await browser.newPage();
    const errors = [];
    const releaseRequests = [];
    const authorRequests = [];
    const grabRequests = [];
    const requestFailures = [];
    const heldAuthorRoutes = new Map();
    let holdNextAuthorSearch = false;
    let heldAuthorRequestResolver = null;
    page.on('pageerror', (error) => errors.push({ type: 'pageerror', message: error.message, stack: error.stack }));
    page.on('console', (message) => {
      if (message.type() === 'error') {
        errors.push({ type: 'console', message: message.text() });
      }
    });
    page.on('requestfailed', (request) => requestFailures.push({ url: request.url(), failure: request.failure()?.errorText }));
    await page.route('**/*', async(route) => {
      const request = route.request();
      const url = new URL(request.url());
      const pathname = url.pathname;
      if (pathname === '/api/v1/release') {
        const params = Object.fromEntries(url.searchParams);
        if (request.method() === 'POST') {
          const payload = request.postDataJSON();
          grabRequests.push(payload);
          return route.fulfill({ json: {} });
        }
        if (params.authorId) {
          authorRequests.push(params);
          if (holdNextAuthorSearch) {
            holdNextAuthorSearch = false;
            heldAuthorRoutes.set(params.searchId, route);
            if (heldAuthorRequestResolver) {
              heldAuthorRequestResolver(params);
              heldAuthorRequestResolver = null;
            }
            return;
          }
          return route.fulfill({ json: {
            searchId: params.searchId,
            authorId: Number(params.authorId),
            mediaType: params.mediaType,
            bookResults: [],
            isComplete: true,
            isCancelled: false
          } });
        }
        releaseRequests.push(params);
        return route.fulfill({ json: {
          releases: [],
          hiddenReleases: [{ id: 1, title: 'Hidden release' }],
          filterSummary: { totalResults: 1 }
        } });
      }
      if (pathname.endsWith('/localization')) {
        return route.fulfill({ json: { Strings: strings } });
      }
      if (pathname === '/fixture.js') {
        return route.fulfill({ path: path.join(output, 'fixture.js'), contentType: 'text/javascript' });
      }
      if (pathname === '/style.css') {
        return route.fulfill({ path: path.join(output, 'Content', css), contentType: 'text/css' });
      }
      if (pathname !== '/') {
        return route.fulfill({ status: 404, body: '' });
      }
      return route.fulfill({ contentType: 'text/html', body: '<!doctype html><link rel="stylesheet" href="/style.css"><div id="root"></div><div id="portal-root"></div><script>window.Chaptarr={isProduction:true,apiRoot:"/api/v1"};</script><script src="/fixture.js"></script>' });
    });
    await page.goto('http://author-search.test/');
    await page.evaluate(() => window.renderAuthorSearchFixture('filter-warning', 0, 0));
    assert.equal(await page.getByText('2 results are available behind bypassable filters.', { exact: true }).count(), 1);
    assert.equal(await page.getByText('Showing 0 of 2 results (2 filtered)', { exact: true }).count(), 0);

    for (const view of ['index', 'detail']) {
      for (const [audio, ebook, overrides, count] of [
        [2, 3, {}, 5], [2, 4, {}, 6], [100, 3, { audiobookMonitored: false }, 3],
        [3, 100, { ebookMonitored: null }, 3], [3, 100, { ebookQualityProfileId: null }, 3]
      ]) {
        await page.evaluate((args) => window.renderAuthorSearchFixture(...args), [view, audio, ebook, overrides]);
        await page.getByRole('button', { name: 'Search monitored', exact: true }).click();
        if (count <= 5) {
          assert.deepEqual(await page.evaluate(() => window.searchCommands), [{ name: 'AuthorSearch', authorId: 10 }]);
          assert.equal(await page.getByText('This will perform', { exact: false }).count(), 0);
        } else {
          await page.getByText(`This will perform ${count} book searches`, { exact: false }).waitFor();
          assert.equal(await page.evaluate(() => window.searchCommands.length), 0);
          await page.getByRole('button', { name: 'Cancel', exact: true }).click();
          assert.equal(await page.evaluate(() => window.searchCommands.length), 0);
          await page.getByRole('button', { name: 'Search monitored', exact: true }).click();
          await page.getByRole('button', { name: 'Search', exact: true }).click();
          assert.deepEqual(await page.evaluate(() => window.searchCommands), [{ name: 'AuthorSearch', authorId: 10 }]);
        }
      }
      await page.evaluate((v) => window.renderAuthorSearchFixture(v, 2, 4), view);
      await page.getByRole('button', { name: 'Search monitored', exact: true }).click();
      await page.getByText('This will perform 6 book searches', { exact: false }).waitFor();
      await page.evaluate((v) => window.renderAuthorSearchFixture(v, 2, 3, { id: 11 }, true), view);
      await page.getByRole('button', { name: 'Search monitored', exact: true }).click();
      assert.deepEqual(await page.evaluate(() => window.searchCommands), [{ name: 'AuthorSearch', authorId: 11 }]);
    }

    releaseRequests.length = 0;
    await page.evaluate(() => window.renderAuthorSearchFixture('book-tab', 0, 0));
    await page.waitForFunction(() => window.readReleaseResults().hiddenItems.length === 1);
    assert.deepEqual(releaseRequests, [{ bookId: '10', initialMediaType: 'audiobook', bypassFilters: 'false' }]);

    for (const [audio, ebook, mediaType, count, overrides] of [
      [2, 4, 'audiobook', 2, {}], [2, 4, 'ebook', 4, {}],
      [5, 10, 'audiobook', 5, {}], [6, 10, 'audiobook', 6, {}],
      [10, 5, 'ebook', 5, {}], [10, 6, 'ebook', 6, {}],
      [6, 6, null, 0, {}],
      [0, 10, 'audiobook', 0, {}], [10, 0, 'ebook', 0, {}],
      [6, 10, 'audiobook', 0, { audiobookMonitored: false }],
      [10, 6, 'ebook', 0, { ebookQualityProfileId: null }]
    ]) {
      authorRequests.length = 0;
      await page.evaluate((args) => window.renderAuthorSearchFixture('tab', ...args), [audio, ebook, { selectedMediaType: mediaType, ...overrides }]);
      await page.evaluate(() => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve))));
      assert.equal(authorRequests.length, 0, 'Opening the author Search tab must not query indexers');
      const search = page.getByRole('button', { name: 'Search', exact: true });
      if (count === 0) {
        assert.equal(await search.isDisabled(), true);
      } else {
        const nextAuthorRequest = page.waitForRequest((request) => {
          const url = new URL(request.url());
          return url.pathname === '/api/v1/release' && url.searchParams.has('authorId');
        });
        await search.click();
        if (count > 5) {
          await page.getByText(`This will perform ${count} book searches`, { exact: false }).waitFor();
          assert.equal(authorRequests.length, 0);
          await page.getByRole('button', { name: 'Cancel', exact: true }).click();
          assert.equal(authorRequests.length, 0);
          await search.click();
          await page.getByRole('button', { name: 'Search', exact: true }).last().click();
        }
        const request = await nextAuthorRequest;
        const params = Object.fromEntries(new URL(request.url()).searchParams);
        assert.deepEqual(params, { authorId: '10', mediaType, searchId: params.searchId });
        await page.waitForFunction(() => window.readAuthorSearchState().isComplete === true);
        assert.deepEqual(await page.evaluate(() => window.searchCommands), []);
      }
      await page.evaluate(() => window.renderAuthorSearchFixture('tab', 6, 6, { id: 11, selectedMediaType: 'ebook' }, true));
      assert.equal(await page.getByText('This will perform', { exact: false }).count(), 0);
      assert.equal(authorRequests.length, count ? 1 : 0, 'Changing author/media must not start another search');
      await page.evaluate(() => window.renderAuthorSearchFixture('tab', 6, 6, { id: 11, selectedMediaType: 'ebook' }));
      assert.equal(authorRequests.length, count ? 1 : 0, 'Reopening the author tab must not start a search');
    }

    console.log('Toolbar confirmation and selected-media checks passed.');
    releaseRequests.length = 0;
    authorRequests.length = 0;
    grabRequests.length = 0;
    requestFailures.length = 0;
    await page.evaluate(() => window.renderAuthorSearchFixture('grouped-tabs', 2, 4, { id: 10, selectedMediaType: 'audiobook' }));
    await page.evaluate(() => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    const authorSearchPanel = page.locator('[data-testid="author-search-panel"]');
    assert.equal(await authorSearchPanel.count(), 1, 'The force-rendered Search panel should remain mounted');
    assert.equal(await authorSearchPanel.isVisible(), false, 'The Search tab panel should use react-tabs visibility while inactive');
    assert.deepEqual(authorRequests, [], 'The force-rendered author Search tab must stay idle while hidden');
    await page.getByRole('tab', { name: 'Search tab', exact: true }).click();
    assert.equal(await authorSearchPanel.isVisible(), true, 'Selecting Search should reveal the production tab panel');
    assert.equal(await page.getByRole('button', { name: 'Search', exact: true }).count(), 1);
    assert.deepEqual(authorRequests, [], 'Showing the author Search tab must not start indexer requests');

    let resolveHeldAuthorRequest = null;
    const heldAuthorRequest = new Promise((resolve) => {
      resolveHeldAuthorRequest = resolve;
    });
    holdNextAuthorSearch = true;
    heldAuthorRequestResolver = resolveHeldAuthorRequest;
    await page.getByRole('button', { name: 'Search', exact: true }).click();
    const requestParams = await heldAuthorRequest;
    const searchId = requestParams.searchId;
    assert.equal(requestParams.authorId, '10');
    assert.equal(requestParams.mediaType, 'audiobook');
    assert.equal(authorRequests.length, 1);
    await page.getByRole('tab', { name: 'Books', exact: true }).click();
    assert.equal(await authorSearchPanel.isVisible(), false, 'Switching tabs should hide the still-mounted Search panel');
    assert.equal((await page.evaluate(() => window.readAuthorSearchState())).isFetching, true, 'Hiding the panel must preserve the active author request');
    assert.equal(authorRequests.length, 1);
    await page.getByRole('tab', { name: 'Search tab', exact: true }).click();
    assert.equal(await authorSearchPanel.isVisible(), true, 'Returning to Search should reveal the same panel');

    const preferredA = makeRelease({ guid: 'shared-guid', title: 'Preferred Book A', bookId: 101, rank: 10, preferred: true });
    const otherZuluA = makeRelease({ guid: 'other-zulu', title: 'Other Zulu', bookId: 101, rank: 30 });
    const otherAlphaA = makeRelease({ guid: 'other-alpha', title: 'Other Alpha', bookId: 101, rank: 20 });
    const unmappedExpandedA = {
      ...makeRelease({ guid: 'unmapped-expanded', title: 'Unmapped Expanded Release', bookId: null, rank: 40 }),
      authorId: null,
      downloadAllowed: false,
      rejections: ['Error']
    };
    const mismatchedExpandedA = makeRelease({ guid: 'mismatched-expanded', title: 'Mismatched Expanded Release', bookId: 102, rank: 41 });
    const authorMismatchExpandedA = {
      ...makeRelease({ guid: 'author-mismatch-expanded', title: 'Author Mismatch Expanded Release', bookId: 101, rank: 42 }),
      downloadAllowed: false,
      rejections: ['Author mismatch: Madeline Miller']
    };
    const sharedB = makeRelease({ guid: 'shared-guid', title: 'Shared Book B', bookId: 102, rank: 11, preferred: true });
    const hiddenB = [
      makeRelease({ guid: 'hidden-zulu-b', title: 'Hidden Zulu B', bookId: 102, rank: 12 }),
      makeRelease({ guid: 'hidden-alpha-b', title: 'Hidden Alpha B', bookId: 102, rank: 13 })
    ];
    const mismatchedC = makeRelease({ guid: 'mismatched-summary', title: 'Mismatched Summary Release', bookId: 102, rank: 12, preferred: true });
    const hiddenA = Array.from({ length: 51 }, (_, index) => makeRelease({
      guid: `hidden-${index + 1}`,
      title: `Hidden A ${String(index + 1).padStart(2, '0')}`,
      bookId: 101,
      rank: index + 100
    }));
    const bookA = makeBookResult(101, 'Book A', 'complete', [preferredA, otherZuluA, otherAlphaA, unmappedExpandedA, mismatchedExpandedA, authorMismatchExpandedA], hiddenA);
    const bookB = makeBookResult(102, 'Book B', 'pending', [sharedB], hiddenB);
    const bookC = makeBookResult(103, 'Book C', 'complete', [mismatchedC]);
    const waitingBook = makeBookResult(104, 'Waiting Book', 'pending', []);
    const emptyBook = makeBookResult(105, 'Empty Book', 'complete', []);
    const failedBook = makeBookResult(106, 'Failed Book', 'failed', []);
    const noPreferredBook = makeBookResult(107, 'No Preferred Book', 'complete', [
      makeRelease({ guid: 'no-preferred', title: 'No Preferred Candidate', bookId: 107, rank: 70 })
    ]);

    await page.evaluate((body) => window.sendAuthorSearchSignalR(body), {
      searchId, authorId: 10, mediaType: 'audiobook', bookResults: [bookA], isComplete: false, isCancelled: false
    });
    let searchState = await page.evaluate(() => window.readAuthorSearchState());
    assert.equal(searchState.isFetching, true);
    assert.deepEqual(searchState.bookResults.map((result) => result.bookId), [101], 'The first SignalR delivery should render before the HTTP final response');

    await page.evaluate((body) => window.sendAuthorSearchSignalR(body), {
      searchId, authorId: 10, mediaType: 'audiobook', bookResults: [bookB, bookC, waitingBook, emptyBook, failedBook, noPreferredBook], isComplete: false, isCancelled: false
    });
    searchState = await page.evaluate(() => window.readAuthorSearchState());
    assert.deepEqual(searchState.bookResults.map((result) => result.bookId), [101, 102, 103, 104, 105, 106, 107]);
    assert.equal(await page.getByRole('columnheader', { name: 'Rank', exact: true }).count(), 0, 'The collapsed summary has no rank column');
    assert.equal(await page.getByText('Preferred Book A', { exact: true }).count(), 1, 'The preferred release is the collapsed summary row');
    assert.equal(await page.getByText('Shared Book B', { exact: true }).count(), 1, 'A shared GUID stays in the second book group');
    assert.equal(await page.getByText('Mismatched Summary Release', { exact: true }).count(), 1);

    const summaryTable = page.locator('table[class*="summaryTable"]');
    const mismatchTooltip = 'This result cannot be grabbed because it is not mapped to the searched book.';
    const statusRows = [
      ['Waiting Book', 'Waiting for results'],
      ['Empty Book', 'No results returned'],
      ['Failed Book', 'Search failed'],
      ['No Preferred Book', 'No preferred release returned']
    ];
    const borderEvidence = [];
    for (const [bookTitle, status] of statusRows) {
      const row = page.getByRole('button', { name: `Show other releases for ${bookTitle}`, exact: true }).locator('xpath=ancestor::tr[1]');
      assert.match(await row.innerText(), new RegExp(status));
      const cellBorders = await row.locator('td').evaluateAll((cells) => cells.slice(0, 2).map((cell) => {
        const style = getComputedStyle(cell);
        return { width: style.borderTopWidth, style: style.borderTopStyle };
      }));
      borderEvidence.push({ bookTitle, cellBorders });
      assert.deepEqual(cellBorders, [
        { width: '1px', style: 'solid' },
        { width: '1px', style: 'solid' }
      ], `${bookTitle} cells should keep the shared table separator`);
    }
    const preferredRow = page.getByRole('button', { name: 'Show other releases for Book A', exact: true }).locator('xpath=ancestor::tr[1]');
    const preferredCellBorders = await preferredRow.locator('td').evaluateAll((cells) => cells.slice(0, 2).map((cell) => {
      const style = getComputedStyle(cell);
      return { width: style.borderTopWidth, style: style.borderTopStyle };
    }));
    borderEvidence.push({ bookTitle: 'Book A', cellBorders: preferredCellBorders });
    assert.deepEqual(preferredCellBorders, [
      { width: '1px', style: 'solid' },
      { width: '1px', style: 'solid' }
    ], 'Preferred summary cells should keep the same separator');
    console.log(`Rendered summary separator evidence: ${JSON.stringify(borderEvidence)}`);
    for (const title of ['Mismatched Summary Release']) {
      const summaryRow = page.getByText(title, { exact: true }).locator('xpath=ancestor::tr[1]');
      const grabButton = summaryRow.getByRole('button').last();
      assert.equal(await grabButton.isDisabled(), true, `${title} summary grab should be disabled`);
      assert.equal(await grabButton.getAttribute('title'), mismatchTooltip);
      await grabButton.click({ force: true });
      assert.equal(await page.getByText('Grab Release', { exact: true }).count(), 0, `${title} must not open the manual confirmation`);
      assert.equal(grabRequests.length, 0, `${title} must not POST a grab`);
    }

    const requestCountBeforeExpand = authorRequests.length + releaseRequests.length;
    const expandBookButton = page.getByRole('button', { name: 'Show other releases for Book A', exact: true });
    assert.equal(await expandBookButton.getByText('Expand', { exact: true }).count(), 1, 'The collapsed book action should be visibly labeled');
    assert.equal(await expandBookButton.locator('svg').isVisible(), true, 'The collapsed book action should show the disclosure chevron');
    await expandBookButton.click();
    assert.equal(authorRequests.length + releaseRequests.length, requestCountBeforeExpand, 'Expanding a book must not issue another search');
    await page.getByRole('button', { name: /Show 51 Hidden Results/ }).waitFor();
    const collapseBookButton = page.getByRole('button', { name: 'Hide other releases for Book A', exact: true });
    assert.equal(await collapseBookButton.getByText('Collapse', { exact: true }).count(), 1, 'The expanded book action should be visibly labeled');
    assert.equal(await collapseBookButton.locator('svg').isVisible(), true, 'The expanded book action should show the collapse chevron');
    const layoutSnapshots = [];
    for (const width of [1280, 768, 390]) {
      await page.setViewportSize({ width, height: width === 390 ? 844 : 900 });
      await page.evaluate(() => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve))));
      layoutSnapshots.push(await page.evaluate(() => {
        const summaryElement = document.querySelector('table[class*="summaryTable"]');
        const summaryScroller = summaryElement && summaryElement.parentElement;
        const expandedCell = summaryElement && summaryElement.querySelector('td[class*="authorSearchExpandedCell"]');
        const releaseTable = expandedCell && expandedCell.querySelector('table');
        const releaseScroller = releaseTable && releaseTable.parentElement;
        const headerWidths = (table) => {
          if (!table) {
            return [];
          }

          return Array.from(table.querySelectorAll(':scope > thead > tr > th')).map((cell) => Math.round(cell.getBoundingClientRect().width));
        };
        return {
          viewport: window.innerWidth,
          summaryTableWidth: summaryElement && Math.round(summaryElement.getBoundingClientRect().width),
          summaryScroller: summaryScroller && { clientWidth: summaryScroller.clientWidth, scrollWidth: summaryScroller.scrollWidth },
          summaryHeaders: headerWidths(summaryElement),
          releaseTableWidth: releaseTable && Math.round(releaseTable.getBoundingClientRect().width),
          releaseScroller: releaseScroller && { clientWidth: releaseScroller.clientWidth, scrollWidth: releaseScroller.scrollWidth },
          releaseHeaders: headerWidths(releaseTable)
        };
      }));
    }
    console.log(`Rendered author-search widths: ${JSON.stringify(layoutSnapshots)}`);
    const summaryColumnMinimums = [230, 280, 110, 130, 80, 150, 90];
    for (const snapshot of layoutSnapshots) {
      assert.ok(snapshot.summaryTableWidth >= 1150, `The summary table should keep its minimum width at ${snapshot.viewport}px`);
      assert.equal(snapshot.summaryHeaders.length, 7, 'The summary retains its seven columns');
      snapshot.summaryHeaders.forEach((width, index) => {
        assert.ok(width >= summaryColumnMinimums[index], `Summary column ${index + 1} should not collapse at ${snapshot.viewport}px`);
      });
      assert.ok(snapshot.releaseTableWidth >= 1172, `The expanded release table should keep its minimum width at ${snapshot.viewport}px`);
      assert.ok(snapshot.releaseHeaders[0] <= 60, `The nested Rank header should retain its own width at ${snapshot.viewport}px`);
      assert.ok(snapshot.releaseHeaders[1] <= 75, `The nested Protocol header should retain its own width at ${snapshot.viewport}px`);
      if (snapshot.viewport <= 768) {
        assert.ok(snapshot.summaryScroller.scrollWidth > snapshot.summaryScroller.clientWidth, 'The summary Scroller should expose horizontal overflow on narrow screens');
        assert.ok(snapshot.releaseScroller.scrollWidth > snapshot.releaseScroller.clientWidth, 'The expanded release Scroller should expose horizontal overflow on narrow screens');
      }
    }
    await page.setViewportSize({ width: 1280, height: 900 });
    for (const title of ['Unmapped Expanded Release', 'Mismatched Expanded Release']) {
      const expandedRow = page.getByText(title, { exact: true }).locator('xpath=ancestor::tr[1]');
      const grabButton = expandedRow.getByRole('button').last();
      assert.equal(await grabButton.isDisabled(), true, `${title} expanded grab should be disabled`);
      assert.equal(await grabButton.getAttribute('title'), mismatchTooltip);
      await grabButton.click({ force: true });
      assert.equal(await page.getByText('Grab Release', { exact: true }).count(), 0, `${title} must not open the manual confirmation`);
      assert.equal(grabRequests.length, 0, `${title} must not POST a grab`);
    }
    const authorMismatchRow = page.getByText('Author Mismatch Expanded Release', { exact: true }).locator('xpath=ancestor::tr[1]');
    await authorMismatchRow.locator('td').nth(12).locator('span').first().hover();
    await page.getByText('Author mismatch: Madeline Miller', { exact: true }).waitFor();
    console.log('The release rejection popover exposes the complete author mismatch reason.');
    console.log('Sequential SignalR groups rendered; unmapped and mismatched scoped grabs are disabled in the summary and expanded rows.');

    const wrongAuthorEvent = {
      searchId, authorId: 11, mediaType: 'audiobook',
      bookResults: [makeBookResult(999, 'Wrong Author Book', 'complete', [makeRelease({ guid: 'wrong-author', title: 'Wrong Author Release', bookId: 999, rank: 1 })])],
      isComplete: false, isCancelled: false
    };
    const beforeWrongAuthorEvent = JSON.stringify(await page.evaluate(() => window.readAuthorSearchState()));
    await page.evaluate((body) => window.sendAuthorSearchSignalR(body), wrongAuthorEvent);
    assert.equal(JSON.stringify(await page.evaluate(() => window.readAuthorSearchState())), beforeWrongAuthorEvent, 'Late events for another author must be ignored');

    await page.getByRole('button', { name: 'Open book modal', exact: true }).click();
    await page.waitForFunction(() => window.readReleaseResults().hiddenItems.length === 1);
    assert.deepEqual(releaseRequests, [{ bookId: '900', initialMediaType: 'audiobook', bypassFilters: 'false' }]);
    searchState = await page.evaluate(() => window.readAuthorSearchState());
    assert.equal(searchState.isFetching, true, 'The book modal request must not clear or finish the author search');
    assert.deepEqual(searchState.bookResults.map((result) => result.bookId), [101, 102, 103, 104, 105, 106, 107]);
    await page.getByRole('button', { name: 'Close book modal', exact: true }).click();
    searchState = await page.evaluate(() => window.readAuthorSearchState());
    assert.equal(searchState.isFetching, true, 'Closing the book modal must not abort the author search');
    assert.equal(heldAuthorRoutes.has(searchId), true);
    assert.equal(requestFailures.some((failure) => failure.url.includes(`searchId=${searchId}`)), false);
    console.log('Book modal request preserved the active author search.');

    const bookARow = page.getByRole('row').filter({ hasText: 'Book A' }).first();
    const grabRequest = page.waitForRequest((request) => request.method() === 'POST' && new URL(request.url()).pathname === '/api/v1/release');
    await bookARow.locator('[title="Add to downloaded queue"]').click();
    const grabbedPayload = (await grabRequest).postDataJSON();
    assert.deepEqual(grabbedPayload, {
      guid: 'shared-guid', indexerId: 17, authorId: 10, bookId: 101, searchId, searchedBookId: 101
    });
    await page.waitForFunction(() => window.readAuthorSearchState().bookResults[0].releases.some((release) => release.guid === 'shared-guid' && release.isGrabbed));
    searchState = await page.evaluate(() => window.readAuthorSearchState());
    assert.equal(searchState.bookResults[0].releases.find((release) => release.guid === 'shared-guid').isGrabbed, true);
    assert.equal(searchState.bookResults[1].releases.find((release) => release.guid === 'shared-guid').isGrabbed, false, 'The same GUID in another book group keeps independent grab state');
    const expandedGrabRequest = page.waitForRequest((request) => request.method() === 'POST' && new URL(request.url()).pathname === '/api/v1/release');
    const validExpandedRow = page.getByText('Other Alpha', { exact: true }).locator('xpath=ancestor::tr[1]');
    await validExpandedRow.getByRole('button').last().click();
    const expandedGrabbedPayload = (await expandedGrabRequest).postDataJSON();
    assert.deepEqual(expandedGrabbedPayload, {
      guid: 'other-alpha', indexerId: 17, authorId: 10, bookId: 101, searchId, searchedBookId: 101
    });
    await page.waitForFunction(() => window.readAuthorSearchState().bookResults[0].releases.some((release) => release.guid === 'other-alpha' && release.isGrabbed));
    console.log('Valid scoped summary and expanded grabs passed with independent per-book state.');

    const finalAuthorResult = {
      searchId,
      authorId: 10,
      mediaType: 'audiobook',
      bookResults: [
        makeBookResult(101, 'Book A', 'complete', [preferredA, otherZuluA, otherAlphaA, unmappedExpandedA, mismatchedExpandedA, authorMismatchExpandedA], hiddenA),
        makeBookResult(102, 'Book B', 'pending', [sharedB], hiddenB),
        makeBookResult(103, 'Book C', 'complete', [mismatchedC]),
        waitingBook,
        emptyBook,
        failedBook,
        noPreferredBook
      ],
      isComplete: true,
      isCancelled: false
    };
    await heldAuthorRoutes.get(searchId).fulfill({ json: finalAuthorResult });
    heldAuthorRoutes.delete(searchId);
    await page.waitForFunction(() => window.readAuthorSearchState().isComplete === true);
    searchState = await page.evaluate(() => window.readAuthorSearchState());
    assert.equal(searchState.isFetching, false);
    assert.equal(searchState.bookResults[0].releases.find((release) => release.guid === 'shared-guid').isGrabbed, true, 'The final HTTP merge must preserve the prior grab state');
    assert.equal(searchState.bookResults[1].releases.find((release) => release.guid === 'shared-guid').isGrabbed, false);
    assert.equal(searchState.bookResults[0].releases.find((release) => release.guid === 'other-alpha').isGrabbed, true, 'The final HTTP merge must preserve expanded scoped grab state');
    await page.getByText('Search incomplete', { exact: true }).first().waitFor();
    console.log('Final HTTP merge preserved grab state and marks terminal pending books incomplete.');

    assert.equal(await summaryTable.locator(':scope > thead').getByRole('columnheader', { name: 'Rank', exact: true }).count(), 0);
    assert.match(await summaryTable.locator('tbody tr').first().innerText(), /Preferred Book A/);
    await page.getByRole('columnheader', { name: 'Title', exact: true }).last().click();
    assert.match(await summaryTable.locator('tbody tr').first().innerText(), /Preferred Book A/, 'Sorting other releases must keep the preferred release in the summary row');
    assert.equal(await page.getByText('Shared Book B', { exact: true }).count(), 1, 'Sorting hidden alternatives must keep the other preferred summary intact');

    await page.getByRole('button', { name: 'Show other releases for Book B', exact: true }).click();
    await page.getByRole('button', { name: /Show 2 Hidden Results/ }).click();
    const hiddenBookBTable = page.getByText('Hidden Alpha B', { exact: true }).locator('xpath=ancestor::table[1]');
    const hiddenBookBRows = await hiddenBookBTable.locator('tbody tr').allInnerTexts();
    assert.match(hiddenBookBRows[0], /Hidden Alpha B/);
    assert.match(hiddenBookBRows[1], /Hidden Zulu B/);
    await page.getByRole('button', { name: 'Hide other releases for Book B', exact: true }).click();

    await page.getByRole('button', { name: /Show 51 Hidden Results/ }).click();
    await page.getByText('Showing 1-50 of 51', { exact: true }).waitFor();
    assert.equal(await page.getByText('Hidden A 01', { exact: true }).count(), 1);
    assert.equal(await page.getByText('Hidden A 51', { exact: true }).count(), 0);
    await page.getByRole('button', { name: 'Next', exact: true }).last().click();
    await page.getByText('Showing 51-51 of 51', { exact: true }).waitFor();
    assert.equal(await page.getByText('Hidden A 51', { exact: true }).count(), 1);
    await page.getByRole('button', { name: 'Previous', exact: true }).last().click();
    await page.getByText('Showing 1-50 of 51', { exact: true }).waitFor();

    const beforeTerminalLateEvent = JSON.stringify(await page.evaluate(() => window.readAuthorSearchState()));
    await page.evaluate((body) => window.sendAuthorSearchSignalR(body), {
      searchId, authorId: 10, mediaType: 'audiobook', bookResults: [bookA], isComplete: false, isCancelled: false
    });
    assert.equal(JSON.stringify(await page.evaluate(() => window.readAuthorSearchState())), beforeTerminalLateEvent, 'SignalR updates after the final response must be ignored');

    assert.deepEqual(errors, []);
    assert.deepEqual(grabRequests, [grabbedPayload, expandedGrabbedPayload]);
    await page.screenshot({ path: path.join(os.tmpdir(), 'chaptarr-author-search-desktop.png'), fullPage: true });
    await page.setViewportSize({ width: 390, height: 844 });
    await page.screenshot({ path: path.join(os.tmpdir(), 'chaptarr-author-search-mobile.png'), fullPage: true });
    assert.equal(requestFailures.some((failure) => failure.url.includes(`searchId=${searchId}`)), false);

    await page.evaluate(() => window.renderAuthorSearchFixture('legacy-grab', 0, 0));
    const legacyRow = page.getByRole('row').filter({ hasText: 'Legacy Unmapped Release' });
    const legacyGrabButton = legacyRow.getByRole('button').last();
    assert.equal(await legacyGrabButton.isDisabled(), false, 'An unscoped legacy row should retain its manual grab action');
    const beforeLegacyGrabCount = grabRequests.length;
    await legacyGrabButton.click();
    await page.getByText('Grab Release', { exact: true }).waitFor();
    assert.equal(grabRequests.length, beforeLegacyGrabCount, 'Legacy manual rows still require confirmation before POST');
    const legacyGrabRequest = page.waitForRequest((request) => request.method() === 'POST' && new URL(request.url()).pathname === '/api/v1/release');
    await page.getByRole('button', { name: 'Grab', exact: true }).click();
    const legacyGrabbedPayload = (await legacyGrabRequest).postDataJSON();
    assert.deepEqual(legacyGrabbedPayload, {
      guid: 'legacy-unmapped', indexerId: 17, bookId: 900, initialMediaType: 'audiobook', bypassFilters: false
    });
    assert.deepEqual(grabRequests, [grabbedPayload, expandedGrabbedPayload, legacyGrabbedPayload]);
    assert.equal(await page.getByText('Grab Release', { exact: true }).count(), 0);

    authorRequests.length = 0;
    await page.evaluate(() => window.renderAuthorSearchFixture('tab', 2, 3, { selectedMediaType: 'audiobook' }));
    let resolveCancelledAuthorRequest = null;
    const cancelledAuthorRequest = new Promise((resolve) => {
      resolveCancelledAuthorRequest = resolve;
    });
    holdNextAuthorSearch = true;
    heldAuthorRequestResolver = resolveCancelledAuthorRequest;
    await page.getByRole('button', { name: 'Search', exact: true }).click();
    const cancelledRequestParams = await cancelledAuthorRequest;
    const cancelledSearchId = cancelledRequestParams.searchId;

    const keptRelease = makeRelease({
      guid: 'cancel-kept-guid', title: 'Completed Before Stop', bookId: 201, rank: 1, preferred: true
    });
    const cancelledRoster = [
      makeBookResult(201, 'Completed Before Stop', 'pending', []),
      makeBookResult(202, 'Pending At Stop', 'pending', []),
      makeBookResult(203, 'Not Started', 'pending', [])
    ];
    await page.evaluate((body) => window.sendAuthorSearchSignalR(body), {
      searchId: cancelledSearchId,
      authorId: 10,
      mediaType: 'audiobook',
      bookResults: cancelledRoster,
      isComplete: false,
      isCancelled: false
    });
    await page.evaluate((body) => window.sendAuthorSearchSignalR(body), {
      searchId: cancelledSearchId,
      authorId: 10,
      mediaType: 'audiobook',
      bookResults: [makeBookResult(201, 'Completed Before Stop', 'complete', [keptRelease])],
      isComplete: false,
      isCancelled: false
    });
    const keptResultRow = page.getByRole('row').filter({ hasText: 'Completed Before Stop' }).first();
    await keptResultRow.locator('[title="Add to downloaded queue"]').click();
    await page.waitForFunction(() => window.readAuthorSearchState().bookResults
      .find((result) => result.bookId === 201)?.releases
      .some((release) => release.guid === 'cancel-kept-guid' && release.isGrabbed));

    for (const title of ['Pending At Stop', 'Not Started']) {
      const row = page.getByRole('row').filter({ hasText: title }).first();
      assert.match(await row.innerText(), /Waiting for results/, `${title} should show its waiting status before cancellation`);
    }

    const abortedSearchRequest = page.waitForEvent('requestfailed', (request) =>
      request.url().includes(`searchId=${cancelledSearchId}`));
    await Promise.all([
      abortedSearchRequest,
      page.evaluate((cancelledId) => window.cancelAuthorSearchRequest(cancelledId), cancelledSearchId)
    ]);
    await page.evaluate((body) => window.sendAuthorSearchSignalR(body), {
      searchId: cancelledSearchId,
      authorId: 10,
      mediaType: 'audiobook',
      bookResults: [],
      isComplete: true,
      isCancelled: true
    });
    await page.waitForFunction(() => {
      const state = window.readAuthorSearchState();
      return state.isComplete && state.isCancelled && !state.isFetching;
    });
    searchState = await page.evaluate(() => window.readAuthorSearchState());
    assert.equal(searchState.bookResults[0].releases[0].isGrabbed, true, 'A grabbed result must survive terminal cancellation');
    assert.equal(searchState.bookResults[1].status, 'pending', 'Cancellation may leave unfinished groups pending in state');
    for (const title of ['Pending At Stop', 'Not Started']) {
      const row = page.getByRole('row').filter({ hasText: title }).first();
      assert.match(await row.innerText(), /Search stopped before all books completed\./, `${title} should show the stopped status`);
    }
    assert.equal(await page.locator('.followingBalls').count(), 0, 'Terminal cancellation should leave no active loader');
    assert.equal(await page.getByText('Search stopped before all books completed.', { exact: true }).count(), 3,
      'The alert and both unfinished book rows should show the stopped status');
    assert.deepEqual(errors, []);
    heldAuthorRoutes.delete(cancelledSearchId);

    console.log('Author-search browser check passed: confirmation and media boundaries, production tab visibility, scoped invalid/valid grabs, legacy confirmation, incremental groups, final merge, preferred summary and hidden-results paging.');
  } finally {
    await browser.close();
    fs.rmSync(output, { recursive: true, force: true });
  }
})().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
