/* Verify the approved Wanted table and existing pending-author operations in Chromium. */
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const webpack = require('webpack');
const { chromium } = require('playwright');

const root = path.resolve(__dirname, '../..');
const output = fs.mkdtempSync(path.join(os.tmpdir(), 'chaptarr-wanted-authors-'));
const artifacts = process.env.CHAPTARR_LAYOUT_ARTIFACTS || fs.mkdtempSync(path.join(os.tmpdir(), 'chaptarr-wanted-authors-evidence-'));
fs.mkdirSync(artifacts, { recursive: true });
const appRootStyle = fs.readFileSync(path.join(root, 'frontend/src/index.ejs'), 'utf8').match(/<style>([\s\S]*?)<\/style>/)[1];
const traces = [];
const strings = JSON.parse(fs.readFileSync(path.join(root, 'src/NzbDrone.Core/Localization/Core/en.json')));
const config = require('./webpack.config')({ production: true });

config.entry = { fixture: path.join(__dirname, 'wanted-authors-fixture.js') };
config.output = { ...config.output, path: output, filename: 'fixture.js', publicPath: '/' };
config.optimization = { ...config.optimization, minimize: false, splitChunks: false, runtimeChunk: false };
config.plugins = config.plugins.filter((plugin) => ['DefinePlugin', 'MiniCssExtractPlugin'].includes(plugin.constructor.name));
for (const rule of config.module.rules) {
  if (Array.isArray(rule.use)) {
    rule.use = rule.use.filter((loader) => !String(typeof loader === 'string' ? loader : loader.loader).includes('css-modules-typescript-loader'));
  }
}

function compile() {
  const compiler = webpack(config);
  return new Promise((resolve, reject) => compiler.run((error, stats) => {
    compiler.close(() => {});
    if (error || stats.hasErrors()) {
      reject(error || new Error(stats.toString({ all: false, errors: true })));
    } else {
      resolve();
    }
  }));
}

function records() {
  const statuses = ['NotRequested', 'Pending', 'InProgress', 'Retrying', 'PartialSuccess', 'Succeeded', 'Failed'];
  return Array.from({ length: 1000 }, (_, index) => ({
    id: index + 1,
    authorName: `Queued Writer ${String(index + 1).padStart(4, '0')}`,
    providerId: `hc:${index + 1}`,
    createdAt: new Date(Date.UTC(2026, 0, 1) + index * 60000).toISOString(),
    overallStatus: statuses[(index + 1) % statuses.length],
    audiobookStatus: statuses[(index + 1) % statuses.length],
    ebookStatus: 'NotRequested',
    lastError: 'Validation failed: -- providerId: Author gr:58 is excluded from import lists.'
  }));
}

async function open(browser, width = 1280, surface = 'authors', scenario = {}) {
  const page = await browser.newPage({ viewport: { width, height: 900 } });
  const errors = [];
  const consoleMessages = [];
  page.on('console', (message) => {
    if (['error', 'warning'].includes(message.type())) {
      consoleMessages.push({ type: message.type(), text: message.text() });
    }
  });
  page.on('pageerror', (error) => errors.push(error.message));
  const data = scenario.records || records();
  const requests = [];
  const mutations = [];
  const held = [];
  traces.push({ width, surface, requests, mutations, errors, consoleMessages });
  let holdPage = !!scenario.holdPage;
  let holdAction = false;
  let failPage = false;
  let failAction = false;
  let empty = false;
  const cssName = fs.readdirSync(path.join(output, 'Content')).find((name) => name.endsWith('.css'));
  const handlePage = (route) => {
    const params = new URL(route.request().url()).searchParams;
    const pageNumber = Number(params.get('page') || 1);
    const pageSize = Number(params.get('pageSize') || 10);
    const key = params.get('sortKey') || 'id';
    const descending = (params.get('sortDirection') || 'descending').toLowerCase() === 'descending';
    const sorted = [...data].sort((left, right) => {
      const value = typeof left[key] === 'number' ? left[key] - right[key] : String(left[key]).localeCompare(String(right[key]));
      return descending ? -value : value;
    });
    return route.fulfill({ contentType: 'application/json', body: JSON.stringify(scenario.envelope || {
      page: pageNumber, pageSize, sortKey: key, sortDirection: descending ? 'descending' : 'ascending',
      totalRecords: empty ? 0 : sorted.length,
      records: empty ? [] : sorted.slice((pageNumber - 1) * pageSize, pageNumber * pageSize)
    }) });
  };
  await page.route('**/*', (route) => {
    const request = route.request();
    const pathname = new URL(request.url()).pathname;
    if (pathname === '/') {
      return route.fulfill({ contentType: 'text/html', body: `<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><link rel="stylesheet" href="/style.css"><style>${appRootStyle}</style></head><body><div id="root" class="root"></div><div id="portal-root"></div><script>window.Chaptarr={urlBase:"",apiRoot:"/api/v1",isProduction:true,instanceName:"Chaptarr",apiKey:"fixture"};</script><script src="/fixture.js"></script></body></html>` });
    }
    if (pathname === '/fixture.js') {
      return route.fulfill({ path: path.join(output, 'fixture.js'), contentType: 'text/javascript' });
    }
    if (pathname === '/Content/Images/chaptarr-logo.svg') {
      return route.fulfill({ path: path.join(root, 'frontend/src/Content/Images/chaptarr-logo.svg'), contentType: 'image/svg+xml' });
    }
    if (pathname === '/style.css') {
      return route.fulfill({ path: path.join(output, 'Content', cssName), contentType: 'text/css' });
    }
    if (pathname === '/api/v1/localization') {
      return route.fulfill({ contentType: 'application/json', body: JSON.stringify({ Strings: scenario.strings || strings }) });
    }
    if (pathname === '/api/v1/pendingauthorimport/paged') {
      requests.push({ method: request.method(), url: request.url() });
      if (holdPage) {
        held.push(() => handlePage(route));
        return undefined;
      }
      if (failPage) {
        return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: 'PRIVATE_RETURNED_REASON' }) });
      }
      return handlePage(route);
    }
    if ((/^\/api\/v1\/pendingauthorimport\/\d+(\/retry)?$/).test(pathname)) {
      const id = Number(pathname.match(/\d+(?=\/retry$|$)/)[0]);
      const name = pathname.endsWith('/retry') ? 'retry' : 'cancel';
      const actionName = new URL(request.url()).searchParams.get('delete') === 'true' ? 'delete' : name;
      mutations.push({ id, name: actionName, url: request.url(), method: request.method(), body: request.postData(), pathname });
      const respond = () => {
        if (failAction) {
          return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: 'PRIVATE_RETURNED_REASON' }) });
        }
        if (actionName === 'delete') {
          data.splice(data.findIndex((item) => item.id === id), 1);
        } else {
          const item = data.find((record) => record.id === id);
          item.overallStatus = name === 'retry' ? 'Retrying' : 'Failed';
          if (name === 'cancel') {
            item.audiobookStatus = 'NotRequested';
            item.ebookStatus = 'NotRequested';
          }
        }
        const messages = { retry: 'Retry scheduled', cancel: 'Pending import cancelled', delete: 'Pending import deleted' };
        return route.fulfill({ contentType: 'application/json', body: JSON.stringify({ message: messages[actionName] }) });
      };
      if (holdAction) {
        held.push(respond);
        return undefined;
      }
      return respond();
    }
    if (pathname === '/api/v1/wanted/missing') {
      return route.fulfill({ contentType: 'application/json', body: JSON.stringify({
        page: 1, pageSize: 20, totalRecords: 5, records: Array.from({ length: 5 }, (_, index) => ({
          id: index + 1, authorId: 1, author: { id: 1, authorName: 'Reference Writer', titleSlug: 'reference-writer' },
          title: `Reference Book ${index + 1}`, titleSlug: `reference-book-${index + 1}`, mediaType: 'ebook', releaseDate: '2026-01-01T00:00:00Z'
        }))
      }) });
    }
    if (pathname.startsWith('/api/v1/queue/details')) {
      return route.fulfill({ contentType: 'application/json', body: '[]' });
    }
    const assetPath = path.resolve(output, `.${decodeURIComponent(pathname)}`);
    if (assetPath.startsWith(`${output}${path.sep}`) && fs.existsSync(assetPath) && fs.statSync(assetPath).isFile()) {
      return route.fulfill({ path: assetPath });
    }
    return route.fulfill({ contentType: 'application/json', body: '{}' });
  });
  await page.goto(`http://wanted-authors.test/?surface=${surface}`);
  try {
    await page.waitForFunction(() => window.fixtureReady);
  } catch (error) {
    console.error('Fixture page errors:', errors);
    throw error;
  }
  return {
    page, data, errors, requests, mutations,
    holdPages: () => {
      holdPage = true;
    }, holdActions: () => {
      holdAction = true;
    },
    failPages: (value) => {
      failPage = value;
    }, failActions: (value) => {
      failAction = value;
    },
    setEmpty: (value) => {
      empty = value;
    },
    release: async() => {
      holdPage = false;
      holdAction = false;
      for (const respond of held.splice(0)) {
        await respond();
      }
    }
  };
}

async function populated(page) {
  await page.waitForFunction(() => window.getWantedAuthorsState().isPopulated && !window.getWantedAuthorsState().isFetching && !window.getWantedAuthorsState().pendingAction);
}

async function noPrivateReason(page) {
  assert.equal(await page.getByText('PRIVATE_RETURNED_REASON', { exact: false }).count(), 0);
  assert.equal(await page.locator('[title*="PRIVATE_RETURNED_REASON"]').count(), 0);
}

async function visibleRows(page) {
  await page.waitForFunction(() => {
    const wrapper = document.querySelector('[class*="PageTransitionWrapper-wrapper"]');
    return !wrapper || getComputedStyle(wrapper).opacity === '1';
  });
  const geometry = await page.locator('tbody tr').first().evaluate((row) => {
    const bounds = row.getBoundingClientRect();
    const scroller = row.closest('[class*="PageContentBody-contentBody"]').getBoundingClientRect();
    return { row: bounds.toJSON(), scroller: scroller.toJSON(), viewport: { width: innerWidth, height: innerHeight } };
  });
  assert.equal(geometry.scroller.height > 0, true, 'Real page body must have visible height');
  assert.equal(Math.min(geometry.row.bottom, geometry.scroller.bottom, geometry.viewport.height) > Math.max(geometry.row.top, geometry.scroller.top, 0), true, 'First row must intersect the actual visible scroller and viewport');
  return geometry;
}

async function capture(page, name) {
  await page.screenshot({ path: path.join(artifacts, `${name}.png`), fullPage: true });
}

(async() => {
  let browser = null;
  const checks = [];
  const receipts = [];
  try {
    await compile();
    browser = await chromium.launch();
    const waitingRows = [
      { id: 1300, authorName: 'Future retry', nextAttemptAt: '2050-01-01T12:34:00Z', overallStatus: 'Retrying' },
      { id: 1301, authorName: 'Past retry', nextAttemptAt: '2000-01-01T12:34:00Z', overallStatus: 'Retrying' },
      { id: 1302, authorName: 'Retry without date', overallStatus: 'Retrying' },
      { id: 1304, authorName: 'Retry invalid date', nextAttemptAt: 'invalid-date', overallStatus: 'Retrying' },
      { id: 1303, authorName: 'Actual active phase', nextAttemptAt: '2050-01-01T12:34:00Z', overallStatus: 'InProgress', audiobookStatus: 'InProgress', ebookStatus: 'Pending' }
    ].map((row) => ({ audiobookStatus: 'Pending', ebookStatus: 'NotRequested', providerId: `hc:${row.id}`, createdAt: '2026-01-01T00:00:00Z', ...row }));
    for (const width of [1280, 375]) {
      const fixture = await open(browser, width, 'authors', { records: waitingRows.map((row) => ({ ...row })) });
      const { page } = fixture;
      await populated(page);
      await visibleRows(page);
      for (const item of waitingRows) {
        const row = page.getByRole('row').filter({ hasText: item.providerId });
        const status = row.locator('td').nth(3);
        if (item.overallStatus === 'Retrying') {
          assert.match(await status.innerText(), /^Waiting(?:\n|$)/, 'Scheduled retries are Waiting, including past or absent eligibility times');
          if (item.nextAttemptAt && item.nextAttemptAt !== 'invalid-date') {
            assert.match(await status.innerText(), /Next check after Jan 1 (?:2050|2000) 12:34/, 'Reuse the configured date and clock formats; eligibility is not a countdown');
          } else {
            assert.equal(await status.innerText(), 'Waiting');
          }
        } else {
          assert.equal(await status.innerText(), strings.InProgress, 'Only the actual API active phase displays In progress');
          assert.equal(await status.getByText('Next check after', { exact: false }).count(), 0);
        }
        assert.equal(await row.getByRole('button', { name: strings.Retry, exact: true }).count(), 1);
      }
      const retry = page.getByRole('row').filter({ hasText: 'hc:1300' }).getByRole('button', { name: strings.Retry, exact: true });
      await retry.focus();
      await page.keyboard.press('Enter');
      await populated(page);
      assert.deepEqual(fixture.mutations.map(({ method, pathname, body }) => ({ method, pathname, body })), [{ method: 'POST', pathname: '/api/v1/pendingauthorimport/1300/retry', body: null }]);
      receipts.push({ width, scenario: 'Waiting future/past/absent nextAttemptAt; actual InProgress and manual Retry preserved', mutations: fixture.mutations });
      await capture(page, `waiting-status-${width}`);
      await page.close();
    }
    checks.push('Waiting uses actual retry eligibility and existing date/time preferences without changing manual Retry');
    const eligibilityCases = [
      { name: 'cancelled', audiobookStatus: 'NotRequested', ebookStatus: 'NotRequested', overallStatus: 'Failed', expectedRetry: false },
      { name: 'succeeded', audiobookStatus: 'Succeeded', ebookStatus: 'NotRequested', overallStatus: 'Succeeded', expectedRetry: false },
      { name: 'not requested', audiobookStatus: 'NotRequested', ebookStatus: 'NotRequested', overallStatus: 'NotRequested', expectedRetry: false },
      { name: 'no-side retry', audiobookStatus: 'NotRequested', ebookStatus: 'NotRequested', overallStatus: 'Retrying', expectedRetry: false },
      { name: 'missing sides', overallStatus: 'Failed', expectedRetry: false },
      { name: 'audiobook failed', audiobookStatus: 'Failed', ebookStatus: 'NotRequested', overallStatus: 'Failed', expectedRetry: true },
      { name: 'ebook failed', audiobookStatus: 'NotRequested', ebookStatus: 'Failed', overallStatus: 'Failed', expectedRetry: true },
      { name: 'partial success', audiobookStatus: 'Succeeded', ebookStatus: 'Failed', overallStatus: 'PartialSuccess', expectedRetry: true },
      { name: 'active request', audiobookStatus: 'Pending', ebookStatus: 'NotRequested', overallStatus: 'Pending', expectedRetry: true }
    ].map((item, index) => ({ ...item, id: 1100 + index, providerId: `hc:${1100 + index}`, authorName: item.name, createdAt: '2026-01-01T00:00:00Z' }));
    for (const width of [1280, 375]) {
      const eligibility = await open(browser, width, 'authors', { records: eligibilityCases.map((item) => ({ ...item })) });
      await populated(eligibility.page);
      await visibleRows(eligibility.page);
      for (const record of eligibilityCases) {
        const row = eligibility.page.locator('tbody tr').filter({ hasText: record.providerId });
        const retry = row.getByRole('button', { name: strings.Retry, exact: true });
        assert.equal(await retry.count(), record.expectedRetry ? 1 : 0, `${record.name}: Retry must be ${record.expectedRetry ? 'available' : 'absent'}`);
        if (record.expectedRetry) {
          assert.equal(await retry.isEnabled(), true);
        }
        assert.equal(await row.getByRole('button', { name: strings.CancelImport, exact: true }).isDisabled(), !['Pending', 'InProgress', 'Retrying'].includes(record.overallStatus));
        assert.equal(await row.getByRole('button', { name: strings.Delete, exact: true }).isEnabled(), true);
      }
      assert.equal(eligibility.mutations.length, 0);
      const ebook = eligibilityCases.find((record) => record.name === 'ebook failed');
      const retry = eligibility.page.locator('tbody tr').filter({ hasText: ebook.providerId }).getByRole('button', { name: strings.Retry, exact: true });
      await retry.scrollIntoViewIfNeeded();
      await capture(eligibility.page, `authors-retry-eligibility-${width}`);
      await retry.focus();
      await eligibility.page.keyboard.press('Enter');
      await populated(eligibility.page);
      assert.equal(eligibility.mutations.length, 1);
      assert.equal(eligibility.mutations[0].method, 'POST');
      assert.equal(eligibility.mutations[0].pathname, `/api/v1/pendingauthorimport/${ebook.id}/retry`);
      assert.equal(eligibility.mutations[0].body, null);
      assert.equal(await retry.isEnabled(), true);
      assert.deepEqual(eligibility.errors, []);
      receipts.push({ scenario: 'Retry eligibility from actual requested-side statuses', width, cases: eligibilityCases, mutations: eligibility.mutations });
      await eligibility.page.close();
    }
    checks.push('default desktop/phone no-side and Succeeded rows omit Retry; audio/ebook/partial/active requests retain existing keyboard retry,Cancel,Delete');

    for (const width of [1280, 375]) {
      const partial = await open(browser, width, 'authors', { records: [{
        ...records()[0], id: 1, authorName: 'Frank Herbert', providerId: 'gr:58',
        audiobookStatus: 'Succeeded', ebookStatus: 'Failed', overallStatus: 'PartialSuccess',
        lastError: 'Author gr:58 is excluded from import lists for Ebook.'
      }] });
      await populated(partial.page);
      const row = partial.page.locator('tbody tr');
      assert.equal(await row.getByText(strings.PartialSuccess, { exact: true }).count(), 1);
      assert.equal(await row.getByText('Author gr:58 is excluded from import lists for Ebook.', { exact: true }).count(), 0);
      assert.deepEqual(partial.errors, []);
      await capture(partial.page, `authors-partial-reason-${width}`);
      await partial.page.close();
    }
    checks.push('partial success keeps consistent status without exposing raw failure reasons');

    const desktop = await open(browser);
    const { page } = desktop;
    await populated(page);
    assert.equal(await page.locator('tbody tr').count(), 20);
    assert.equal(await page.evaluate(() => window.getWantedAuthorsState().totalRecords), 1000);
    assert.equal(desktop.requests.length, 1);
    assert.equal(new URL(desktop.requests[0].url).searchParams.get('pageSize'), '20');
    assert.equal(await page.getByText(strings.NotAvailable, { exact: true }).count() > 0, true);
    assert.equal(await page.getByText('Validation failed: -- providerId: Author gr:58 is excluded from import lists.', { exact: true }).count(), 0);
    await noPrivateReason(page);
    receipts.push({ scenario: 'visible desktop geometry', geometry: await visibleRows(page) });
    await capture(page, 'authors-desktop-light');
    checks.push('1000 records,20 DOM rows,one bounded default GET; actual enum labels, unavailable status; no raw HTTP exception details');

    await page.getByRole('button', { name: '1 / 50', exact: true }).click();
    await page.locator('select[name="pageSelect"]').selectOption('2');
    await populated(page);
    assert.equal(await page.evaluate(() => window.getWantedAuthorsState().page), 2);
    const retryRow = page.locator('tbody tr').filter({ hasText: 'hc:979' });
    desktop.holdActions();
    await retryRow.getByRole('button', { name: strings.Retry, exact: true }).focus();
    await page.keyboard.press('Enter');
    await page.waitForFunction(() => window.getWantedAuthorsState().pendingAction?.name === 'retry');
    assert.equal(await retryRow.getByRole('button', { name: strings.Retry, exact: true }).isDisabled(), true);
    await desktop.release();
    await populated(page);
    assert.equal(desktop.mutations[0].method, 'POST');
    assert.equal(desktop.mutations[0].pathname, '/api/v1/pendingauthorimport/979/retry');
    assert.equal(desktop.mutations[0].body, null);
    assert.equal(await page.evaluate(() => window.getWantedAuthorsState().page), 2);
    assert.equal(await retryRow.getByText(strings.Waiting, { exact: true }).count(), 1);
    checks.push('keyboard retry exact POST/no body,spinner recovery,current page retained');

    const cancelRow = page.locator('tbody tr').filter({ hasText: 'hc:974' });
    await cancelRow.getByRole('button', { name: strings.CancelImport, exact: true }).click();
    let modal = page.locator('[class*="ModalContent-modalContent"]');
    await modal.getByRole('button', { name: strings.Cancel, exact: true }).focus();
    await page.keyboard.press('Enter');
    await modal.waitFor({ state: 'hidden' });
    assert.equal(desktop.mutations.length, 1);
    await cancelRow.getByRole('button', { name: strings.CancelImport, exact: true }).click();
    await page.keyboard.press('Escape');
    await modal.waitFor({ state: 'hidden' });
    assert.equal(desktop.mutations.length, 1);
    await cancelRow.getByRole('button', { name: strings.CancelImport, exact: true }).click();
    await modal.getByRole('button', { name: strings.CancelImport, exact: true }).focus();
    await page.keyboard.press('Enter');
    await populated(page);
    assert.equal(desktop.mutations[1].method, 'DELETE');
    assert.equal(desktop.mutations[1].pathname, '/api/v1/pendingauthorimport/974');
    assert.equal(desktop.mutations[1].body, null);
    assert.equal(await page.evaluate(() => window.getWantedAuthorsState().page), 2);
    assert.equal(await cancelRow.getByText(strings.NotAvailable, { exact: true }).count(), 1);
    assert.equal(await cancelRow.getByRole('button', { name: strings.CancelImport, exact: true }).isDisabled(), true);
    assert.equal(await cancelRow.getByRole('button', { name: strings.Retry, exact: true }).count(), 0);
    assert.equal(await cancelRow.getByRole('button', { name: strings.Delete, exact: true }).isEnabled(), true);
    assert.deepEqual(await page.evaluate(() => {
      const item = window.getWantedAuthorsState().items.find((record) => record.id === 974);
      return [item.audiobookStatus, item.ebookStatus];
    }), ['NotRequested', 'NotRequested']);
    checks.push('Cancel/Escape do not mutate;confirmed keyboard DELETE,no row removal/guessed cancellation status,page preserved');

    desktop.failActions(true);
    await retryRow.getByRole('button', { name: strings.Retry, exact: true }).click();
    await page.getByText(strings.ErrorUpdatingPendingAuthorImport, { exact: true }).waitFor();
    assert.equal(await retryRow.getByRole('button', { name: strings.Retry, exact: true }).isDisabled(), false);
    await noPrivateReason(page);
    const errorBounds = await page.getByText(strings.ErrorUpdatingPendingAuthorImport, { exact: true }).boundingBox();
    const bodyBounds = await page.locator('[class*="PageContentBody-contentBody"]').boundingBox();
    receipts.push({ scenario: 'desktop action failure actual viewport geometry', errorBounds, bodyBounds });
    assert.equal(Math.min(errorBounds.y + errorBounds.height, bodyBounds.y + bodyBounds.height, 900) > Math.max(errorBounds.y, bodyBounds.y, 0), true, 'Action error must intersect the visible page body');
    await capture(page, 'authors-action-error-desktop-light');
    desktop.failActions(false);
    await retryRow.getByRole('button', { name: strings.Retry, exact: true }).click();
    await populated(page);
    assert.equal(await page.getByText(strings.ErrorUpdatingPendingAuthorImport, { exact: true }).count(), 0);
    checks.push('action failure visible,generic,recoverable;successful retry clears error');

    await page.getByRole('button', { name: strings.Options, exact: true }).click();
    modal = page.locator('[class*="ModalContent-modalContent"]');
    await modal.waitFor();
    await capture(page, 'authors-options-desktop-light');
    const pageSizeInput = modal.locator('input[name="pageSize"]');
    await pageSizeInput.fill('50');
    await populated(page);
    assert.equal(await page.locator('tbody tr').count(), 50);
    assert.equal(await page.evaluate(() => window.getWantedAuthorsState().page), 1);
    assert.equal(new URL(desktop.requests.at(-1).url).searchParams.get('pageSize'), '50');
    const validRequests = desktop.requests.length;
    await pageSizeInput.fill('251');
    await modal.getByText('Page size must not exceed 250', { exact: true }).waitFor();
    assert.equal(desktop.requests.length, validRequests);
    await page.keyboard.press('Escape');
    await modal.waitFor({ state: 'hidden' });
    await page.locator('thead th').filter({ hasText: strings.AuthorName }).click();
    await populated(page);
    assert.equal(new URL(desktop.requests.at(-1).url).searchParams.get('sortKey'), 'authorName');
    assert.equal(await page.evaluate(() => window.getWantedAuthorsState().items[0].providerId), 'hc:1000');
    await page.locator('thead th').filter({ hasText: strings.AuthorName }).click();
    await populated(page);
    assert.equal(new URL(desktop.requests.at(-1).url).searchParams.get('sortDirection'), 'ascending');
    assert.equal(await page.evaluate(() => window.getWantedAuthorsState().items[0].providerId), 'hc:1');
    checks.push('incumbent table options applies bounded50,returns first page,rejects251 without fetch;existing sort and Escape work');
    receipts.push({ scenario: 'default desktop', requests: desktop.requests, mutations: desktop.mutations });
    assert.deepEqual(desktop.errors, []);
    await page.close();

    const mobile = await open(browser, 375, 'authors', { holdPage: true });
    assert.equal(await mobile.page.evaluate(() => window.getWantedAuthorsState().isFetching), true);
    assert.equal(await mobile.page.locator('tbody tr').count(), 0);
    await mobile.release();
    await populated(mobile.page);
    assert.equal(await mobile.page.locator('tbody tr').count(), 20);
    const overflow = await mobile.page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth);
    assert.equal(overflow, false, 'Existing horizontal Table scroller keeps the page in the phone viewport');
    receipts.push({ scenario: 'visible phone geometry', geometry: await visibleRows(mobile.page) });
    await capture(mobile.page, 'authors-phone-dark');
    await mobile.page.locator('div[class*="Table-tableContainer"]').evaluate((table) => {
      table.scrollLeft = table.scrollWidth;
    });
    const phoneActions = mobile.page.locator('tbody tr').first();
    const deleteBounds = await phoneActions.getByRole('button', { name: strings.Delete, exact: true }).boundingBox();
    assert.equal(deleteBounds.x >= 0 && deleteBounds.x + deleteBounds.width <= 375, true);
    await phoneActions.getByRole('button', { name: strings.Delete, exact: true }).focus();
    assert.equal(await phoneActions.getByRole('button', { name: strings.Delete, exact: true }).evaluate((button) => document.activeElement === button), true);
    receipts.push({ scenario: 'phone existing table horizontal scroll reaches action controls', deleteBounds });
    await capture(mobile.page, 'authors-phone-actions-dark');
    mobile.failPages(true);
    await mobile.page.getByRole('button', { name: strings.Reload, exact: true }).click();
    await mobile.page.getByText(strings.ErrorFetchingPendingAuthorImports, { exact: true }).waitFor();
    await noPrivateReason(mobile.page);
    await capture(mobile.page, 'authors-load-error-phone-dark');
    mobile.failPages(false);
    await mobile.page.getByRole('button', { name: strings.Reload, exact: true }).focus();
    await mobile.page.keyboard.press('Enter');
    await populated(mobile.page);
    mobile.setEmpty(true);
    await mobile.page.getByRole('button', { name: strings.Reload, exact: true }).click();
    await mobile.page.getByText(strings.NoPendingAuthorImports, { exact: true }).waitFor();
    await capture(mobile.page, 'authors-empty-phone-dark');
    assert.deepEqual(mobile.errors, []);
    receipts.push({ scenario: 'phone', requests: mobile.requests, mutations: mobile.mutations });
    await mobile.page.close();
    checks.push('375px dark theme,contained table;load failure keyboard Reload and empty state');

    for (const width of [1280, 375]) {
      const reference = await open(browser, width, 'missing');
      await reference.page.locator('tbody tr').first().waitFor();
      await visibleRows(reference.page);
      await capture(reference.page, `missing-reference-${width}`);
      if (width === 375) {
        await reference.page.locator('#sidebar-toggle-button').click();
      }
      const wantedLink = reference.page.getByRole('link', { name: strings.Wanted, exact: true });
      const parentHref = await wantedLink.getAttribute('href');
      const childRoutes = await wantedLink.locator('..').locator('a[class*="PageSidebarItem-childLink"]').evaluateAll((links) => links.map((link) => new URL(link.href).pathname));
      receipts.push({ scenario: 'Wanted child order and incumbent parent target', width, parentHref, childRoutes });
      assert.equal(parentHref, '/wanted/missing');
      assert.deepEqual(childRoutes, ['/wanted/missing', '/wanted/cutoffunmet', '/wanted/authors']);
      await reference.page.locator('a[href="/wanted/authors"]').click();
      await populated(reference.page);
      assert.equal(await reference.page.evaluate(() => window.getFixturePath()), '/wanted/authors');
      if (width === 375) {
        await reference.page.locator('#sidebar-toggle-button').click();
      }
      await reference.page.getByRole('link', { name: strings.Wanted, exact: true }).click();
      await reference.page.waitForFunction(() => window.getFixturePath() === '/wanted/missing');
      await reference.page.locator('tbody tr').first().waitFor();
      await visibleRows(reference.page);
      assert.equal(reference.mutations.length, 0);
      receipts.push({ scenario: 'Wanted parent click from Authors opens Missing', width, path: await reference.page.evaluate(() => window.getFixturePath()) });
      await capture(reference.page, `wanted-navigation-${width}`);
      assert.deepEqual(reference.errors, []);
      await reference.page.close();
    }
    checks.push('Wanted order Missing/CutoffUnmet/Authors; actual parent click from Authors opens Missing at desktop/phone');
    const lastPage = await open(browser, 1280, 'authors', { records: records().slice(0, 21) });
    await populated(lastPage.page);
    await lastPage.page.getByRole('button', { name: '1 / 2', exact: true }).click();
    await lastPage.page.locator('select[name="pageSelect"]').selectOption('2');
    await populated(lastPage.page);
    assert.equal(await lastPage.page.locator('tbody tr').count(), 1);
    await lastPage.page.locator('tbody tr').getByRole('button', { name: strings.Delete, exact: true }).click();
    const deleteModal = lastPage.page.locator('[class*="ModalContent-modalContent"]');
    await deleteModal.getByRole('button', { name: strings.Delete, exact: true }).focus();
    await lastPage.page.keyboard.press('Enter');
    await populated(lastPage.page);
    assert.equal(lastPage.mutations[0].method, 'DELETE');
    assert.equal(new URL(lastPage.mutations[0].url).searchParams.get('delete'), 'true');
    assert.equal(lastPage.mutations[0].body, null);
    assert.equal(await lastPage.page.evaluate(() => window.getWantedAuthorsState().page), 1);
    assert.equal(await lastPage.page.locator('tbody tr').count(), 20);
    assert.equal(await lastPage.page.getByText(strings.NoPendingAuthorImports, { exact: true }).count(), 0);
    receipts.push({ scenario: '21 rows,last-page manual delete', requests: lastPage.requests, mutations: lastPage.mutations });
    assert.deepEqual(lastPage.errors, []);
    await lastPage.page.close();
    checks.push('manual Delete uses explicit flag and actual row removal;single last-page row deletion returns to remaining20');

    const inactive = { ...records()[5], authorName: null, overallStatus: 'Failed' };
    const deleteFailure = await open(browser, 375, 'authors', { records: [inactive] });
    await populated(deleteFailure.page);
    const inactiveRow = deleteFailure.page.locator('tbody tr');
    assert.equal(await inactiveRow.locator('td').first().innerText(), '', 'An unavailable author name stays blank; the provider ID remains in its own column');
    assert.equal(await inactiveRow.getByText(inactive.providerId, { exact: true }).count(), 1);
    assert.equal(await inactiveRow.getByRole('button', { name: strings.CancelImport, exact: true }).isDisabled(), true);
    assert.equal(await inactiveRow.getByRole('button', { name: strings.Delete, exact: true }).isDisabled(), false);
    await inactiveRow.getByRole('button', { name: strings.Delete, exact: true }).click();
    const inactiveModal = deleteFailure.page.locator('[class*="ModalContent-modalContent"]');
    await inactiveModal.getByText(strings.DeletePendingAuthorImportMessage.replace('{authorName}', inactive.providerId), { exact: true }).waitFor();
    await capture(deleteFailure.page, 'authors-delete-confirm-phone-dark');
    await deleteFailure.page.keyboard.press('Escape');
    await inactiveModal.waitFor({ state: 'hidden' });
    assert.equal(deleteFailure.mutations.length, 0);
    await inactiveRow.getByRole('button', { name: strings.Delete, exact: true }).click();
    await inactiveModal.getByRole('button', { name: strings.Cancel, exact: true }).focus();
    await deleteFailure.page.keyboard.press('Enter');
    await inactiveModal.waitFor({ state: 'hidden' });
    assert.equal(deleteFailure.mutations.length, 0);
    deleteFailure.failActions(true);
    await inactiveRow.getByRole('button', { name: strings.Delete, exact: true }).click();
    await inactiveModal.getByRole('button', { name: strings.Delete, exact: true }).click();
    await deleteFailure.page.getByText(strings.ErrorUpdatingPendingAuthorImport, { exact: true }).waitFor();
    assert.equal(await inactiveRow.getByRole('button', { name: strings.Delete, exact: true }).isDisabled(), false);
    assert.equal(await deleteFailure.page.locator('tbody tr').count(), 1);
    await noPrivateReason(deleteFailure.page);
    await capture(deleteFailure.page, 'authors-delete-error-phone-dark');
    deleteFailure.failActions(false);
    await inactiveRow.getByRole('button', { name: strings.Delete, exact: true }).click();
    await inactiveModal.getByRole('button', { name: strings.Delete, exact: true }).focus();
    await deleteFailure.page.keyboard.press('Enter');
    await populated(deleteFailure.page);
    await deleteFailure.page.getByText(strings.NoPendingAuthorImports, { exact: true }).waitFor();
    assert.equal(await deleteFailure.page.evaluate(() => window.getWantedAuthorsState().totalRecords), 0);
    assert.equal(deleteFailure.requests.every((request) => new URL(request.url).searchParams.get('page') === '1'), true);
    assert.equal(deleteFailure.mutations.every((request) => request.method === 'DELETE' && new URL(request.url).searchParams.get('delete') === 'true' && request.body === null), true);
    receipts.push({ scenario: 'inactive unnamed request Delete confirms provider ID,fails visibly without removal,recovers and empties only actual zero records', requests: deleteFailure.requests, mutations: deleteFailure.mutations });
    assert.deepEqual(deleteFailure.errors, []);
    await deleteFailure.page.close();
    checks.push('phone Delete works inactive rows,uses provider fallback;Cancel/Escape are inert;500 retains row+visible error+recovered button;last request deletion is truly empty without page0');

    const french = JSON.parse(fs.readFileSync(path.join(root, 'src/NzbDrone.Core/Localization/Core/fr.json')));
    const translated = await open(browser, 375, 'authors', { strings: { ...strings, ...french }, records: [{ ...records()[0], authorName: 'Ωμέγα 李', overallStatus: 'Failed' }] });
    await populated(translated.page);
    assert.equal(await translated.page.getByText(french.NotAvailable, { exact: true }).count() > 0, true);
    assert.equal(await translated.page.getByText('Ωμέγα 李', { exact: true }).count(), 1);
    await noPrivateReason(translated.page);
    assert.deepEqual(translated.errors, []);
    await translated.page.close();
    checks.push('unavailable status uses the existing French translation');

    const nativeDir = process.env.CHAPTARR_WANTED_NATIVE_DIR;
    if (nativeDir) {
      for (const provider of ['sqlite', 'postgres']) {
        const envelope = JSON.parse(fs.readFileSync(path.join(nativeDir, `${provider}-page20.json`)));
        const nativePage = await open(browser, 1280, 'authors', { envelope });
        await populated(nativePage.page);
        assert.equal(await nativePage.page.locator('tbody tr').count(), 20);
        assert.equal(await nativePage.page.evaluate(() => window.getWantedAuthorsState().totalRecords), 1000);
        assert.deepEqual(await nativePage.page.evaluate(() => window.getWantedAuthorsState().items.map((item) => item.id)), envelope.records.map((item) => item.id));
        receipts.push({ scenario: `${provider} exact native page20 envelope`, requests: nativePage.requests, ids: envelope.records.map((item) => item.id) });
        assert.deepEqual(nativePage.errors, []);
        await nativePage.page.close();

        const states = JSON.parse(fs.readFileSync(path.join(nativeDir, `${provider}-states.json`)));
        const nativeStates = await open(browser, 1280, 'authors', { records: [states.cancelled, states.failed, states.succeeded] });
        await populated(nativeStates.page);
        for (const name of ['cancelled', 'failed', 'succeeded']) {
          const record = states[name];
          const row = nativeStates.page.locator('tbody tr').filter({ hasText: record.providerId });
          assert.equal(await row.getByText(name === 'succeeded' ? strings.Succeeded : strings.NotAvailable, { exact: true }).count(), 1);
          assert.equal(await row.getByRole('button', { name: strings.CancelImport, exact: true }).isDisabled(), true);
          assert.equal(await row.getByRole('button', { name: strings.Delete, exact: true }).isDisabled(), false);
          assert.equal(await row.getByRole('button', { name: strings.Retry, exact: true }).count(), name === 'failed' ? 1 : 0);
          if (record.lastError) {
            assert.equal(await row.getByText(record.lastError, { exact: true }).count(), 0);
            assert.equal(await row.locator('[title]').evaluateAll((nodes, reason) => nodes.some((node) => node.getAttribute('title').includes(reason)), record.lastError), false);
          }
        }
        await capture(nativeStates.page, `authors-native-${provider}-states`);
        receipts.push({ scenario: `${provider} three actual resource snapshots in a test-only three-row envelope; NOT the default native page20/page50`, records: [states.cancelled, states.failed, states.succeeded] });
        assert.deepEqual(nativeStates.errors, []);
        await nativeStates.page.close();
      }
      checks.push('both native persisted controller page20 envelopes replay exactly; actual cancelled/failed/succeeded snapshots remain truthful and hide raw failure reasons');
    }
    fs.writeFileSync(path.join(artifacts, 'results.json'), JSON.stringify({ checks, receipts, traces, status: 'passed', fixtures: 'Synthetic stored API-shape rows; native persisted controller exports are separate integration evidence' }, null, 2));
    console.log(JSON.stringify({ suite: 'Wanted Authors existing-pattern browser controls', checks, status: 'passed', artifacts }, null, 2));
  } catch (error) {
    const pages = browser ? browser.contexts().flatMap((context) => context.pages()) : [];
    const visible = await Promise.all(pages.map(async(page) => ({
      url: page.url(),
      body: await page.locator('body').innerText().catch(() => 'unavailable'),
      state: await page.evaluate(() => window.getWantedAuthorsState?.()).catch(() => null)
    })));
    fs.writeFileSync(path.join(artifacts, 'failure.json'), JSON.stringify({ error: String(error), checks, receipts, traces, visible }, null, 2));
    throw error;
  } finally {
    if (browser) {
      await browser.close();
    }
    fs.rmSync(output, { recursive: true, force: true });
  }
})().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
