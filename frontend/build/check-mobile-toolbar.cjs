/* Uses the repo-pinned Playwright; no backend or library data is used. */
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const webpack = require('webpack');
const darkTheme = require('../src/Styles/Themes/dark');
const lightTheme = require('../src/Styles/Themes/light');

const { chromium } = require('playwright');

const root = path.resolve(__dirname, '../..');
const strings = JSON.parse(fs.readFileSync(path.join(root, 'src/NzbDrone.Core/Localization/Core/en.json')));
const output = fs.mkdtempSync(path.join(os.tmpdir(), 'chaptarr-toolbar-'));
const config = require('./webpack.config')({ production: true });

config.entry = { fixture: path.join(__dirname, 'mobile-toolbar-fixture.js') };
config.output = { ...config.output, path: output, filename: 'fixture.js' };
config.optimization = { minimize: false };
config.plugins = config.plugins.filter((p) => ['DefinePlugin', 'MiniCssExtractPlugin'].includes(p.constructor.name));
// Fixture builds must not generate source declarations or overwrite application output.
for (const rule of config.module.rules) {
  if (Array.isArray(rule.use)) {
    rule.use = rule.use.filter((loader) => !String(typeof loader === 'string' ? loader : loader.loader).includes('css-modules-typescript-loader'));
  }
}

async function assertFooterPopulation(page, theme, width, mode) {
  await page.evaluate((footerMode) => window.renderLibraryFooterFixture(footerMode), mode);
  const booksData = page.getByText('Books', { exact: true }).locator('xpath=..');
  const filesData = page.getByText('Files', { exact: true }).locator('xpath=..');
  await booksData.waitFor();
  const expectedBookCount = mode === 'client' ? '3' : '2';
  const renderedBookCount = (await booksData.innerText()).trim().split(/\s+/).pop();
  const renderedFileCount = (await filesData.innerText()).trim().split(/\s+/).pop();
  assert.equal(renderedBookCount, expectedBookCount, `${theme} ${width}px: ${mode} footer uses Library book population`);
  assert.equal(renderedFileCount, '1', `${theme} ${width}px: ${mode} footer uses Library file population`);

  const footerLayout = await page.evaluate(() => window.getBookFooterLayout());
  const expectedLegend = [
    strings.BookFilesAvailable,
    strings.MissingBookMonitored,
    strings.UnreleasedOrUnknownBookMonitored,
    strings.BookNoFilesUnmonitored
  ];
  assert.equal(footerLayout.position, 'fixed', `${theme} ${width}px: browse footer remains pinned`);
  assert(footerLayout.isSticky, `${theme} ${width}px: Books footer uses the existing sticky mode`);
  assert.deepEqual(footerLayout.legendItems.map((item) => item.text), expectedLegend, `${theme} ${width}px: sticky Books legend uses localized book states`);
  assert.deepEqual(footerLayout.legendItems.map((item) => item.color), [
    footerLayout.colors.available,
    footerLayout.colors.missing,
    footerLayout.colors.future,
    footerLayout.colors.unmonitored
  ], `${theme} ${width}px: legend swatches match progress bar theme colors`);
  assert(footerLayout.legendItems.every((item) => item.withinLegend), `${theme} ${width}px: legend labels fit their grid cells`);
  assert(footerLayout.legendWithinFooter, `${theme} ${width}px: legend remains inside the pinned footer`);
  assert(footerLayout.statisticsWithinFooter, `${theme} ${width}px: statistics remain inside the pinned footer`);
  assert(footerLayout.legendDoesNotOverlapStatistics, `${theme} ${width}px: legend does not overlap footer statistics`);
  assert(footerLayout.footer.left >= 0 && footerLayout.footer.right <= width, `${theme} ${width}px: footer is not clipped horizontally`);
  assert(footerLayout.footer.top >= 0 && footerLayout.footer.bottom <= footerLayout.viewportHeight, `${theme} ${width}px: footer is not clipped vertically`);
  assert(footerLayout.documentWidth <= footerLayout.viewportWidth, `${theme} ${width}px: footer does not create horizontal page overflow`);
  assert.equal(footerLayout.pinnedHeight, Math.ceil(footerLayout.footer.height), `${theme} ${width}px: pinned spacing tracks footer height`);
  assert(footerLayout.contentPaddingBottom >= footerLayout.pinnedHeight, `${theme} ${width}px: list bottom padding reserves pinned footer space`);

  if (theme === 'dark' && width === 1440 && mode === 'client') {
    const desktopPinnedHeight = footerLayout.pinnedHeight;
    await page.setViewportSize({ width: 390, height: 800 });
    await page.waitForFunction(() => {
      return window.isBookFooterPinnedHeightCurrent();
    });
    const resizedLayout = await page.evaluate(() => window.getBookFooterLayout());
    assert.equal(resizedLayout.position, 'fixed', 'dark 390px resize: footer remains pinned after viewport resize');
    assert(resizedLayout.legendWithinFooter && resizedLayout.statisticsWithinFooter, 'dark 390px resize: legend and statistics remain within the footer');
    assert(resizedLayout.legendDoesNotOverlapStatistics, 'dark 390px resize: legend does not overlap statistics');
    assert(resizedLayout.footer.left >= 0 && resizedLayout.footer.right <= 390, 'dark 390px resize: footer is not clipped horizontally');
    assert(resizedLayout.footer.top >= 0 && resizedLayout.footer.bottom <= resizedLayout.viewportHeight, 'dark 390px resize: footer is not clipped vertically');
    assert(resizedLayout.documentWidth <= resizedLayout.viewportWidth, 'dark 390px resize: footer does not create horizontal page overflow');
    assert.equal(resizedLayout.pinnedHeight, Math.ceil(resizedLayout.footer.height), 'dark 390px resize: pinned spacing tracks the resized footer');
    assert(resizedLayout.contentPaddingBottom >= resizedLayout.pinnedHeight, 'dark 390px resize: list padding reserves resized footer space');
    assert.notEqual(resizedLayout.pinnedHeight, desktopPinnedHeight, 'dark 390px resize: responsive wrapping updates pinned height');
    await page.setViewportSize({ width: 1440, height: 800 });
    await page.waitForFunction(() => {
      return window.isBookFooterPinnedHeightCurrent();
    });
  }

  if (process.env.CHAPTARR_LAYOUT_ARTIFACTS) {
    fs.mkdirSync(process.env.CHAPTARR_LAYOUT_ARTIFACTS, { recursive: true });
    await page.screenshot({ path: path.join(process.env.CHAPTARR_LAYOUT_ARTIFACTS, `${theme}-${width}-${mode}-library-footer.png`) });
  }
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
    const css = fs.readdirSync(path.join(output, 'Content')).find((file) => file.endsWith('.css'));
    let cases = 0;
    for (const theme of ['dark', 'light']) {
      for (const width of [320, 390, 600, 768, 769, 820, 900, 992, 1000, 1024, 1025, 1180, 1440]) {
        const context = await browser.newContext({ viewport: { width, height: 800 }, isMobile: width <= 1024, hasTouch: width <= 1024 });
        const page = await context.newPage();
        const errors = [];
        page.on('pageerror', (error) => errors.push(error.message));
        await page.route('**/*', (route) => {
          const pathname = new URL(route.request().url()).pathname;
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
          const variables = Object.entries((theme === 'dark' ? darkTheme : lightTheme)).map(([key, value]) => `--${key}:${value}`).join(';');
          return route.fulfill({ contentType: 'text/html', body: `<!doctype html><meta name="viewport" content="width=device-width,initial-scale=1"><link rel="stylesheet" href="/style.css"><style>:root{${variables}}body{margin:0}#root{margin-left:${width > 768 ? 210 : 0}px}</style><div id="root"></div><div id="portal-root"></div><script>window.Chaptarr={isProduction:true,apiRoot:'/api/v1'};</script><script src="/fixture.js"></script>` });
        });
        await page.goto('http://toolbar.test/');
        await page.evaluate(() => window.renderToolbarFixture());
        await page.getByText('Filter', { exact: true }).waitFor();
        if (theme === 'dark' && width === 320) {
          const data = await page.evaluate(() => window.getLibraryFilterAssertions());
          assert.deepEqual(data.menuKeys, ['all', 'library'], 'Library follows All in the Books filter list');
          assert.deepEqual(data.footerIds, [1, 2, 5], 'client footer population includes each row’s own monitoring flag and own files');
          assert.deepEqual(data.bookIndexIds, [1, 2, 5], 'table, overview, and client posters use the same Library population');
          assert.deepEqual(data.libraryIds.unscoped, [1, 2, 5], 'unscoped Books Library ignores monitoring flags from a different row media type');
          assert.deepEqual(data.libraryIds.audiobook, [1, 2], 'audiobook Library uses audiobook monitoring and always includes files');
          assert.deepEqual(data.libraryIds.ebook, [5], 'ebook Library uses its own monitoring flag');
          for (const [mediaType, query] of [['audiobook', data.audiobookQuery], ['ebook', data.ebookQuery]]) {
            assert.equal(query.clientSide, false, `${mediaType} Library posters use the server query path`);
            assert.equal(query.key, `title_ascending_library_${mediaType}`);
            assert.equal(query.params.library, true, `${mediaType} buckets, pages, IDs, and footer request Library`);
            assert.equal(query.params.mediaType, mediaType, 'the selected media enum remains on the request');
          }
        }
        // Focusing must reveal the actual control, not merely keep the document width fixed.
        const filter = page.getByText('Filter', { exact: true }).locator('xpath=ancestor::*[self::button or self::a][1]');
        await filter.focus();
        const visible = await filter.evaluate((element) => {
          const r = element.getBoundingClientRect();
          for (let parent = element.parentElement; parent; parent = parent.parentElement) {
            const style = getComputedStyle(parent);
            const p = parent.getBoundingClientRect();
            if (['hidden', 'auto', 'scroll'].includes(style.overflowX) && (r.left < p.left - 1 || r.right > p.right + 1)) {
              return false;
            }
          }
          return r.left >= 0 && r.right <= innerWidth;
        });
        assert(visible, `${theme} ${width}px: Filter is clipped after focus`);
        if (width === 900) {
          await filter.focus();
          await page.keyboard.press('Enter');
        } else {
          await filter.click();
        }
        const libraryOption = page.getByText('Library', { exact: true });
        await libraryOption.waitFor();
        assert.equal(await libraryOption.locator('xpath=ancestor::button[1]').getAttribute('title'), 'Monitored books and books with files', `${theme} ${width}px: Library tooltip uses the approved explanation`);
        if (process.env.CHAPTARR_LAYOUT_ARTIFACTS && [390, 900].includes(width)) {
          fs.mkdirSync(process.env.CHAPTARR_LAYOUT_ARTIFACTS, { recursive: true });
          await page.screenshot({ path: path.join(process.env.CHAPTARR_LAYOUT_ARTIFACTS, `${theme}-${width}-library-filter.png`) });
        }
        if ([390, 900].includes(width)) {
          await libraryOption.click();
          assert.equal(await page.evaluate(() => window.libraryFilterSelection), 'library', `${theme} ${width}px: selecting Library dispatches the existing filter selection`);
        } else {
          await page.mouse.click(5, 300);
        }
        await page.getByTitle('Options', { exact: true }).click();
        await page.getByText('Columns', { exact: true }).waitFor();
        await page.keyboard.press('Escape');
        if (process.env.CHAPTARR_LAYOUT_ARTIFACTS && [390, 900, 1025].includes(width)) {
          fs.mkdirSync(process.env.CHAPTARR_LAYOUT_ARTIFACTS, { recursive: true });
          await page.screenshot({ path: path.join(process.env.CHAPTARR_LAYOUT_ARTIFACTS, `${theme}-${width}.png`) });
        }
        if (width === 900) {
          await page.locator('button[aria-pressed]').filter({ hasText: 'eBooks' }).click();
          await page.setViewportSize({ width: 1440, height: 800 });
          await page.setViewportSize({ width: 900, height: 800 });
          assert.equal(await page.locator('[aria-pressed=true]').textContent(), '📖eBooks');
          await filter.click();
          await page.getByText('Library', { exact: true }).waitFor();
        }
        if (theme === 'dark' && width === 390) {
          await page.evaluate(() => window.renderPersistedLibraryMenu(true));
          await page.getByText('Filter', { exact: true }).click();
          await page.getByText('Library', { exact: true }).click();
          assert.equal(await page.locator('#persisted-filter-key').textContent(), 'library', 'real app store selects the Library filter');
          await page.getByRole('button', { name: 'Switch media type' }).click();
          assert.equal(await page.locator('#persisted-media-type').textContent(), 'ebook', 'real app store switches media type');
          assert.equal(await page.locator('#persisted-filter-key').textContent(), 'library', 'media switch preserves the selected Library filter');
          await page.evaluate(() => window.remountPersistedLibraryMenu());
          assert.equal(await page.locator('#persisted-filter-key').textContent(), 'library', 'same-store menu remount preserves the Library filter');
          assert.equal(await page.locator('#persisted-media-type').textContent(), 'ebook', 'same-store menu remount preserves selected media');
          await page.reload();
          await page.evaluate(() => window.renderPersistedLibraryMenu(false));
          assert.equal(await page.locator('#persisted-filter-key').textContent(), 'library', 'redux-localstorage restores Library after browser reload');
          assert.equal(await page.locator('#persisted-media-type').textContent(), 'ebook', 'media choice survives browser reload');
          const restoredEbookQuery = await page.evaluate(() => window.getPersistedLibraryQuery());
          assert.equal(restoredEbookQuery.key, 'title_ascending_library_ebook', 'restored ebook selection builds the Library query');
          assert.equal(restoredEbookQuery.params.library, true, 'restored ebook query sends library=true');
          assert.equal(restoredEbookQuery.params.mediaType, 'ebook', 'restored Library query keeps the ebook media type');
          await page.getByRole('button', { name: 'Switch media type' }).click();
          assert.equal(await page.locator('#persisted-media-type').textContent(), 'audiobook', 'real app store switches back to audiobook');
          assert.equal(await page.locator('#persisted-filter-key').textContent(), 'library', 'second media switch preserves the Library filter');
          const restoredAudiobookQuery = await page.evaluate(() => window.getPersistedLibraryQuery());
          assert.equal(restoredAudiobookQuery.key, 'title_ascending_library_audiobook', 'audiobook media switch keeps the Library query');
          assert.equal(restoredAudiobookQuery.params.library, true, 'audiobook Library query sends library=true');
        }
        if ([390, 1440].includes(width)) {
          await page.evaluate(() => window.renderBookRowDateFixture());
          const bookRows = await page.evaluate(() => window.getBookRowDateAssertions());
          assert.deepEqual(bookRows.map((item) => item.text), [
            strings.NotAvailable,
            strings.Missing,
            strings.NotAvailable,
            'MP3',
            strings.NotAvailable,
            strings.NotMonitored,
            '',
            ''
          ], 'real Author Details BookRow uses the shared date rule and keeps file status precedence');
          assert.deepEqual(bookRows.slice(0, 3).map((item) => item.title), [
            strings.NotAvailable,
            strings.BookAvailableButMissing,
            strings.NotAvailable
          ], 'real BookRow uses Readarr Not Available wording for undated and future dates and Missing for past dates');
          assert.match(bookRows[3].title, /^MP3 - /, 'real BookRow keeps the file quality label ahead of date status');
          assert.equal(bookRows[4].title, strings.NotAvailable, 'absent date uses the existing Not Available tooltip');
          assert.equal(bookRows[5].title, strings.NotMonitored, 'unmonitored status takes precedence over date fallback');
          assert.equal(bookRows[6].title, strings.BookIsDownloading, 'grabbed status takes precedence over monitoring and date fallback');
          assert.equal(bookRows[7].progress, '50', 'queue progress takes precedence over grabbed, monitoring and date fallback');
          const statusSort = await page.evaluate(() => window.getBookStatusSortAssertions());
          assert.deepEqual(statusSort, {
            unknownMonitored: 2,
            pastMonitored: 3,
            futureMonitored: 2,
            futureFileUnmonitored: 4
          }, 'real bookActions status sort uses the shared date rule and keeps file-bearing rows ahead');
          if (process.env.CHAPTARR_LAYOUT_ARTIFACTS) {
            fs.mkdirSync(process.env.CHAPTARR_LAYOUT_ARTIFACTS, { recursive: true });
            await page.screenshot({ path: path.join(process.env.CHAPTARR_LAYOUT_ARTIFACTS, `${theme}-${width}-author-bookrow-date-status.png`) });
          }
        }
        if ([390, 1440].includes(width)) {
          await page.evaluate(() => window.renderLibraryFooterFixture('client'));
          const themeColors = await page.evaluate(() => window.getBookFooterLayout().colors);
          await page.evaluate(() => window.renderBookProgressFixture());
          const progress = await page.evaluate(() => window.getBookProgressAssertions());
          const expectedCounts = [
            '1 / 1 (Files: 1)',
            '0 / 1 (Files: 0)',
            '0 / 1 (Files: 0)',
            '0 / 1 (Files: 0)',
            '0 / 1 (Files: 0)'
          ];
          assert.deepEqual(progress.map((item) => item.count), expectedCounts, `${theme} ${width}px: each book uses 1/1 or 0/1 progress text`);
          assert.deepEqual(progress.map((item) => item.color), [
            themeColors.available,
            themeColors.missing,
            themeColors.future,
            themeColors.future,
            themeColors.unmonitored
          ], `${theme} ${width}px: progress bars match the green, red, blue, and yellow legend colors`);
          assert(progress.every((item) => item.ariaValue === '100'), `${theme} ${width}px: status bars retain full-width meter semantics`);
          if (process.env.CHAPTARR_LAYOUT_ARTIFACTS) {
            fs.mkdirSync(process.env.CHAPTARR_LAYOUT_ARTIFACTS, { recursive: true });
            await page.screenshot({ path: path.join(process.env.CHAPTARR_LAYOUT_ARTIFACTS, `${theme}-${width}-book-progress.png`) });
          }
          await assertFooterPopulation(page, theme, width, 'client');
          await assertFooterPopulation(page, theme, width, 'infinite');
        }
        await page.evaluate(() => window.renderHeaderFixture());
        const bookmark = page.locator('[class*=AuthorDetailsHeader-toggleMonitoredContainer] svg').first();
        await bookmark.waitFor();
        const clippedIcons = await page.evaluate(() => [...document.querySelectorAll('svg')].filter((icon) => {
          const r = icon.getBoundingClientRect();
          if (!r.width || r.left < 0 || r.right > innerWidth) {
            return false;
          }
          for (let parent = icon.parentElement; parent; parent = parent.parentElement) {
            const style = getComputedStyle(parent);
            const p = parent.getBoundingClientRect();
            if (['hidden', 'auto', 'scroll'].includes(style.overflowY) && (r.top < p.top - 0.5 || r.bottom > p.bottom + 0.5)) {
              return true;
            }
          }
          return false;
        }).map((icon) => icon.getAttribute('data-icon')));
        assert.deepEqual(clippedIcons, [], `${theme} ${width}px: header icons clipped vertically`);
        const spellings = page.getByLabel(strings.AlternateSpellings, { exact: true });
        const spellingsHitTarget = await spellings.evaluate((element) => {
          const r = element.getBoundingClientRect();
          const hit = document.elementFromPoint(r.x + r.width / 2, r.y + r.height / 2);
          return element.contains(hit);
        });
        assert(spellingsHitTarget, `${theme} ${width}px: author navigation covers the alternate-spellings icon`);
        await spellings.hover();
        await page.getByText('An alternate provider spelling', { exact: true }).waitFor();
        await page.keyboard.press('Escape');
        await page.mouse.move(0, 400);
        await spellings.focus();
        await page.getByText('An alternate provider spelling', { exact: true }).waitFor();
        await page.keyboard.press('Escape');
        await page.getByTitle('Author list', { exact: true }).click();
        assert.equal(await page.evaluate(() => window.authorNavigationPressed), true, `${theme} ${width}px: navigation stays clickable`);
        if (process.env.CHAPTARR_LAYOUT_ARTIFACTS) {
          await page.screenshot({ path: path.join(process.env.CHAPTARR_LAYOUT_ARTIFACTS, `${theme}-${width}-author-header.png`) });
        }

        if ([320, 390].includes(width)) {
          await page.addInitScript(() => Object.defineProperty(navigator, 'userAgent', {
            value: 'Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 Chrome/130.0.0.0 Mobile Safari/537.36'
          }));
          await page.reload();
          await page.evaluate(() => window.renderHeaderFixture());
          // SwipeHeader renders a previous, current and next author on phones.
          const touchSpellings = page.getByRole('button', { name: strings.AlternateSpellings, exact: true }).nth(1);
          await touchSpellings.tap();
          await page.getByText('An alternate provider spelling', { exact: true }).waitFor();
          await page.keyboard.press('Escape');
          await touchSpellings.focus();
          await page.keyboard.press('Enter');
          await page.getByText('An alternate provider spelling', { exact: true }).waitFor();
          await page.keyboard.press('Escape');
          await page.getByTitle('Author list', { exact: true }).tap();
          assert.equal(await page.evaluate(() => window.authorNavigationPressed), true, `${theme} ${width}px: mobile navigation stays tappable`);
        }

        assert.deepEqual(errors, [], `${theme} ${width}px: browser errors`);
        cases++;
        await context.close();
      }
    }
    console.log(`PASS: ${cases} toolbar cases, including Library filtering and persistence, per-book status colors, undated BookRow and sort status, sticky legend layout, sidebar widths, and header icons`);
  } finally {
    await browser.close();
  }
})().catch((error) => {
  console.error(error.message);
  process.exitCode = 1;
}).finally(() => fs.rmSync(output, { recursive: true, force: true }));
