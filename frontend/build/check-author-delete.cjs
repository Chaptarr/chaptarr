/* Uses the repo-pinned browser runner; requests never reach a real library. */
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const webpack = require('webpack');
const createRouteCapture = require('./capture-browser-route.cjs');

const { chromium } = require('playwright');

const root = path.resolve(__dirname, '../..');
const output = fs.mkdtempSync(path.join(os.tmpdir(), 'chaptarr-delete-ui-'));
console.log(`Temporary browser fixture: ${output}`);
const darkTheme = require('../src/Styles/Themes/dark');
const lightTheme = require('../src/Styles/Themes/light');

const themeVariables = { dark: darkTheme, light: lightTheme };
const config = require('./webpack.config')({ production: true });

config.entry = { fixture: path.join(__dirname, 'author-delete-fixture.js') };
config.output = { ...config.output, path: output, filename: 'fixture.js' };
config.optimization = { minimize: false };
config.plugins = config.plugins.filter((p) => ['DefinePlugin', 'MiniCssExtractPlugin'].includes(p.constructor.name));
for (const rule of config.module.rules) {
  if (Array.isArray(rule.use)) {
    rule.use = rule.use.filter((loader) => !String(typeof loader === 'string' ? loader : loader.loader).includes('css-modules-typescript-loader'));
  }
}
const baseline = process.argv.includes('--baseline');
const booksOnly = process.argv.includes('--books-only');
const disposalOnly = process.argv.includes('--disposal-only');
const evidence = process.env.CHAPTARR_UI_EVIDENCE;

async function checkBookDelete(page, single, strings, theme, width) {
  const pending = createRouteCapture('author deletion request');
  const requests = [];
  await page.route('**/book/**', async(route) => {
    const request = route.request();
    if (request.method() !== 'DELETE') {
      return route.fallback();
    }
    requests.push(single ? Object.fromEntries(new URL(request.url()).searchParams) : request.postDataJSON());
    pending.capture(route);
  });
  const render = () => page.evaluate((value) => window.renderDeleteFixture(value, 'audiobook', 'book', true), single);
  await render();
  assert.equal(await page.getByText('Previous deletion failed', { exact: true }).count(), 0);
  const deleteButton = page.getByRole('button', { name: strings.Delete, exact: true });
  const files = page.locator('input[name="deleteFiles"]');
  const exclusion = page.locator('input[name="addImportListExclusion"]');
  const closeButton = page.getByRole('button', { name: single ? strings.Close : strings.Cancel, exact: true });
  await files.focus();
  await page.keyboard.press('Space');
  if (single) {
    await page.locator('input[name="applyToBothFormats"]').focus();
    await page.keyboard.press('Space');
  }
  const expected = single ? { deleteFiles: 'true', addImportListExclusion: 'true', applyToBothFormats: 'true' } :
    { bookIds: [10, 11], deleteFiles: true, addImportListExclusion: true };
  const first = pending.next();
  await deleteButton.click();
  const firstRoute = await first;
  await page.waitForFunction((section) => window.getDeleteFixtureState()[section].isDeleting, single ? 'books' : 'bookIndex');
  assert.equal(await deleteButton.isDisabled(), true);
  assert.equal(await files.isDisabled(), true);
  assert.equal(await exclusion.isDisabled(), true);
  assert.equal(await closeButton.isDisabled(), true);
  assert.equal(await page.evaluate(() => window.deleteFixtureCloses), 0, 'Pending delete must keep the dialog open');
  assert.deepEqual(requests, [expected]);
  await files.evaluate((element) => {
    const scroller = element.closest('[tabindex="-1"]');
    scroller.scrollTop = scroller.scrollHeight;
  });
  await firstRoute.fulfill({ status: 500, json: { message: 'Unable to delete book file: /audiobooks/Frank Herbert/Dune/Part 02.mp3' } });
  await page.getByText('Unable to delete book file: /audiobooks/Frank Herbert/Dune/Part 02.mp3', { exact: true }).waitFor();
  assert.equal(await page.getByRole('alert').filter({ hasText: 'Unable to delete book file:' }).evaluate((element) => {
    const scroller = element.closest('[tabindex="-1"]');
    const alertBounds = element.getBoundingClientRect();
    const scrollBounds = scroller.getBoundingClientRect();
    return alertBounds.top >= scrollBounds.top && alertBounds.bottom <= scrollBounds.bottom;
  }), true, 'A deletion error must be visible even when the dialog body was scrolled down');
  assert.equal(await files.isChecked(), true);
  assert.equal(await exclusion.isChecked(), true);
  assert.equal(await deleteButton.isEnabled(), true);
  if (single) {
    assert.equal(await page.locator('input[name="applyToBothFormats"]').isChecked(), true);
  }
  assert.equal(await page.evaluate(() => window.getDeleteFixtureState().books.items.length), 2);
  assert.deepEqual(await page.evaluate(() => window.deleteFixtureNavigation), []);
  assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `${theme} ${width}: book deletion error overflow`);
  await page.screenshot({ path: path.join(output, `${theme}-${width}-book-${single ? 'single' : 'bulk'}-failure.png`) });
  const retry = pending.next();
  await deleteButton.click();
  const retryRoute = await retry;
  await page.waitForFunction((section) => window.getDeleteFixtureState()[section].isDeleting, single ? 'books' : 'bookIndex');
  assert.equal(await page.getByText('Unable to delete book file: /audiobooks/Frank Herbert/Dune/Part 02.mp3', { exact: true }).count(), 0, 'Retry clears the previous error');
  assert.deepEqual(requests, [expected, expected], 'Retry retains the exact deletion choices');
  await retryRoute.fulfill({ json: {} });
  await deleteButton.waitFor({ state: 'hidden' });
  assert.equal(await page.evaluate(() => window.deleteFixtureCloses), single ? 0 : 1, 'Removing a single book unmounts its details page before the success callback');
  assert.deepEqual(await page.evaluate(() => window.deleteFixtureNavigation), single ? ['/author/1'] : []);

  await render();
  await closeButton.click();
  assert.equal(requests.length, 2, 'Opening and cancelling must not delete anything');
  await render();
  const fallback = pending.next();
  await deleteButton.click();
  const fallbackRoute = await fallback;
  await fallbackRoute.fulfill({ status: 503, contentType: 'text/html', body: 'Gateway unavailable' });
  await page.getByText(strings.BookDeleteFailed, { exact: true }).waitFor();
  assert.equal(await deleteButton.isEnabled(), true);
  await closeButton.click();

  if (single) {
    await render();
    const leaving = pending.next();
    await deleteButton.click();
    const leavingRoute = await leaving;
    await page.keyboard.press('Escape');
    await deleteButton.waitFor({ state: 'hidden' });
    await page.evaluate(() => window.history.pushState({}, '', '/wanted/missing'));
    await leavingRoute.fulfill({ json: {} });
    await page.waitForFunction(() => !window.getDeleteFixtureState().books.isDeleting);
    assert.deepEqual(await page.evaluate(() => window.deleteFixtureNavigation), [], 'A late response must not navigate after leaving the dialog');
    assert.equal(await page.evaluate(() => window.deleteFixtureCloses), 1);
    await page.evaluate(() => window.history.replaceState({}, '', '/'));
  }
  await page.unroute('**/book/**');
}

function authorPreview(request, recycleBin = '') {
  const { authorIds, applyToBothFormats } = request.postDataJSON();
  const paths = applyToBothFormats ? ['/library/Dune/Frank Herbert', '/audiobooks/Dune/Frank Herbert', '/ebooks/Dune/Frank Herbert', '/library/Dune/Brian Herbert'] : ['/ebooks/Dune/Frank Herbert'];
  return { recycleBin, warnings: [], otherAuthors: [], files: [], folders: paths.filter((folder) => authorIds.includes(2) || !folder.includes('Brian')).map((folder) => ({
    path: folder, deleteContents: true, reason: null, bookFileCount: 2, size: 4096, otherFiles: [`${folder}/cover.jpg`]
  })) };
}

async function checkAuthorPreview(page, strings) {
  const pending = createRouteCapture('author delete preview');
  await page.route('**/author/deletepreview', (route) => pending.capture(route));
  await page.evaluate(() => window.renderDeleteFixture(true, 'audiobook'));
  const button = page.getByRole('button', { name: strings.Delete, exact: true });
  const first = pending.next();
  await page.locator('input[name="deleteFiles"]').focus();
  await page.keyboard.press('Space');
  const held = await first;
  assert.equal(await button.isDisabled(), true, 'Deletion must wait for the actual inventory');
  await held.fulfill({ status: 409, json: { message: 'The selected folder is a configured root.' } });
  await page.getByText('The selected folder is a configured root.', { exact: true }).waitFor();
  assert.equal(await button.isDisabled(), true, 'A refused preview must never authorize deletion');
  await page.keyboard.press('Escape');
  await page.evaluate(() => window.renderDeleteFixture(true, 'audiobook'));
  const second = pending.next();
  await page.locator('input[name="deleteFiles"]').focus();
  await page.keyboard.press('Space');
  const request = await second;
  const preview = authorPreview(request.request());
  preview.otherAuthors = [{ authorId: 2, name: 'Brian Herbert', fileCount: 1 }];
  await request.fulfill({ json: preview });
  const consent = page.locator('input[name="deleteOtherAuthorsFiles"]');
  await consent.waitFor();
  assert.equal(await button.isDisabled(), true, 'The other author warning needs explicit consent');
  await consent.focus();
  await page.keyboard.press('Space');
  const deleting = page.waitForRequest((r) => r.method() === 'DELETE' && new URL(r.url()).pathname.endsWith('/author/1'));
  await button.click();
  assert.equal(new URL((await deleting).url()).searchParams.get('deleteOtherAuthorsFiles'), 'true');
  await button.waitFor({ state: 'hidden' });
  await page.unroute('**/author/deletepreview');
}

async function checkAuthorDisposal(page, strings, theme, width) {
  let cases = 0;
  for (const single of [false, true]) {
    for (const recycleBin of ['', '/recycle', null]) {
      await page.route('**/author/deletepreview', (route) => (recycleBin === null ?
        route.fulfill({ status: 503, json: { message: 'Unavailable' } }) : route.fulfill({ json: authorPreview(route.request(), recycleBin) })));
      await page.evaluate((value) => window.renderDeleteFixture(value, 'ebook'), single);
      const files = page.locator('input[name="deleteFiles"]');
      await files.focus();
      await page.keyboard.press('Space');
      let key = recycleBin ? 'DeleteBookFilesCalibreRecyclingWarning' : 'DeleteBookFilesCalibrePermanentWarning';
      let state = recycleBin ? 'bin' : 'permanent';
      if (recycleBin === null) {
        key = 'AuthorDeleteRecyclingBinUnknown';
        state = 'unknown';
      }
      await page.getByText(strings[key], { exact: true }).waitFor();
      assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
      await page.screenshot({ path: path.join(output, `${theme}-${width}-${single ? 'single' : 'bulk'}-${state}.png`) });
      await page.keyboard.press('Escape');
      await page.unroute('**/author/deletepreview');
      cases++;
    }
  }
  return cases;
}

(async() => {
  const compiler = webpack(config);
  await new Promise((resolve, reject) => compiler.run((error, stats) => {
    compiler.close(() => {});
    if (error || stats.hasErrors()) {
      reject(error || new Error(stats.toString({ all: false, errors: true })));
    } else {
      resolve();
    }
  }));
  const browser = await chromium.launch();
  try {
    const css = fs.readdirSync(path.join(output, 'Content')).find((f) => f.endsWith('.css'));
    const strings = JSON.parse(fs.readFileSync(path.join(root, 'src/NzbDrone.Core/Localization/Core/en.json')));
    let cases = 0;
    for (const theme of ['light', 'dark']) {
      for (const width of baseline ? [390, 1440] : [320, 412, 1440]) {
        const page = await browser.newPage({ viewport: { width, height: 850 } });
        const errors = [];
        let refuseDelete = false;
        page.on('pageerror', (e) => errors.push(e.message));
        await page.route('**/*', (route) => {
          const request = route.request();
          const pathname = new URL(request.url()).pathname;
          if (pathname.endsWith('/localization')) {
            return route.fulfill({ json: { Strings: strings } });
          }
          if (pathname.endsWith('/config/mediamanagement')) {
            return route.fulfill({ json: { recycleBin: '' } });
          }
          if (pathname.endsWith('/author/deletepreview')) {
            return route.fulfill({ json: authorPreview(request) });
          }
          if (pathname.endsWith('/bookfile/deletepreview')) {
            const { bookIds } = request.postDataJSON();
            return route.fulfill({ json: {
              files: bookIds.map((bookId) => ({
                id: bookId, bookId, path: `/audiobooks/Frank Herbert/Book ${bookId}/Part 01.mp3`,
                associatedPaths: [], isPermanentDelete: true
              })), hasCalibreFiles: false
            } });
          }
          if (request.method() === 'DELETE' && (/\/author\/(editor|1)$/).test(pathname)) {
            return refuseDelete ?
              route.fulfill({ status: 409, json: { message: 'Frank Herbert: choose a format or explicitly select both. Nothing was changed.' } }) :
              route.fulfill({ json: {} });
          }
          if (pathname.endsWith('/author') && request.method() === 'GET') {
            return route.fulfill({ json: [{ id: 1, authorName: 'Frank Herbert', sortName: 'herbert frank', path: '/library/Dune/Frank Herbert' }] });
          }
          if (pathname.endsWith('/book') && request.method() === 'GET') {
            return route.fulfill({ json: [] });
          }
          if (pathname.endsWith('/book/10/siblings')) {
            return route.fulfill({ json: {
              currentBook: { bookId: 10, title: 'Dune', mediaType: 'audiobook', files: [{ path: '/audiobooks/Frank Herbert/Dune/Part 01.mp3', size: 2048 }] },
              siblings: [{ bookId: 20, title: 'Dune', mediaType: 'ebook', files: [] }], siblingMediaType: 'ebook', ebookCount: 1
            } });
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
          const variables = Object.entries(themeVariables[theme]).map(([k, v]) => `--${k}:${v}`).join(';');
          return route.fulfill({ contentType: 'text/html', body: `<!doctype html><meta name="viewport" content="width=device-width,initial-scale=1"><link rel="stylesheet" href="/style.css"><style>:root{${variables}}body{margin:0}</style><div id="root"></div><div id="portal-root"></div><script>window.Chaptarr={isProduction:true,apiRoot:'/api/v1',urlBase:''};</script><script src="/fixture.js"></script>` });
        });
        await page.goto('http://delete-fixture.test/');
        if (!booksOnly) {
          cases += await checkAuthorDisposal(page, strings, theme, width);
        }
        for (const single of disposalOnly ? [] : [false, true]) {
          await checkBookDelete(page, single, strings, theme, width);
          cases++;
        }
        if (!booksOnly && !disposalOnly) {
          await page.evaluate(() => window.renderDeleteFixture());
          await page.getByText('Delete Selected Author', { exact: true }).waitFor();
          const deleteButton = page.getByRole('button', { name: 'Delete', exact: true });
          const both = page.getByRole('button', { name: strings.AuthorDeleteBothFormats, exact: true });
          const files = page.locator('input[name="deleteFiles"]');
          const exclusion = page.locator('input[name="addImportListExclusion"]');
          const media = page.getByRole('button', { name: strings.Ebooks, exact: true });
          assert.equal(await deleteButton.isDisabled(), true, 'All-format view must require an explicit choice');
          assert.equal(await both.count(), 0);
          await page.getByRole('button', { name: strings.AuthorDeleteSelectFormat, exact: true }).click();
          await page.getByText(strings.Ebooks, { exact: true }).click();
          await exclusion.focus();
          await page.keyboard.press('Space');
          assert.equal(await exclusion.isChecked(), true);
          const scopedRequest = page.waitForRequest((request) => request.url().endsWith('/author/editor') && request.method() === 'DELETE');
          await deleteButton.click();
          assert.deepEqual((await scopedRequest).postDataJSON(), { authorIds: [1, 2], mediaType: 'ebook', applyToBothFormats: false, deleteFiles: false, addImportListExclusion: true });

          await deleteButton.waitFor({ state: 'hidden' });
          await page.evaluate(() => window.renderDeleteFixture(false, 'ebook'));
          assert.equal(await exclusion.isChecked(), false);
          await files.focus();
          await page.keyboard.press('Space');
          assert.equal(await files.isChecked(), true);
          assert.equal(await page.getByText(strings.AuthorDeleteFormatFilesHelpText, { exact: true }).isVisible(), true);
          await page.getByText('/ebooks/Dune/Frank Herbert', { exact: true }).waitFor();
          for (const configuredPath of ['/library/Dune/Frank Herbert', '/audiobooks/Dune/Frank Herbert', '/library/Dune/Brian Herbert']) {
            assert.equal(await page.getByText(configuredPath, { exact: true }).count(), 0, 'A scoped deletion must not present configured folders as folders to delete');
          }
          assert.equal(await page.getByText(strings.AuthorDeleteScopedFilesSummary.replace('{mediaType}', strings.Ebooks), { exact: true }).count(), 2);
          await page.screenshot({ path: path.join(output, `${theme}-${width}-scoped.png`) });
          refuseDelete = true;
          await deleteButton.click();
          await page.getByText('Frank Herbert: choose a format or explicitly select both. Nothing was changed.').waitFor();
          assert.equal(await media.isVisible(), true, 'Refusal must preserve the requested scope');
          assert.equal(await files.isChecked(), true, 'Refusal must preserve the checkbox');
          assert.equal(await deleteButton.isEnabled(), true, 'Refusal must leave a working retry');
          await media.click();
          await page.getByText(strings.AuthorDeleteBothFormats, { exact: true }).click();
          await page.getByText('/audiobooks/Dune/Frank Herbert', { exact: true }).first().waitFor();
          const selectedPaths = ['/library/Dune/Frank Herbert', '/audiobooks/Dune/Frank Herbert', '/ebooks/Dune/Frank Herbert', '/library/Dune/Brian Herbert'];
          for (const selectedPath of selectedPaths) {
            assert.equal(await page.getByText(selectedPath, { exact: true }).count(), 1, `${selectedPath} must be disclosed once`);
          }
          assert.equal(await page.getByText(strings.AuthorDeleteFoldersHelpText, { exact: true }).isVisible(), true);
          assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `${theme} ${width}: bulk overflow`);
          await page.screenshot({ path: path.join(output, `${theme}-${width}-paths.png`) });
          refuseDelete = false;
          const wholeRequest = page.waitForRequest((request) => request.url().endsWith('/author/editor') && request.method() === 'DELETE');
          await deleteButton.click();
          assert.deepEqual((await wholeRequest).postDataJSON(), { authorIds: [1, 2], applyToBothFormats: true, deleteFiles: true, addImportListExclusion: false });

          await deleteButton.waitFor({ state: 'hidden' });
          await page.evaluate(() => window.renderDeleteFixture(true, 'ebook'));
          assert.equal(await media.isVisible(), true);
          assert.equal(await both.count(), 0);
          await files.focus();
          await page.keyboard.press('Space');
          assert.equal(await page.getByText(strings.AuthorDeleteFormatFilesHelpText, { exact: true }).isVisible(), true);
          assert.equal(await page.getByText('/audiobooks/Dune/Frank Herbert', { exact: true }).count(), 0);
          assert.equal(await page.getByText(strings.AuthorDeleteScopedFilesSummary.replace('{mediaType}', strings.Ebooks), { exact: true }).count(), 1);
          assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `${theme} ${width}: single overflow`);
          await page.screenshot({ path: path.join(output, `${theme}-${width}-single.png`) });
          const singleRequest = page.waitForRequest((request) => new URL(request.url()).pathname.endsWith('/author/1') && request.method() === 'DELETE');
          await deleteButton.click();
          const singleQuery = new URL((await singleRequest).url()).searchParams;
          assert.equal(singleQuery.get('mediaType'), 'ebook');
          assert.equal(singleQuery.get('applyToBothFormats'), 'false');
          assert.equal(singleQuery.get('deleteFiles'), 'true');
          await page.waitForFunction(() => !window.getDeleteFixtureState().authors.isDeleting);
          assert.equal(await page.evaluate(() => window.getDeleteFixtureState().authors.items.some((author) => author.id === 1)), true, 'Scoped success must not remove the surviving author from state');

          await deleteButton.waitFor({ state: 'hidden' });
          await page.evaluate(() => window.renderDeleteFixture(true, 'audiobook'));
          await page.locator('input[name="readdAuthor"]').focus();
          await page.keyboard.press('Space');
          assert.equal(await both.isVisible(), true, 'Purge and re-add must visibly apply to the whole author');
          assert.equal(await files.isDisabled(), true);
          assert.equal(await exclusion.isDisabled(), true);
          const readdRequest = page.waitForRequest((request) => new URL(request.url()).pathname.endsWith('/author/1') && request.method() === 'DELETE');
          await page.getByRole('button', { name: strings.PurgeAndReaddAuthor, exact: true }).click();
          const readdQuery = new URL((await readdRequest).url()).searchParams;
          assert.equal(readdQuery.get('readdAuthor'), 'true');
          assert.equal(readdQuery.get('applyToBothFormats'), 'true');
          cases++;
        }
        if (!baseline && !booksOnly && !disposalOnly && theme === 'light' && width === 320) {
          await checkAuthorPreview(page, strings);
          cases += 2;
        }
        assert.deepEqual(errors, []);
        await page.close();
      }
    }
    console.log(JSON.stringify({ cases, baseline, output }));
  } finally {
    await browser.close();
  }
})().catch((e) => {
  console.error(e);
  process.exitCode = 1;
}).finally(() => {
  try {
    if (evidence) {
      fs.mkdirSync(evidence, { recursive: true });
      for (const file of fs.readdirSync(output).filter((name) => name.endsWith('.png'))) {
        fs.copyFileSync(path.join(output, file), path.join(evidence, file));
      }
    }
  } finally {
    fs.rmSync(output, { recursive: true, force: true });
  }
});
