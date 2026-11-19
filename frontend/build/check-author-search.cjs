/* Render the real grouped search UI with local responses, including unload requests. */
const assert = require('node:assert/strict');
const { createHash } = require('node:crypto');
const fs = require('node:fs');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');
const webpack = require('webpack');
const { chromium } = require('playwright');
const checkReconnect = require('./author-search-reconnect-check.cjs');
const checkRetention = require('./author-search-retention-check.cjs');
const checkCompletion = require('./author-search-completion-check.cjs');
const checkTable = require('./author-search-table-check.cjs');
const checkAuthorSync = require('./author-sync-check.cjs');
const checkCommandMessages = require('./command-message-check.cjs');

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

async function setAddAuthorCheckbox(page, name, value) {
  const input = page.locator(`input[name="${name}"]`);
  if (await input.isChecked() !== value) {
    await input.locator('..').click();
  }
  assert.equal(await input.isChecked(), value, 'The visible incumbent checkbox must reflect the selected boolean');
}

async function selectAddAuthorMonitor(page, label, index, optionLabel) {
  const group = page.locator('#portal-root [class*="FormGroup-group"]').filter({
    has: page.locator('[class*="FormLabel-label"]').filter({ hasText: label })
  }).nth(index);
  await group.getByRole('button').first().click();
  await page.locator('[class*="EnhancedSelectInput-optionsContainer"]:visible').getByText(optionLabel, { exact: true }).click();
}

const modalChoices = {
  audiobook: {
    defaults: { root: '/fixture/audio', quality: 11, metadata: 21, monitored: false, monitor: 'all', newItems: 'new', tags: [41] },
    overrides: { root: '/fixture/audio-alt', quality: 12, metadata: 22, monitored: true, monitor: 'existing', newItems: 'none', tags: [43] }
  },
  ebook: {
    defaults: { root: '/fixture/ebook', quality: 13, metadata: 23, monitored: true, monitor: 'missing', newItems: 'none', tags: [42] },
    overrides: { root: '/fixture/ebook-alt', quality: 14, metadata: 24, monitored: false, monitor: 'none', newItems: 'all', tags: [] }
  }
};

async function openCapturedSearchResult(page, capture, kind, mediaType) {
  const selected = { ...capture[kind], searchProvider: capture.provider };
  await page.evaluate(({ mode, family, result }) => window.renderAddAuthorFixture(mode, family, result), {
    mode: mediaType, family: `captured-${kind}`, result: selected
  });
  const prefix = kind === 'author' ? 'AddNewAuthorSearchResult' : 'AddNewBookSearchResult';
  await page.locator(`[class*="${prefix}-underlay"]`).click();
}

async function checkAddAuthorPayloads(page, importRequests, capture = null) {
  const choices = modalChoices;
  const selectedAuthor = capture?.author || { foreignAuthorId: 'gr:fixture-selected-author', authorName: 'Selected Search Writer' };
  const modalReceipts = [];
  const failures = [];
  for (const mediaType of ['audiobook', 'ebook', 'both']) {
    for (const overrides of capture ? [true] : [false, true]) {
      const start = importRequests.length;
      if (capture) {
        await openCapturedSearchResult(page, capture, 'author', mediaType);
      } else {
        await page.evaluate((mode) => window.renderAddAuthorFixture(mode), mediaType);
        await page.locator('[class*="AddNewAuthorSearchResult-underlay"]').click();
      }
      await page.getByText(selectedAuthor.authorName, { exact: true }).last().waitFor();
      const sides = mediaType === 'both' ? ['audiobook', 'ebook'] : [mediaType];
      for (const side of sides) {
        const selection = choices[side].overrides;
        await page.locator(`select[name="${side}QualityProfileId"] option[value="${selection.quality}"]`).waitFor({ state: 'attached' });
        await page.locator(`select[name="${side}MetadataProfileId"] option[value="${selection.metadata}"]`).waitFor({ state: 'attached' });
        assert.equal(await page.locator(`select[name="${side}MetadataProfileId"] option[value="25"]`).count(), 0, 'The existing author flow excludes a metadata profile named None');
        if (overrides) {
          await page.locator('[class*="RootFolderSelectInputSelectedValue-path"]').filter({ hasText: choices[side].defaults.root }).first().click();
          await page.locator('[class*="RootFolderSelectInputOption-value"]').filter({ hasText: selection.root }).click();
          await setAddAuthorCheckbox(page, `${side}Monitored`, selection.monitored);
          const sideIndex = sides.indexOf(side);
          const strings = JSON.parse(fs.readFileSync(path.join(root, 'src/NzbDrone.Core/Localization/Core/en.json')));
          await selectAddAuthorMonitor(page, strings.InitialBookMonitoring, sideIndex, side === 'audiobook' ? strings.BooksWithFiles : strings.None);
          await selectAddAuthorMonitor(page, strings.MonitorNewBooks, sideIndex, side === 'audiobook' ? strings.None : strings.AllNewBooks);
          await page.locator(`select[name="${side}QualityProfileId"]`).selectOption(String(selection.quality));
          await page.locator(`select[name="${side}MetadataProfileId"]`).selectOption(String(selection.metadata));
          const tags = page.locator(`input[name="${side}Tags"]`);
          await tags.focus();
          await tags.press('Backspace');
          if (selection.tags.length) {
            await tags.fill('Chosen');
            await tags.press('ArrowDown');
            await tags.press('Enter');
          }
        }
      }
      if (overrides) {
        await setAddAuthorCheckbox(page, 'searchForMissingBooks', true);
      }
      const selected = await page.evaluate(() => ({ fields: [...document.querySelectorAll('#portal-root select, #portal-root input')].map((input) => ({ name: input.name, value: input.value, checked: input.type === 'checkbox' ? input.checked : undefined })), authorDefaults: window.readAddAuthorFixtureState().authorDefaults }));
      const labels = { both: 'Add Audiobooks + Ebooks', audiobook: 'Add Audiobooks', ebook: 'Add Ebooks' };
      const add = page.getByRole('button', { name: labels[mediaType], exact: true });
      if (capture) {
        const responses = [page.waitForResponse((response) => {
          const request = response.request();
          return new URL(response.url()).pathname === '/api/v1/author/import' && request.method() === 'POST' && request.postDataJSON().mediaType === mediaType;
        })];
        await add.click();
        await Promise.all(responses);
      } else {
        await add.focus();
        await page.keyboard.press('Enter');
      }
      if (!capture) {
        await page.waitForFunction(() => window.readAddAuthorFixtureState().isQueued && !window.readAddAuthorFixtureState().isAdding);
      }
      const posted = importRequests.slice(start);
      modalReceipts.push({ mediaType, overrides, selected, posted, provenance: capture ? `Captured ${capture.provider} search response; local-library overlay cleared; actual selected search-card/modal/thunk; intercepted POST` : 'Synthetic provider-search card; real connected modal and production store thunk; all requests intercepted locally' });
      try {
        assert.equal(posted.length, 1, 'Both uses one durable import request');
        assert.deepEqual(await page.evaluate(() => window.readAddAuthorFixtureState().authorDefaults), selected.authorDefaults, 'Queued submission preserves selected settings');
        const expected = {
          foreignAuthorId: selectedAuthor.foreignAuthorId, authorName: selectedAuthor.authorName, mediaType,
          ...(mediaType === 'both' ? { syncMonitoredAcrossFormats: false } : {}), manualFlag: true, searchForMissingBooks: overrides
        };
        for (const side of sides) {
          const selection = choices[side][overrides ? 'overrides' : 'defaults'];
          Object.assign(expected, {
            [`${side}MonitorExistingMode`]: selection.monitor,
            [`${side}Monitored`]: selection.monitored, [`${side}MonitorNewItems`]: selection.newItems
          });
          if (mediaType === 'both') {
            Object.assign(expected, {
              [`${side}RootFolderPath`]: selection.root, [`${side}QualityProfileId`]: selection.quality,
              [`${side}MetadataProfileId`]: selection.metadata, [`${side}Tags`]: selection.tags
            });
          } else {
            Object.assign(expected, {
              rootFolder: selection.root, qualityProfileId: selection.quality, metadataProfileId: selection.metadata,
              monitor: selection.monitor, tags: selection.tags
            });
          }
          const profileField = selected.fields.find((field) => field.name === `${side}QualityProfileId`);
          assert.equal(profileField.value, String(selection.quality));
        }
        assert.equal(posted[0].method, 'POST');
        assert.equal(posted[0].pathname, '/api/v1/author/import');
        assert.deepEqual(posted[0].body, expected, 'Every serialized import field preserves the real modal choices');
      } catch (error) {
        failures.push({ mediaType, overrides, error: error.message });
      }
    }
  }
  if (process.env.CHAPTARR_LAYOUT_ARTIFACTS) {
    fs.mkdirSync(process.env.CHAPTARR_LAYOUT_ARTIFACTS, { recursive: true });
    fs.writeFileSync(path.join(process.env.CHAPTARR_LAYOUT_ARTIFACTS, capture ? `provider-${capture.provider}-author-payloads.json` : 'modal-payloads.json'), JSON.stringify({ modalReceipts, failures }, null, 2));
    await page.screenshot({ path: path.join(process.env.CHAPTARR_LAYOUT_ARTIFACTS, capture ? `provider-${capture.provider}-author.png` : 'add-author-both-overrides.png'), fullPage: true });
  }
  assert.deepEqual(failures, [], 'All selected Add New fields must survive every import payload');
}

function checkBookModalMetadata(page) {
  return page.evaluate(() => {
    const rows = [];
    for (const mediaType of ['audiobook', 'ebook']) {
      for (const profileId of [53, 63]) {
        rows.push({ mediaType, profileId, payload: window.getBookModalMetadataPayload(mediaType, profileId) });
      }
    }
    rows.push({ mediaType: 'legacy', profileId: 73, payload: window.getBookModalMetadataPayload(undefined, undefined, true) });
    return rows;
  }).then((rows) => {
    for (const { mediaType, profileId, payload } of rows) {
      assert.equal(payload.id, 0);
      assert.equal(payload.localBookId, null);
      assert.equal(payload.addOptions.searchForNewBook, true);
      assert.equal(payload.editions[0].foreignEditionId, 'hc:edition:8200-audiobook');
      assert.equal(payload.editions[0].manualAdd, false);
      if (mediaType === 'legacy') {
        assert.equal(payload.author.audiobookMetadataProfileId, 73);
        assert.equal(payload.author.ebookMetadataProfileId, 73);
      } else {
        const side = mediaType;
        const other = side === 'audiobook' ? 'ebook' : 'audiobook';
        assert.equal(payload.author[`${side}MetadataProfileId`], profileId, 'Selected metadata uses the requested side before native root normalization');
        assert.equal(payload.author[`${other}MetadataProfileId`], other === 'audiobook' ? 93 : 94, 'An inactive media side retains its original metadata');
        assert.equal(payload.author[`${side}Monitored`], false);
        assert.equal(payload.author[`${side}MonitorNewItems`], 'none');
        assert.deepEqual(payload.author[`${side}Tags`], []);
      }
    }
  });
}

async function checkAddSeriesPayloads(page, seriesRequests, selectedBooks = [{ foreignBookId: 'hc:8100', foreignAuthorId: 'hc:8000', authorName: 'Selected Search Writer' }]) {
  const receipts = [];
  const failures = [];
  const defaults = {
    audiobook: { root: '/fixture/audio', quality: 11, metadata: 21, monitored: false, monitor: 'all', newItems: 'new', tags: [41] },
    ebook: { root: '/fixture/ebook', quality: 13, metadata: 23, monitored: true, monitor: 'missing', newItems: 'none', tags: [42] }
  };
  const overrides = {
    audiobook: { root: '/fixture/audio-alt', quality: 12, metadata: 22, monitored: true, monitor: 'existing', newItems: 'none', tags: [] },
    ebook: { root: '/fixture/ebook-alt', quality: 14, metadata: 24, monitored: false, monitor: 'none', newItems: 'all', tags: [43] }
  };
  const strings = JSON.parse(fs.readFileSync(path.join(root, 'src/NzbDrone.Core/Localization/Core/en.json')));
  for (const mediaType of ['audiobook', 'ebook', 'both']) {
    for (const useOverrides of [false, true]) {
      const start = seriesRequests.length;
      await page.evaluate((mode) => window.renderAddSeriesFixture(mode), mediaType);
      const sides = mediaType === 'both' ? ['audiobook', 'ebook'] : [mediaType];
      const add = page.getByRole('button', { name: strings.AddSelected, exact: true });
      await add.waitFor();
      for (const side of sides) {
        await page.locator(`select[name="${side}MetadataProfileId"] option[value="${overrides[side].metadata}"]`).waitFor({ state: 'attached' });
        assert.equal(await page.locator(`select[name="${side}MetadataProfileId"] option[value="25"]`).count(), 0, 'Series follows the existing exclusion of a named None profile');
        if (useOverrides) {
          await page.locator('[class*="RootFolderSelectInputSelectedValue-path"]').filter({ hasText: defaults[side].root }).first().click();
          await page.locator('[class*="RootFolderSelectInputOption-value"]').filter({ hasText: overrides[side].root }).click();
          await setAddAuthorCheckbox(page, `${side}Monitored`, overrides[side].monitored);
          await selectAddAuthorMonitor(page, strings.InitialBookMonitoring, sides.indexOf(side), side === 'audiobook' ? strings.BooksWithFiles : strings.None);
          await selectAddAuthorMonitor(page, strings.MonitorNewBooks, sides.indexOf(side), side === 'audiobook' ? strings.None : strings.AllNewBooks);
          await page.locator(`select[name="${side}QualityProfileId"]`).selectOption(String(overrides[side].quality));
          await page.locator(`select[name="${side}MetadataProfileId"]`).selectOption(String(overrides[side].metadata));
          const tags = page.locator(`input[name="${side}Tags"]`);
          await tags.focus();
          await tags.press('Backspace');
          if (overrides[side].tags.length) {
            await tags.fill('Chosen');
            await tags.press('ArrowDown');
            await tags.press('Enter');
          }
        }
      }
      const responses = sides.map((side) => page.waitForResponse((response) => {
        const request = response.request();
        return new URL(response.url()).pathname === '/api/v1/series/add' &&
          request.method() === 'POST' && request.postDataJSON().selectedMediaType === side;
      }));
      await add.click();
      await Promise.all(responses);
      const posted = seriesRequests.slice(start);
      receipts.push({ mediaType, useOverrides, posted });
      try {
        assert.equal(posted.length, sides.length);
        for (let i = 0; i < sides.length; i++) {
          const side = sides[i];
          const chosen = (useOverrides ? overrides : defaults)[side];
          assert.deepEqual(posted[i].body, {
            foreignSeriesId: 'hc:8300', selectedMediaType: side,
            selectedBooks,
            monitor: chosen.monitor, tags: chosen.tags,
            [`${side}Monitored`]: chosen.monitored, [`${side}MonitorExistingMode`]: chosen.monitor, [`${side}MonitorNewItems`]: chosen.newItems,
            [`${side}RootFolderPath`]: chosen.root, [`${side}QualityProfileId`]: chosen.quality, [`${side}MetadataProfileId`]: chosen.metadata
          });
        }
      } catch (error) {
        failures.push({ mediaType, useOverrides, error: error.message });
      }
    }
  }
  for (const side of ['audiobook', 'ebook']) {
    const start = seriesRequests.length;
    await page.evaluate((mode) => window.renderAddSeriesFixture(mode), side);
    await page.locator(`select[name="${side}MetadataProfileId"] option[value="${defaults[side].metadata}"]`).waitFor({ state: 'attached' });
    await page.locator(`select[name="${side}QualityProfileId"]`).selectOption(String(defaults[side].quality));
    await page.locator(`select[name="${side}MetadataProfileId"]`).selectOption(String(defaults[side].metadata));
    await setAddAuthorCheckbox(page, `${side}Monitored`, defaults[side].monitored);
    const tags = page.locator(`input[name="${side}Tags"]`);
    await tags.fill('Chosen');
    await tags.press('ArrowDown');
    await tags.press('Enter');
    const before = await page.evaluate(() => window.readAddAuthorFixtureState().authorDefaults);
    await page.locator('[class*="RootFolderSelectInputSelectedValue-path"]').filter({ hasText: defaults[side].root }).first().click();
    await page.locator('[class*="RootFolderSelectInputOption-value"]').filter({ hasText: overrides[side].root }).click();
    const after = await page.evaluate(() => window.readAddAuthorFixtureState().authorDefaults);
    const other = side === 'audiobook' ? 'ebook' : 'audiobook';
    for (const key of Object.keys(before).filter((field) => field.startsWith(other))) {
      assert.deepEqual(after[key], before[key], 'Changing a root resets only the chosen media side');
    }
    const response = page.waitForResponse((result) => {
      const request = result.request();
      return new URL(result.url()).pathname === '/api/v1/series/add' &&
        request.method() === 'POST' && request.postDataJSON().selectedMediaType === side;
    });
    await page.getByRole('button', { name: strings.AddSelected, exact: true }).click();
    await response;
    const body = seriesRequests[start]?.body;
    const chosen = { ...overrides[side], monitor: side === 'audiobook' ? 'missing' : 'none' };
    receipts.push({ mediaType: side, rootChange: true, before, after, posted: body });
    try {
      assert.equal(body[`${side}QualityProfileId`], chosen.quality);
      assert.equal(body[`${side}MetadataProfileId`], chosen.metadata);
      assert.equal(body[`${side}Monitored`], chosen.monitored);
      assert.equal(body.monitor, chosen.monitor);
      assert.equal(body[`${side}MonitorNewItems`], chosen.newItems);
      assert.deepEqual(body.tags, chosen.tags);
    } catch (error) {
      failures.push({ mediaType: side, rootChange: true, error: error.message });
    }
  }
  if (process.env.CHAPTARR_LAYOUT_ARTIFACTS) {
    fs.writeFileSync(path.join(process.env.CHAPTARR_LAYOUT_ARTIFACTS, 'series-modal-payloads.json'), JSON.stringify({ receipts, failures }, null, 2));
  }
  assert.deepEqual(failures, [], 'Shared author/series default authority preserves every submitted field');
}

async function applyCapturedBookChoices(page, sides) {
  const strings = JSON.parse(fs.readFileSync(path.join(root, 'src/NzbDrone.Core/Localization/Core/en.json')));
  for (const side of sides) {
    const selection = modalChoices[side].overrides;
    const label = side === 'audiobook' ? strings.AudiobookRootFolder : strings.EbookRootFolder;
    const group = page.locator('#portal-root [class*="FormGroup-group"]').filter({
      has: page.locator('[class*="FormLabel-label"]').filter({ hasText: label })
    });
    await group.getByRole('button').first().click();
    await page.locator('[class*="RootFolderSelectInputOption-value"]').filter({ hasText: selection.root }).click();
    await setAddAuthorCheckbox(page, `${side}Monitored`, selection.monitored);
    await selectAddAuthorMonitor(page, strings.InitialBookMonitoring, sides.indexOf(side), /^(Only This Book|OnlyThisBook)$/);
    await selectAddAuthorMonitor(page, strings.MonitorNewBooks, sides.indexOf(side), side === 'audiobook' ? strings.None : strings.AllNewBooks);
    await page.locator(`select[name="${side}QualityProfileId"] option[value="${selection.quality}"]`).waitFor({ state: 'attached' });
    await page.locator(`select[name="${side}MetadataProfileId"] option[value="${selection.metadata}"]`).waitFor({ state: 'attached' });
    await page.locator(`select[name="${side}QualityProfileId"]`).selectOption(String(selection.quality));
    await page.locator(`select[name="${side}MetadataProfileId"]`).selectOption(String(selection.metadata));
    const tags = page.locator(`input[name="${side}Tags"]`);
    await tags.focus();
    await tags.press('Backspace');
    if (selection.tags.length) {
      await tags.fill('Chosen');
      await tags.press('ArrowDown');
      await tags.press('Enter');
    }
  }
  await setAddAuthorCheckbox(page, 'searchForNewBook', true);
}

function assertInactiveAuthorFields(actual, original, mediaType) {
  for (const suffix of ['RootFolderPath', 'QualityProfileId', 'MetadataProfileId', 'Monitored', 'MonitorNewItems', 'Tags']) {
    assert.deepEqual(actual[`${mediaType}${suffix}`], original[`${mediaType}${suffix}`]);
  }
}

async function checkCapturedProviderPayloads(page, importRequests, bookRequests, seriesRequests) {
  if (!process.env.CHAPTARR_PROVIDER_CAPTURE_MANIFEST) {
    return;
  }
  const manifest = JSON.parse(fs.readFileSync(process.env.CHAPTARR_PROVIDER_CAPTURE_MANIFEST));
  const bookReceipts = [];
  const overlays = [];
  for (const entry of manifest) {
    for (const kind of ['author', 'book']) {
      const file = entry[`${kind}Capture`];
      const captureBytes = fs.readFileSync(file);
      const captureSha256 = createHash('sha256').update(captureBytes).digest('hex');
      const raw = JSON.parse(captureBytes);
      const rows = structuredClone(raw);
      const clearLocalAuthor = (author) => {
        if (!author) {
          return;
        }
        author.id = 0;
        author.folder = '';
        for (const key of Object.keys(author)) {
          if (key.endsWith('Path') || ['path', 'qualityProfileId', 'metadataProfileId', 'tags', 'monitored', 'monitorNewItems'].includes(key) || (/^(audiobook|ebook)(QualityProfileId|MetadataProfileId|Tags|Monitored|MonitorNewItems)$/).test(key)) {
            delete author[key];
          }
        }
      };
      for (const row of rows) {
        delete row.existingLocalId;
        if (row.author) {
          clearLocalAuthor(row.author);
        }
        if (row.book) {
          row.book.id = 0;
          row.book.localBookId = null;
          row.book.localAudiobookBooks = [];
          row.book.localEbookBooks = [];
          if (row.book.author) {
            clearLocalAuthor(row.book.author);
          }
        }
      }
      const author = rows.find((row) => row.author?.foreignAuthorId === entry.authorId)?.author;
      const book = rows.find((row) => row.book?.foreignBookId === entry.bookId)?.book;
      assert.ok(author?.authorName && author.foreignAuthorId, 'A real selected provider author must have its own name and ID');
      if (kind === 'book') {
        assert.ok(book && book.author?.foreignAuthorId === author.foreignAuthorId);
      }
      const receipt = JSON.parse(fs.readFileSync(entry[`${kind}Receipt`]));
      const capture = { provider: entry.provider, rows, author, book, term: new URL(receipt.request, 'http://capture.test').searchParams.get('term') };
      overlays.push({ provider: entry.provider, kind, capture: file, captureSha256, authorName: author.authorName, authorId: author.foreignAuthorId, workId: book?.foreignBookId, localOnlyOverlay: true });
      if (kind === 'author') {
        await checkAddAuthorPayloads(page, importRequests, capture);
      } else {
        for (const mode of ['audiobook', 'ebook', 'both']) {
          await openCapturedSearchResult(page, capture, 'book', mode);
          const sides = mode === 'both' ? ['audiobook', 'ebook'] : [mode];
          await applyCapturedBookChoices(page, sides);
          const start = bookRequests.length;
          const responses = sides.map((side) => page.waitForResponse((response) => {
            const request = response.request();
            return new URL(response.url()).pathname === '/api/v1/book' && request.method() === 'POST' && request.postDataJSON().mediaType === side;
          }));
          const labels = { audiobook: 'Add Audiobook', ebook: 'Add eBook', both: 'Add Audiobook + eBook' };
          await page.getByRole('button', { name: labels[mode], exact: true }).click();
          await Promise.all(responses);
          const posted = bookRequests.slice(start);
          assert.equal(posted.length, sides.length);
          for (let i = 0; i < sides.length; i++) {
            const side = sides[i];
            const other = side === 'audiobook' ? 'ebook' : 'audiobook';
            const body = posted[i].body;
            const choice = modalChoices[side].overrides;
            assert.equal(body.foreignBookId, entry.bookId);
            assert.equal(body.author.foreignAuthorId, entry.authorId);
            assert.equal(body.author.authorName, capture.book.author.authorName);
            assert.equal(body.mediaType, side);
            assert.equal(body.id, 0);
            assert.equal(body.localBookId, null);
            assert.equal(body[`${side}Monitored`], true);
            assert.equal(body[`${other}Monitored`], false);
            assert.equal(body.addOptions.searchForNewBook, true);
            assert.equal(body.author[`${side}RootFolderPath`], choice.root);
            assert.equal(body.author[`${side}QualityProfileId`], choice.quality);
            assert.equal(body.author[`${side}MetadataProfileId`], choice.metadata);
            assert.equal(body.author[`${side}Monitored`], choice.monitored);
            assert.equal(body.author[`${side}MonitorNewItems`], choice.newItems);
            assert.deepEqual(body.author[`${side}Tags`], choice.tags);
            assert.equal(body.author.addOptions.monitor, 'specificBook');
            assert.deepEqual(body.author.addOptions.booksToMonitor, [entry.bookId]);
            assertInactiveAuthorFields(body.author, capture.book.author, other);
            assert.deepEqual(body.editions.map((edition) => ({ id: edition.foreignEditionId, names: edition.narratorNames, manualAdd: edition.manualAdd })), capture.book.editions.map((edition) => ({ id: edition.foreignEditionId, names: edition.narratorNames, manualAdd: edition.manualAdd })));
          }
          bookReceipts.push({ provider: entry.provider, mode, source: file, posted });
        }
      }
    }
  }
  if (process.env.CHAPTARR_LAYOUT_ARTIFACTS) {
    fs.writeFileSync(path.join(process.env.CHAPTARR_LAYOUT_ARTIFACTS, 'provider-book-payloads.json'), JSON.stringify({ bookReceipts, overlays }, null, 2));
  }
  const audibleEntry = manifest.find((entry) => entry.provider === 'audible');
  const missingIdBook = JSON.parse(fs.readFileSync(audibleEntry.bookCapture)).find((row) => row.book?.foreignBookId === 'az:B09G7B8XXY')?.book;
  assert.ok(missingIdBook?.author?.authorName);
  assert.equal(missingIdBook.author.foreignAuthorId, '');
  await page.evaluate((book) => window.renderAddAuthorFixture('audiobook', 'captured-book', {
    ...book, id: 0, localBookId: null, localAudiobookBooks: [], localEbookBooks: []
  }), missingIdBook);
  assert.equal(await page.locator('[class*="AddNewBookSearchResult-underlay"]').isDisabled(), true, 'A real provider result without an author ID stays non-addable');
  const missingIdReceipt = { workId: missingIdBook.foreignBookId, authorName: missingIdBook.author.authorName, authorId: missingIdBook.author.foreignAuthorId, disabled: true };
  fs.writeFileSync(path.join(process.env.CHAPTARR_LAYOUT_ARTIFACTS, 'missing-provider-author-id.json'), JSON.stringify(missingIdReceipt, null, 2));

  const goodreadsEntry = manifest.find((entry) => entry.provider === 'goodreads');
  const goodreadsRows = JSON.parse(fs.readFileSync(goodreadsEntry.bookCapture));
  const knownBooks = ['gr:113603', 'gr:220778298'].map((id) => {
    const book = goodreadsRows.find((row) => row.book?.foreignBookId === id)?.book;
    assert.ok(book?.author?.foreignAuthorId && book.author.authorName);
    return { foreignBookId: id, foreignAuthorId: book.author.foreignAuthorId, authorName: book.author.authorName, title: book.title };
  });
  const seriesBooks = [...knownBooks, { foreignBookId: 'hc:8102', foreignAuthorId: 'hc:8002', title: 'Name Absent Control' }];
  const selectedBooks = seriesBooks.map(({ foreignBookId, foreignAuthorId, authorName }) => ({ foreignBookId, foreignAuthorId, ...(authorName === undefined ? {} : { authorName }) }));
  const seriesHandler = (route) => route.fulfill({ json: { foreignSeriesId: 'hc:8300', books: seriesBooks } });
  await page.route('**/api/v1/series/lookup?*', seriesHandler);
  await checkAddSeriesPayloads(page, seriesRequests, selectedBooks);
  await page.unroute('**/api/v1/series/lookup?*', seriesHandler);

}

async function checkModalReopenAndZeroFallback(page, seriesRequests, bookRequests) {
  const receipts = [];
  const failures = [];
  const strings = JSON.parse(fs.readFileSync(path.join(root, 'src/NzbDrone.Core/Localization/Core/en.json')));
  const defaults = {
    audiobook: { quality: 11, metadata: 21, monitored: false, monitor: 'all', newItems: 'new', tags: [41] },
    ebook: { quality: 13, metadata: 23, monitored: true, monitor: 'missing', newItems: 'none', tags: [42] }
  };
  const manual = {
    audiobook: { quality: 12, metadata: 22, monitored: true, monitor: 'existing', newItems: 'none', tags: [] },
    ebook: { quality: 14, metadata: 24, monitored: false, monitor: 'none', newItems: 'all', tags: [43] }
  };
  for (const mode of ['audiobook', 'ebook', 'both']) {
    await page.evaluate((value) => window.renderAddSeriesFixture(value), mode);
    const sides = mode === 'both' ? ['audiobook', 'ebook'] : [mode];
    for (const side of sides) {
      await page.locator(`select[name="${side}MetadataProfileId"] option[value="${manual[side].metadata}"]`).waitFor({ state: 'attached' });
      await setAddAuthorCheckbox(page, `${side}Monitored`, manual[side].monitored);
      await selectAddAuthorMonitor(page, strings.InitialBookMonitoring, sides.indexOf(side), side === 'audiobook' ? strings.BooksWithFiles : strings.None);
      await selectAddAuthorMonitor(page, strings.MonitorNewBooks, sides.indexOf(side), side === 'audiobook' ? strings.None : strings.AllNewBooks);
      await page.locator(`select[name="${side}QualityProfileId"]`).selectOption(String(manual[side].quality));
      await page.locator(`select[name="${side}MetadataProfileId"]`).selectOption(String(manual[side].metadata));
      const tags = page.locator(`input[name="${side}Tags"]`);
      await tags.focus();
      await tags.press('Backspace');
      if (manual[side].tags.length) {
        await tags.fill('Chosen');
        await tags.press('ArrowDown');
        await tags.press('Enter');
      }
    }
    for (const phase of ['manual', 'reopened']) {
      if (phase === 'reopened') {
        await page.locator('#portal-root [class*="ModalContent-modalContent-"]').waitFor({ state: 'detached' });
        await page.evaluate(() => window.reopenAddSeriesFixture());
        await page.locator(`select[name="${sides[0]}MetadataProfileId"] option[value="${defaults[sides[0]].metadata}"]`).waitFor({ state: 'attached' });
      }
      const start = seriesRequests.length;
      const responses = sides.map((side) => page.waitForResponse((response) => {
        const request = response.request();
        return new URL(response.url()).pathname === '/api/v1/series/add' && request.method() === 'POST' && request.postDataJSON().selectedMediaType === side;
      }));
      await page.getByRole('button', { name: strings.AddSelected, exact: true }).click();
      await Promise.all(responses);
      const posted = seriesRequests.slice(start);
      receipts.push({ family: 'series', mode, phase, posted });
      try {
        assert.equal(posted.length, sides.length);
        for (let i = 0; i < sides.length; i++) {
          const side = sides[i];
          const chosen = (phase === 'manual' ? manual : defaults)[side];
          assert.deepEqual(posted[i].body, {
            foreignSeriesId: 'hc:8300', selectedMediaType: side,
            selectedBooks: [{ foreignBookId: 'hc:8100', foreignAuthorId: 'hc:8000', authorName: 'Selected Search Writer' }],
            monitor: chosen.monitor, tags: chosen.tags,
            [`${side}Monitored`]: chosen.monitored, [`${side}MonitorExistingMode`]: chosen.monitor, [`${side}MonitorNewItems`]: chosen.newItems,
            [`${side}RootFolderPath`]: `/fixture/${side === 'audiobook' ? 'audio' : 'ebook'}`,
            [`${side}QualityProfileId`]: chosen.quality, [`${side}MetadataProfileId`]: chosen.metadata
          });
        }
      } catch (error) {
        failures.push({ family: 'series', mode, phase, error: error.message });
      }
    }
  }
  for (const mode of ['audiobook', 'ebook', 'both']) {
    const sides = mode === 'both' ? ['audiobook', 'ebook'] : [mode];
    const start = bookRequests.length;
    const responses = sides.map((side) => page.waitForResponse((response) => {
      const request = response.request();
      return new URL(response.url()).pathname === '/api/v1/book' && request.method() === 'POST' && request.postDataJSON().mediaType === side;
    }));
    await page.evaluate((value) => window.getBookModalMetadataPayload(value, 0, false, true), mode);
    await Promise.all(responses);
    const posted = bookRequests.slice(start);
    receipts.push({ family: 'book-action', mode, posted });
    try {
      assert.equal(posted.length, sides.length);
      for (let i = 0; i < sides.length; i++) {
        const side = sides[i];
        const other = side === 'audiobook' ? 'ebook' : 'audiobook';
        assert.equal(posted[i].body.author[`${side}MetadataProfileId`], 73, 'A zero unset sentinel uses the existing positive generic fallback');
        assert.equal(posted[i].body.author[`${other}MetadataProfileId`], other === 'audiobook' ? 93 : 94, 'The inactive source side stays unchanged');
      }
    } catch (error) {
      failures.push({ family: 'book-action', mode, error: error.message });
    }
  }
  if (process.env.CHAPTARR_LAYOUT_ARTIFACTS) {
    fs.writeFileSync(path.join(process.env.CHAPTARR_LAYOUT_ARTIFACTS, 'reopen-zero-payloads.json'), JSON.stringify({ receipts, failures }, null, 2));
  }
  assert.deepEqual(failures, [], 'New modal openings reuse root defaults; existing positive metadata fallback excludes zero');
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
  const unloadStops = [];
  const server = http.createServer((request, response) => {
    const url = new URL(request.url, 'http://localhost');
    if (request.method === 'DELETE' && url.pathname === '/api/v1/release') {
      unloadStops.push({ searchId: url.searchParams.get('searchId'), apiKey: request.headers['x-api-key'] });
      response.writeHead(200, { 'Content-Type': 'application/json' });
      response.end(JSON.stringify({ isStopping: true }));
      return;
    }
    response.writeHead(404);
    response.end();
  });
  try {
    await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
    const css = fs.readdirSync(path.join(output, 'Content')).find((name) => name.endsWith('.css'));
    const strings = JSON.parse(fs.readFileSync(path.join(root, 'src/NzbDrone.Core/Localization/Core/en.json')));
    const context = await browser.newContext();
    const page = await context.newPage();
    const errors = [];
    const releaseRequests = [];
    const importRequests = [];
    const seriesRequests = [];
    const bookRequests = [];
    const saveProviderWire = () => {
      if (process.env.CHAPTARR_PROVIDER_CAPTURE_MANIFEST && process.env.CHAPTARR_LAYOUT_ARTIFACTS) {
        fs.mkdirSync(process.env.CHAPTARR_LAYOUT_ARTIFACTS, { recursive: true });
        fs.writeFileSync(path.join(process.env.CHAPTARR_LAYOUT_ARTIFACTS, 'provider-wire-incremental.json'), JSON.stringify({ importRequests, bookRequests, seriesRequests }, null, 2));
      }
    };
    const authorRequests = [];
    const grabRequests = [];
    const stopRequests = [];
    const stopResponseStatuses = [];
    const expectedStopResourceErrors = [];
    let pendingExpectedStopResourceErrorCount = 0;
    const requestFailures = [];
    const authorLifecycleEvents = [];
    const activeAuthorSearches = new Set();
    const completedSearches = new Map();
    const sendAuthorResults = async(body) => {
      const result = { startedAt: new Date().toISOString(), ...body };
      if (result.isComplete) {
        completedSearches.set(`${result.authorId}:${result.mediaType}`, result);
      }
      await page.evaluate((value) => window.sendAuthorSearchSignalR(value),
        result.isComplete ? { ...result, bookResults: [] } : result);
    };
    let heldAdmissionRoute = null;
    let heldAdmissionCommand = null;
    let holdNextAdmission = false;
    const heldBookRoutes = new Map();
    let holdNextAuthorSearch = false;
    let heldAuthorRequestResolver = null;
    let holdNextBookSearch = false;
    let heldBookRequestResolver = null;
    let failNextStopRequest = false;
    const expectedCommandErrors = [];
    const observedExpectedHttpErrors = [];
    page.on('pageerror', (error) => {
      errors.push({ type: 'pageerror', message: error.message, stack: error.stack });
      if (process.env.CHAPTARR_PROVIDER_CAPTURE_MANIFEST) {
        console.error(error.stack);
      }
    });
    page.on('console', (message) => {
      if (process.env.CHAPTARR_PROVIDER_CAPTURE_MANIFEST && message.type() === 'error') {
        console.error(message.text());
      }
      if (message.type() === 'error') {
        const expectedStopError = 'Failed to load resource: the server responded with a status of 503 (Service Unavailable)';
        if (pendingExpectedStopResourceErrorCount > 0 && message.text() === expectedStopError) {
          pendingExpectedStopResourceErrorCount--;
          expectedStopResourceErrors.push(message.text());
          return;
        }

        const expectedIndex = expectedCommandErrors.findIndex((error) =>
          message.location().url === error.url && message.text().includes(`status of ${error.status}`));
        if (expectedIndex !== -1) {
          observedExpectedHttpErrors.push({ ...expectedCommandErrors.splice(expectedIndex, 1)[0], message: message.text() });
          return;
        }
        errors.push({ type: 'console', message: message.text(), url: message.location().url });
      }
    });
    page.on('response', (response) => {
      if (process.env.CHAPTARR_PROVIDER_CAPTURE_MANIFEST && response.status() >= 400) {
        console.error(`HTTP ${response.status()} ${response.url()}`);
      }
    });
    page.on('requestfailed', (request) => requestFailures.push({ url: request.url(), failure: request.failure()?.errorText }));
    page.on('request', (request) => {
      const url = new URL(request.url());
      const searchId = url.searchParams.get('searchId');
      if (url.pathname === '/api/v1/release' && (request.method() === 'DELETE' || searchId)) {
        authorLifecycleEvents.push({ event: 'request', method: request.method(), searchId });
      }
    });
    page.on('requestfailed', (request) => {
      const url = new URL(request.url());
      const searchId = url.searchParams.get('searchId');
      if (url.pathname === '/api/v1/release' && searchId) {
        authorLifecycleEvents.push({ event: 'failed', method: request.method(), searchId });
      }
    });
    await page.context().route('**/*', async(route) => {
      const request = route.request();
      const url = new URL(request.url());
      const pathname = url.pathname;
      if (heldAdmissionCommand && pathname === `/api/v1/command/${heldAdmissionCommand.id}`) {
        return route.fulfill({ json: heldAdmissionCommand });
      }
      if (process.env.CHAPTARR_PROVIDER_CAPTURE_MANIFEST && request.resourceType() === 'image') {
        return route.fulfill({ contentType: 'image/svg+xml', body: '<svg xmlns="http://www.w3.org/2000/svg" width="1" height="1"/>' });
      }
      if (pathname === '/api/v1/book' && request.method() === 'POST') {
        bookRequests.push({ method: request.method(), pathname, body: request.postDataJSON() });
        saveProviderWire();
        return route.fulfill({ status: 202, json: { pendingId: 1, message: 'Book queued for import' } });
      }
      if (pathname === '/api/v1/series/lookup') {
        return route.fulfill({ json: { foreignSeriesId: 'hc:8300', books: [{ foreignBookId: 'hc:8100', foreignAuthorId: 'hc:8000', authorName: 'Selected Search Writer', title: 'Selected Search Book' }] } });
      }
      if (pathname === '/api/v1/series/add') {
        seriesRequests.push({ method: request.method(), pathname, body: request.postDataJSON() });
        saveProviderWire();
        return route.fulfill({ json: { success: true, addedAuthors: [], monitoredBooks: [], pendingAuthorImportIds: [1] } });
      }
      if (pathname === '/api/v1/author/import') {
        importRequests.push({ method: request.method(), pathname, body: request.postDataJSON() });
        saveProviderWire();
        return route.fulfill({ status: 202, json: { pendingId: 1, message: 'Author queued for import' } });
      }
      if (pathname.toLowerCase() === '/api/v1/rootfolder') {
        const roots = await page.evaluate(() => window.addAuthorFixture.roots);
        return route.fulfill({ json: roots });
      }
      if (pathname === '/api/v1/qualityprofile' || pathname === '/api/v1/metadataprofile') {
        const profiles = await page.evaluate((kind) => window.addAuthorFixture[kind], pathname.endsWith('/qualityprofile') ? 'qualityProfiles' : 'metadataProfiles');
        const mediaType = url.searchParams.get('mediaType');
        return route.fulfill({ json: profiles.filter((profile) => !mediaType || profile.profileType === (mediaType === 'audiobook' ? 1 : 2)) });
      }
      if (pathname === '/api/v1/release') {
        const params = Object.fromEntries(url.searchParams);
        if (request.method() === 'DELETE') {
          stopRequests.push(params);
          if (failNextStopRequest) {
            failNextStopRequest = false;
            stopResponseStatuses.push(503);
            pendingExpectedStopResourceErrorCount++;
            return route.fulfill({ status: 503, json: { message: 'Temporary stop failure' } });
          }
          stopResponseStatuses.push(200);
          return route.fulfill({ json: { isStopping: true } });
        }
        if (request.method() === 'POST') {
          const payload = request.postDataJSON();
          grabRequests.push(payload);
          return route.fulfill({ json: {} });
        }
        if (params.existing === 'true') {
          const result = completedSearches.get(`${params.authorId}:${params.mediaType}`);
          return result ? route.fulfill({ json: result }) : route.fulfill({ status: 204, body: '' });
        }
        if (params.authorId) {
          completedSearches.delete(`${params.authorId}:${params.mediaType}`);
          authorRequests.push(params);
          if (holdNextAuthorSearch) {
            holdNextAuthorSearch = false;
            activeAuthorSearches.add(params.searchId);
            if (holdNextAdmission) {
              holdNextAdmission = false;
              heldAdmissionRoute = route;
            } else {
              await route.fulfill({ json: {
                searchId: params.searchId, authorId: Number(params.authorId), mediaType: params.mediaType,
                bookResults: [], isComplete: false, isCancelled: false
              } });
            }
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
        if (holdNextBookSearch) {
          holdNextBookSearch = false;
          heldBookRoutes.set(params.bookId, route);
          if (heldBookRequestResolver) {
            heldBookRequestResolver(params);
            heldBookRequestResolver = null;
          }
          return;
        }
        return route.fulfill({ json: {
          releases: [],
          hiddenReleases: [{ id: 1, title: 'Hidden release' }],
          filterSummary: { totalResults: 1 }
        } });
      }
      if (pathname.endsWith('/localization')) {
        return route.fulfill({ json: { Strings: strings } });
      }
      if (pathname === '/Content/Images/author-placeholder-muted.png') {
        return route.fulfill({ path: path.join(root, 'frontend/src/Content/Images/author-placeholder-muted.png'), contentType: 'image/png' });
      }
      if (pathname === '/Content/Images/chaptarr-logo.svg') {
        return route.fulfill({ path: path.join(root, 'frontend/src/Content/Images/chaptarr-logo.svg'), contentType: 'image/svg+xml' });
      }
      if (pathname === '/fixture.js') {
        return route.fulfill({ path: path.join(output, 'fixture.js'), contentType: 'text/javascript' });
      }
      if (pathname === '/style.css') {
        return route.fulfill({ path: path.join(output, 'Content', css), contentType: 'text/css' });
      }
      if (pathname !== '/') {
        if (process.env.CHAPTARR_SYNC_ONLY) {
          console.error(`Unmatched sync fixture request: ${request.method()} ${url.pathname}${url.search}`);
        }
        return route.fulfill({ status: 404, body: '' });
      }
      return route.fulfill({ contentType: 'text/html', body: '<!doctype html><link rel="stylesheet" href="/style.css"><div id="root"></div><div id="portal-root"></div><script>window.Chaptarr={isProduction:true,urlBase:"",apiRoot:"/api/v1",apiKey:"fixture"};</script><script src="/fixture.js"></script>' });
    });
    await page.goto(`http://127.0.0.1:${server.address().port}/`);
    if (process.env.CHAPTARR_COMPLETION_ONLY) {
      await checkCompletion(page, strings, makeRelease, expectedCommandErrors);
      assert.deepEqual(expectedCommandErrors, []);
      assert.deepEqual(errors, []);
      return;
    }
    if (process.env.CHAPTARR_LIFECYCLE_ONLY) {
      await checkCompletion(page, strings, makeRelease, expectedCommandErrors);
      await checkRetention(page, strings, makeRelease);
      await checkReconnect(page, strings, makeRelease, expectedCommandErrors, unloadStops);
      await checkCommandMessages(page, expectedCommandErrors);
      assert.deepEqual(expectedCommandErrors, []);
      assert.deepEqual(errors, []);
      return;
    }
    if (process.env.CHAPTARR_RETENTION_ONLY) {
      await checkRetention(page, strings, makeRelease);
      assert.deepEqual(errors, []);
      return;
    }
    if (process.env.CHAPTARR_RECONNECT_ONLY) {
      await checkReconnect(page, strings, makeRelease, expectedCommandErrors, unloadStops);
      assert.deepEqual(expectedCommandErrors, []);
      assert.deepEqual(errors, []);
      return;
    }
    if (process.env.CHAPTARR_MESSAGES_ONLY) {
      await checkCommandMessages(page, expectedCommandErrors);
      assert.deepEqual(expectedCommandErrors, [], 'The command refresh failure was observed');
      assert.deepEqual(errors, []);
      return;
    }
    if (process.env.CHAPTARR_SYNC_ONLY) {
      await checkAuthorSync(page, {
        importRequests, checkAddAuthorPayloads, selectAddAuthorMonitor, setAddAuthorCheckbox, strings,
        beforeInjectedFailure: () => assert.deepEqual(errors, [], 'Normal form interactions have zero browser console or page errors'),
        expectHttpError: (url, status) => expectedCommandErrors.push({ url, status })
      });
      assert.deepEqual(expectedCommandErrors, [], 'The exact injected HTTP failure was observed');
      assert.deepEqual(errors, [], 'No unexpected browser console or page errors');
      if (process.env.CHAPTARR_LAYOUT_ARTIFACTS) {
        fs.writeFileSync(path.join(process.env.CHAPTARR_LAYOUT_ARTIFACTS, 'browser-errors.json'), JSON.stringify({
          unexpectedErrors: errors, expectedInjectedHttpErrors: observedExpectedHttpErrors, requestFailures
        }, null, 2));
      }
      console.log('Author sync browser checks passed.');
      return;
    }
    if (process.env.CHAPTARR_PROVIDER_CAPTURE_MANIFEST) {
      await checkCapturedProviderPayloads(page, importRequests, bookRequests, seriesRequests);
      assert.deepEqual(errors, []);
      console.log('Captured provider selected Author/Book modal and supplied Series-name payload checks passed.');
      return;
    }
    const checkTableFixture = () => checkTable(page, {
      makeRelease,
      artifacts: process.env.CHAPTARR_LAYOUT_ARTIFACTS,
      captureOnly: process.env.CHAPTARR_TABLE_BEFORE === '1',
      requestCount: () => authorRequests.length + releaseRequests.length,
      beginSearch: async(mediaType, bookResults) => {
        await page.evaluate((side) => window.renderAuthorSearchFixture('grouped-tabs', 3, 3, { selectedMediaType: side }), mediaType);
        await page.getByRole('tab', { name: 'Search tab', exact: true }).click();
        holdNextAuthorSearch = true;
        await page.getByRole('button', { name: 'Search', exact: true }).click();
        await page.waitForFunction(() => window.readAuthorSearchState().isAccepted === true);
        const { searchId } = await page.evaluate(() => window.readAuthorSearchState());
        const updateSearch = async(results, isComplete = false) => {
          await sendAuthorResults({
            searchId, authorId: 10, mediaType, bookResults: results, isComplete, isCancelled: false
          });
          if (isComplete) {
            activeAuthorSearches.delete(searchId);
          }
        };
        await updateSearch(bookResults);
        return updateSearch;
      }
    });
    if (process.env.CHAPTARR_TABLE_ONLY) {
      await checkTableFixture();
      assert.deepEqual(errors, []);
      console.log('Author-search table browser check passed.');
      return;
    }

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

    authorRequests.length = 0;
    await page.evaluate(() => window.renderAuthorSearchFixture('grouped-tabs', 6, 0, { selectedMediaType: 'audiobook' }));
    await page.getByRole('tab', { name: 'Search tab', exact: true }).click();
    assert.equal(await page.getByRole('button', { name: 'Search', exact: true }).count(), 1);
    let resolvePendingRosterRequest = null;
    const pendingRosterRequest = new Promise((resolve) => {
      resolvePendingRosterRequest = resolve;
    });
    holdNextAuthorSearch = true;
    heldAuthorRequestResolver = resolvePendingRosterRequest;
    await page.getByRole('button', { name: 'Search', exact: true }).click();
    await page.getByText('This will perform 6 book searches', { exact: false }).waitFor();
    assert.equal(authorRequests.length, 0, 'The six-book audiobook search must wait for confirmation');
    await page.getByRole('button', { name: 'Search', exact: true }).last().click();
    const pendingRosterParams = await pendingRosterRequest;
    const pendingRosterSearchId = pendingRosterParams.searchId;
    assert.equal(pendingRosterParams.mediaType, 'audiobook', 'The confirmed roster must use the selected media side');

    const pendingTitles = ['Analgesia', 'Slipstream', 'Forgive & Forget', 'The Summoner', 'Rain Whore', 'Pervert'];
    const pendingRoster = pendingTitles.map((title, index) => makeBookResult(700 + index, title, 'pending', []));
    await sendAuthorResults({
      searchId: pendingRosterSearchId,
      authorId: 10,
      mediaType: 'audiobook',
      bookResults: pendingRoster,
      isComplete: false,
      isCancelled: false
    });
    let pendingSearchState = await page.evaluate(() => window.readAuthorSearchState());
    assert.deepEqual(pendingSearchState.bookResults.map((result) => result.title), pendingTitles,
      'The complete pending roster should be visible before any book result arrives');
    for (const title of pendingTitles) {
      const row = page.getByRole('button', { name: `Show other releases for ${title}`, exact: true }).locator('xpath=ancestor::tr[1]');
      assert.equal(await row.isVisible(), true, `${title} should be visible before the first result`);
      assert.match(await row.innerText(), /Waiting for results/, `${title} should be visible as waiting before the first result`);
    }

    const firstRosterBook = pendingRoster[0];
    const firstRosterRelease = makeRelease({
      guid: 'pending-roster-first-result', title: firstRosterBook.title, bookId: firstRosterBook.bookId, rank: 1, preferred: true
    });
    await sendAuthorResults({
      searchId: pendingRosterSearchId,
      authorId: 10,
      mediaType: 'audiobook',
      bookResults: [makeBookResult(firstRosterBook.bookId, firstRosterBook.title, 'complete', [firstRosterRelease])],
      isComplete: false,
      isCancelled: false
    });
    pendingSearchState = await page.evaluate(() => window.readAuthorSearchState());
    assert.equal(pendingSearchState.bookResults[0].status, 'complete', 'The first book should advance when its result arrives');
    assert.deepEqual(pendingSearchState.bookResults.slice(1).map((result) => result.status), ['pending', 'pending', 'pending', 'pending', 'pending'],
      'Completing the first book must preserve all other roster entries as pending');
    for (const title of pendingTitles.slice(1)) {
      const row = page.getByRole('button', { name: `Show other releases for ${title}`, exact: true }).locator('xpath=ancestor::tr[1]');
      assert.equal(await row.isVisible(), true, `${title} should remain visible after the first result`);
      assert.match(await row.innerText(), /Waiting for results/, `${title} should remain visibly waiting after the first result`);
    }

    await sendAuthorResults({
      searchId: pendingRosterSearchId,
      authorId: 10,
      mediaType: 'audiobook',
      bookResults: [makeBookResult(firstRosterBook.bookId, firstRosterBook.title, 'complete', [firstRosterRelease]), ...pendingRoster.slice(1)],
      isComplete: true,
      isCancelled: false
    });
    activeAuthorSearches.delete(pendingRosterSearchId);
    await page.waitForFunction(() => window.readAuthorSearchState().isComplete === true);
    console.log('A confirmed six-book audiobook roster renders all pending rows before the first result and preserves the other five as waiting.');
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

    await sendAuthorResults({
      searchId, authorId: 10, mediaType: 'audiobook', bookResults: [bookA], isComplete: false, isCancelled: false
    });
    let searchState = await page.evaluate(() => window.readAuthorSearchState());
    assert.equal(searchState.isFetching, true);
    assert.deepEqual(searchState.bookResults.map((result) => result.bookId), [101], 'The first SignalR delivery should render before the HTTP final response');

    await sendAuthorResults({
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
    await sendAuthorResults(wrongAuthorEvent);
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
    assert.equal(activeAuthorSearches.has(searchId), true);
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
    await sendAuthorResults(finalAuthorResult);
    activeAuthorSearches.delete(searchId);
    await page.waitForFunction(() => window.readAuthorSearchState().isComplete === true);
    searchState = await page.evaluate(() => window.readAuthorSearchState());
    assert.equal(searchState.isFetching, false);
    assert.equal(searchState.bookResults[0].releases.find((release) => release.guid === 'shared-guid').isGrabbed, true, 'The final streamed merge must preserve the prior grab state');
    assert.equal(searchState.bookResults[1].releases.find((release) => release.guid === 'shared-guid').isGrabbed, false);
    assert.equal(searchState.bookResults[0].releases.find((release) => release.guid === 'other-alpha').isGrabbed, true, 'The final streamed merge must preserve expanded scoped grab state');
    await page.getByText('Search incomplete', { exact: true }).first().waitFor();
    console.log('Final streamed merge preserved grab state and marks terminal pending books incomplete.');

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
    await sendAuthorResults({
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
    stopRequests.length = 0;
    await page.evaluate(() => window.renderAuthorSearchFixture('grouped-tabs', 3, 3, { selectedMediaType: 'audiobook' }));
    await page.getByRole('tab', { name: 'Search tab', exact: true }).click();
    let resolveCancelledAuthorRequest = null;
    const cancelledAuthorRequest = new Promise((resolve) => {
      resolveCancelledAuthorRequest = resolve;
    });
    holdNextAuthorSearch = true;
    heldAuthorRequestResolver = resolveCancelledAuthorRequest;
    await page.getByRole('button', { name: 'Search', exact: true }).click();
    const cancelledRequestParams = await cancelledAuthorRequest;
    const cancelledSearchId = cancelledRequestParams.searchId;

    await page.getByRole('button', { name: 'Stop search', exact: true }).waitFor();
    assert.equal((await page.evaluate(() => window.readAuthorSearchState())).bookResults.length, 0,
      'An accepted queued search must be stoppable before the book roster arrives');

    const keptRelease = makeRelease({
      guid: 'cancel-kept-guid', title: 'Completed Before Stop', bookId: 201, rank: 1, preferred: true
    });
    const cancelledRoster = [
      makeBookResult(201, 'Completed Before Stop', 'pending', []),
      makeBookResult(202, 'Pending At Stop', 'pending', []),
      makeBookResult(203, 'Not Started', 'pending', [])
    ];
    await sendAuthorResults({
      searchId: cancelledSearchId,
      authorId: 10,
      mediaType: 'audiobook',
      bookResults: cancelledRoster,
      isComplete: false,
      isCancelled: false
    });
    await sendAuthorResults({
      searchId: cancelledSearchId,
      authorId: 10,
      mediaType: 'audiobook',
      bookResults: [makeBookResult(201, 'Completed Before Stop', 'complete', [keptRelease])],
      isComplete: false,
      isCancelled: false
    });
    await page.getByText('Searching book releases...', { exact: true }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Stop search', exact: true }).isVisible(), true);
    await page.setViewportSize({ width: 1280, height: 720 });
    assert.equal(await page.getByText('Search stopped before all books completed.', { exact: true }).count(), 0,
      'A pending search must not display a terminal stopped state');
    await page.screenshot({ path: path.join(os.tmpdir(), 'chaptarr-author-search-active-desktop.png'), fullPage: true });
    await page.setViewportSize({ width: 390, height: 844 });
    await page.screenshot({ path: path.join(os.tmpdir(), 'chaptarr-author-search-active-mobile.png'), fullPage: true });
    await page.setViewportSize({ width: 1280, height: 720 });

    const keptResultRow = page.getByRole('row').filter({ hasText: 'Completed Before Stop' }).first();
    await keptResultRow.locator('[title="Add to downloaded queue"]').click();
    await page.waitForFunction(() => window.readAuthorSearchState().bookResults
      .find((result) => result.bookId === 201)?.releases
      .some((release) => release.guid === 'cancel-kept-guid' && release.isGrabbed));

    for (const title of ['Pending At Stop', 'Not Started']) {
      const row = page.getByRole('row').filter({ hasText: title }).first();
      assert.match(await row.innerText(), /Waiting for results/, `${title} should show its waiting status before cancellation`);
    }

    let resolveHeldBookRequest = null;
    const heldBookRequest = new Promise((resolve) => {
      resolveHeldBookRequest = resolve;
    });
    holdNextBookSearch = true;
    heldBookRequestResolver = resolveHeldBookRequest;
    await page.getByRole('button', { name: 'Open book modal', exact: true }).click();
    const heldBookParams = await heldBookRequest;
    assert.equal(heldBookParams.bookId, '900');
    assert.equal(await page.getByRole('dialog', { name: 'Book search modal' }).count(), 1);

    failNextStopRequest = true;
    await page.getByRole('button', { name: 'Stop search', exact: true }).click();
    await page.getByText('Could not stop the search. Try again.', { exact: true }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Stop search', exact: true }).isDisabled(), false,
      'A failed stop request should leave a retryable control');
    assert.deepEqual(stopRequests, [{ searchId: cancelledSearchId }]);
    assert.deepEqual(stopResponseStatuses, [503]);

    await page.getByRole('button', { name: 'Stop search', exact: true }).click();
    await page.getByText('Stopping after the current book', { exact: true }).waitFor();
    const stoppingButton = page.getByRole('button', { name: 'Stop search', exact: true });
    assert.equal(await stoppingButton.isVisible(), true, 'Keep Stop discoverable while the accepted request is pending');
    assert.equal(await stoppingButton.isDisabled(), true);
    assert.equal(await page.getByText('Search stopped before all books completed.', { exact: true }).count(), 0,
      'The DELETE acknowledgement is not terminal cancellation');
    searchState = await page.evaluate(() => window.readAuthorSearchState());
    assert.equal(searchState.isFetching, true);
    assert.equal(searchState.isCancelled, false);
    assert.equal(searchState.bookResults[0].releases[0].isGrabbed, true);
    assert.deepEqual(stopRequests, [{ searchId: cancelledSearchId }, { searchId: cancelledSearchId }]);
    assert.deepEqual(stopResponseStatuses, [503, 200]);
    assert.equal(expectedStopResourceErrors.length, 1, 'Only the deliberately failed Stop response may log an HTTP console error');
    assert.equal(pendingExpectedStopResourceErrorCount, 0);
    assert.equal(heldBookRoutes.has('900'), true, 'Stopping an author search must leave the book-modal request open');
    assert.equal(requestFailures.some((failure) => new URL(failure.url).searchParams.get('bookId') === '900'), false,
      'Stopping an author search must not abort the book-modal request');

    await page.screenshot({ path: path.join(os.tmpdir(), 'chaptarr-author-search-stopping-desktop.png'), fullPage: true });
    await page.setViewportSize({ width: 390, height: 844 });
    await page.screenshot({ path: path.join(os.tmpdir(), 'chaptarr-author-search-stopping-mobile.png'), fullPage: true });
    await page.setViewportSize({ width: 1280, height: 720 });

    const finalStoppedResult = {
      searchId: cancelledSearchId,
      authorId: 10,
      mediaType: 'audiobook',
      bookResults: [
        makeBookResult(201, 'Completed Before Stop', 'complete', [keptRelease]),
        makeBookResult(202, 'Pending At Stop', 'pending', []),
        makeBookResult(203, 'Not Started', 'pending', [])
      ],
      isComplete: true,
      isCancelled: true
    };
    await sendAuthorResults(finalStoppedResult);
    activeAuthorSearches.delete(cancelledSearchId);
    await page.waitForFunction(() => {
      const state = window.readAuthorSearchState();
      return state.isComplete && state.isCancelled && !state.isFetching;
    });
    searchState = await page.evaluate(() => window.readAuthorSearchState());
    assert.equal(searchState.bookResults[0].releases[0].isGrabbed, true, 'A grabbed result must survive the final cancellation response');
    assert.equal(searchState.bookResults[1].status, 'pending', 'Cancellation may leave unfinished groups pending in state');
    for (const title of ['Pending At Stop', 'Not Started']) {
      const row = page.getByRole('row').filter({ hasText: title }).first();
      assert.match(await row.innerText(), /Search stopped before all books completed\./, `${title} should show the stopped status`);
    }
    assert.equal(await page.getByRole('button', { name: 'Stop search', exact: true }).count(), 0,
      'The Stop control should disappear after terminal cancellation');

    const abortedBookRequest = page.waitForEvent('requestfailed', (request) =>
      new URL(request.url()).searchParams.get('bookId') === '900');
    await page.getByRole('button', { name: 'Close book modal', exact: true }).click();
    await abortedBookRequest;
    assert.equal(await page.locator('.followingBalls').count(), 0, 'Terminal cancellation should leave no author loader');
    assert.equal(await page.getByText('Search stopped before all books completed.', { exact: true }).count(), 3,
      'The alert and both unfinished book rows should show the stopped status');
    assert.equal(requestFailures.some((failure) => failure.url.includes(`searchId=${cancelledSearchId}`)), false,
      'The admission request must finish independently of the streamed terminal result');
    assert.deepEqual(errors, []);

    stopRequests.length = 0;
    await page.evaluate(() => window.renderAuthorSearchFixture('grouped-tabs', 3, 3, { selectedMediaType: 'audiobook' }));
    await page.getByRole('tab', { name: 'Search tab', exact: true }).click();
    let resolveUnmountAuthorRequest = null;
    const unmountAuthorRequest = new Promise((resolve) => {
      resolveUnmountAuthorRequest = resolve;
    });
    holdNextAuthorSearch = true;
    holdNextAdmission = true;
    heldAuthorRequestResolver = resolveUnmountAuthorRequest;
    await page.getByRole('button', { name: 'Search', exact: true }).click();
    const unmountRequestParams = await unmountAuthorRequest;
    const unmountSearchId = unmountRequestParams.searchId;
    assert.equal(await page.getByRole('button', { name: 'Stop search', exact: true }).count(), 0,
      'Stop is unavailable before admission or roster registration');
    await page.evaluate(() => window.renderAuthorSearchFixture('filter-warning', 0, 0));
    heldAdmissionCommand = {
      id: 999, name: 'InteractiveAuthorSearch', status: 'started',
      body: { searchId: unmountSearchId, authorId: 10, mediaType: 'audiobook', sendUpdatesToClient: true }
    };
    await heldAdmissionRoute.fulfill({ json: {
      commandId: 999, searchId: unmountSearchId, authorId: 10, mediaType: 'audiobook', bookResults: [], isComplete: false
    } });
    await page.waitForTimeout(100);
    assert.deepEqual(stopRequests, [], 'Leaving before admission must not stop the background search');
    activeAuthorSearches.delete(unmountSearchId);

    await page.evaluate(() => window.renderAuthorSearchFixture('grouped-tabs', 3, 3, { selectedMediaType: 'audiobook' }));
    await page.getByRole('tab', { name: 'Search tab', exact: true }).click();
    holdNextAuthorSearch = true;
    await page.getByRole('button', { name: 'Search', exact: true }).click();
    await page.waitForFunction(() => window.readAuthorSearchState().isAccepted === true);
    const failedSearchId = (await page.evaluate(() => window.readAuthorSearchState())).searchId;
    await sendAuthorResults({
      searchId: failedSearchId, authorId: 10, mediaType: 'audiobook', bookResults: [], isComplete: true, isFailed: true
    });
    await page.getByText(strings.AuthorSearchIncomplete, { exact: true }).waitFor();
    assert.equal(await page.getByText(strings.AuthorSearchNoBooksReturned, { exact: true }).count(), 0,
      'A failed background search must not also claim there were no books');
    assert.equal((await page.evaluate(() => window.readAuthorSearchState())).isFetching, false);
    activeAuthorSearches.delete(failedSearchId);

    await checkCompletion(page, strings, makeRelease, expectedCommandErrors);
    await checkRetention(page, strings, makeRelease);
    await checkReconnect(page, strings, makeRelease, expectedCommandErrors, unloadStops);
    await checkTableFixture();
    assert.deepEqual(expectedCommandErrors, [], 'Only the explicitly injected command lookup errors are expected');
    await checkAddAuthorPayloads(page, importRequests);
    await checkBookModalMetadata(page);
    await checkAddSeriesPayloads(page, seriesRequests);
    await checkModalReopenAndZeroFallback(page, seriesRequests, bookRequests);
    await checkCapturedProviderPayloads(page, importRequests, bookRequests, seriesRequests);
    await page.reload();
    await checkCommandMessages(page, expectedCommandErrors);
    assert.deepEqual(expectedCommandErrors, [], 'The command refresh failure was observed');
    assert.deepEqual(errors, []);

    console.log('Author-search browser check passed: confirmation and media boundaries, scoped grabs, incremental/final merges, explicit Stop acceptance, retry, modal isolation, terminal cancellation, and unmount cleanup.');
  } finally {
    await browser.close();
    await new Promise((resolve) => server.close(resolve));
    fs.rmSync(output, { recursive: true, force: true });
  }
})().catch((error) => {
  fs.rmSync(output, { recursive: true, force: true });
  console.error(error);
  process.exitCode = 1;
});
