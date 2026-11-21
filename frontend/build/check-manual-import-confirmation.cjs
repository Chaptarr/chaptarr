/* Exercise the real manual-import modal, replacement lookup, and command POST in Chromium. */
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const webpack = require('webpack');
const { chromium } = require('playwright');

const output = fs.mkdtempSync(path.join(os.tmpdir(), 'chaptarr-manual-import-'));
const artifactRoot = process.env.CHAPTARR_PR194_ARTIFACT_DIR || path.join(
  os.tmpdir(),
  'chaptarr-pr194-browser-evidence',
  `${Date.now()}-${process.pid}`
);
fs.mkdirSync(artifactRoot, { recursive: true });

const config = require('./webpack.config')({ production: true });

config.entry = { fixture: path.join(__dirname, 'manual-import-confirmation-fixture.js') };
config.output = { ...config.output, path: output, filename: 'fixture.js', publicPath: '/' };
config.optimization = { ...config.optimization, minimize: false, splitChunks: false, runtimeChunk: false };
config.plugins = config.plugins.filter((plugin) => ['DefinePlugin', 'MiniCssExtractPlugin'].includes(plugin.constructor.name));
for (const rule of config.module.rules) {
  if (Array.isArray(rule.use)) {
    rule.use = rule.use.filter((loader) => !String(typeof loader === 'string' ? loader : loader.loader).includes('css-modules-typescript-loader'));
  }
}

function compileFixture() {
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

const strings = JSON.parse(fs.readFileSync(path.resolve(__dirname, '../../src/NzbDrone.Core/Localization/Core/en.json'), 'utf8'));
const quality = {
  quality: { id: 1, name: 'Any' },
  revision: { version: 1, real: 0, isRepack: false }
};

function metadataItem(id = 1) {
  return {
    id,
    path: `/downloads/suggested-${id}.epub`,
    size: 1024,
    author: null,
    book: null,
    editionId: null,
    suggestedForeignAuthorId: 'hc:author-1',
    suggestedAuthorName: 'Suggested Author',
    suggestedForeignBookId: 'hc:book-1',
    suggestedBookTitle: 'Suggested Book',
    suggestedForeignEditionId: 'hc:edition-1',
    suggestedEditionTitle: 'Suggested Book',
    quality,
    releaseGroup: 'Selected Audit Group',
    indexerFlags: 0,
    customFormats: [],
    rejections: [],
    warnings: [],
    tags: {},
    additionalFile: false
  };
}

function localItem() {
  return {
    id: 10,
    path: '/downloads/local.epub',
    size: 2048,
    author: { id: 3, authorName: 'Local Author', sortName: 'local author' },
    book: { id: 20, title: 'Local Book', disambiguation: null },
    editionId: 30,
    edition: { id: 30, title: 'Local Book' },
    quality,
    releaseGroup: 'Selected Audit Group',
    indexerFlags: 0,
    customFormats: [],
    rejections: [],
    warnings: [],
    tags: {},
    additionalFile: false
  };
}

function requestSummary(captured) {
  return JSON.stringify({
    bookFileUrls: captured.bookFiles.map((request) => request.url),
    commandCount: captured.commands.length,
    commandBodies: captured.commands.map((request) => request.body)
  });
}

async function openFixture(browser, items, { width = 1440, theme = 'dark', existingFiles = true, replaceExisting = true, failPreview = false, folderMode = false, replacementDetails = {}, waiting = false } = {}) {
  const page = await browser.newPage({ viewport: { width, height: 900 }, colorScheme: theme });
  const captured = { manualImports: [], bookFiles: [], commands: [] };
  const errors = [];
  let resolveCommand = null;
  const commandReceived = new Promise((resolve) => {
    resolveCommand = resolve;
  });
  const cssName = fs.readdirSync(path.join(output, 'Content')).find((name) => name.endsWith('.css'));

  page.on('pageerror', (error) => errors.push(error.message));
  await page.route('**/*', async(route) => {
    const request = route.request();
    const url = new URL(request.url());
    const pathname = url.pathname;

    if (pathname.endsWith('/localization')) {
      return route.fulfill({ json: { Strings: strings } });
    }

    if (pathname.endsWith('/manualimport')) {
      captured.manualImports.push(request.url());
      return route.fulfill({ json: items });
    }

    if (pathname.endsWith('/manualimport/replacementfiles')) {
      assert.equal(request.method(), 'POST');
      const selected = request.postDataJSON().files;
      const bookIds = [...new Set(selected.map((file) => String(file.bookId)).filter((id) => id !== '0'))];
      captured.bookFiles.push({ url: request.url(), bookIds });
      if (failPreview) {
        return route.fulfill({ status: 500, json: { message: 'Preview failed' } });
      }
      if (bookIds.length === 0) {
        return route.fulfill({ json: [] });
      }
      if (bookIds.length === 1 && bookIds[0] === '20') {
        return route.fulfill({ json: existingFiles ? [{ id: 500, bookId: 20, path: '/library/Local Book.epub', associatedPaths: ['/library/Other Narration/Local Book.epub', '/library/cover.jpg'], size: 2048,
          editionTitle: 'Local Book', mediaType: 'ebook', isPermanentDelete: true, ...replacementDetails }] : [] });
      }

      return route.fulfill({
        status: 400,
        contentType: 'application/json',
        body: JSON.stringify({ message: 'A bookId filter is required for the test response' })
      });
    }

    if (pathname.endsWith('/command') && request.method() === 'POST') {
      captured.commands.push({ url: request.url(), body: request.postDataJSON() });
      assert.equal(request.postDataJSON().name, 'ManualImport');
      resolveCommand();
      return route.fulfill({ json: { id: 'manual-import-command', name: 'ManualImport', status: 'queued' } });
    }

    if (pathname === '/fixture.js') {
      return route.fulfill({ path: path.join(output, 'fixture.js'), contentType: 'text/javascript' });
    }

    if (pathname === '/style.css') {
      return route.fulfill({ path: path.join(output, 'Content', cssName), contentType: 'text/css' });
    }

    if (pathname === '/') {
      return route.fulfill({
        contentType: 'text/html',
        body: '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="/style.css"></head><body><div id="root"></div><div id="portal-root"></div><script>window.Chaptarr={urlBase:"",apiRoot:"/api/v1",isProduction:true,instanceName:"Chaptarr"};</script><script src="/fixture.js"></script></body></html>'
      });
    }

    const assetPath = path.resolve(output, `.${decodeURIComponent(pathname)}`);
    if (assetPath.startsWith(`${output}${path.sep}`) && fs.existsSync(assetPath) && fs.statSync(assetPath).isFile()) {
      return route.fulfill({ path: assetPath });
    }

    return route.fulfill({ status: 204, body: '' });
  });

  await page.goto('http://manual-import.test/');
  await page.waitForFunction(() => window.manualImportFixtureReady === true);
  await page.evaluate(({ themeName, folder, hold }) => window.renderManualImportFixture(themeName, folder, hold), { themeName: theme, folder: folderMode, hold: waiting });
  if (!folderMode) {
    await page.locator('table').getByRole('button').first().click();
  }
  await page.locator('input[name="1"], input[name="10"]').first().waitFor();
  await page.getByText('Ready now (local):', { exact: false }).waitFor();
  if (!folderMode && !waiting) {
    await page.getByText(strings.InteractiveImportCombineWithExistingMode, { exact: true }).waitFor();
  }
  if (replaceExisting && !waiting) {
    await page.getByText(strings.InteractiveImportCombineWithExistingMode, { exact: true }).click();
    await page.getByText(strings.InteractiveImportReplaceExisting, { exact: true }).click();
    await page.getByText(strings.InteractiveImportReplaceExistingMode, { exact: true }).waitFor();
  }
  return { page, captured, errors, commandReceived };
}

async function waitForCommand(commandReceived) {
  let timeout = null;
  try {
    await Promise.race([
      commandReceived,
      new Promise((resolve, reject) => {
        timeout = setTimeout(() => reject(new Error('ManualImport POST was not captured')), 5000);
      })
    ]);
  } finally {
    clearTimeout(timeout);
  }
}

function waitForRelevantResponse(page) {
  return page.waitForResponse((response) => {
    const pathname = new URL(response.url()).pathname;
    return pathname.endsWith('/manualimport/replacementfiles') || (pathname.endsWith('/command') && response.request().method() === 'POST');
  }, { timeout: 5000 });
}

async function assertAddLabelsFit(page) {
  const failures = await page.locator('[class*="NarratorSearchRow-addButton"]').evaluateAll((buttons) => buttons.flatMap((button) => {
    const label = button.querySelector('[class*="SpinnerButton-label"]');
    const bounds = button.getBoundingClientRect();
    return [...label.getClientRects()].some((rect) => rect.left < bounds.left || rect.right > bounds.right || rect.top < bounds.top || rect.bottom > bounds.bottom) ?
      [label.textContent] : [];
  }));
  assert.deepEqual(failures, [], 'Settled Add labels must fit inside every button');
}

async function saveScreenshot(page, label, width, theme) {
  const screenshotPath = path.join(artifactRoot, `${label}-${theme}-${width}.png`);
  await page.screenshot({ path: screenshotPath, fullPage: true, animations: 'disabled' });
  return screenshotPath;
}

async function assertEventuallyChecked(page, locator, expected) {
  for (let attempt = 0; attempt < 20; attempt++) {
    if (await locator.isChecked() === expected) {
      return;
    }
    await page.waitForTimeout(25);
  }
  assert.equal(await locator.isChecked(), expected);
}

async function verifyMetadataOnly(browser, width, theme) {
  const { page, captured, errors, commandReceived } = await openFixture(browser, [metadataItem()], { width, theme });
  try {
    const rowCheckbox = page.locator('input[name="1"]');
    await rowCheckbox.focus();
    await page.keyboard.press('Space');
    await assertEventuallyChecked(page, rowCheckbox, true);
    const screenshotPath = await saveScreenshot(page, 'metadata-only', width, theme);

    const importButton = page.getByRole('button', { name: strings.Import, exact: true });
    await importButton.focus();
    const response = waitForRelevantResponse(page);
    await page.keyboard.press('Enter');
    await response;

    await page.waitForFunction(() => window.manualImportCloseCount === 1);
    assert.equal(captured.bookFiles.length, 1, `Metadata-only replacement must confirm an empty local replacement set. ${requestSummary(captured)}`);
    await waitForCommand(commandReceived);
    assert.equal(captured.commands.length, 1, `Expected one ManualImport POST after keyboard activation. Captured requests: ${requestSummary(captured)}`);
    const body = captured.commands[0].body;
    assert.equal(body.name, 'ManualImport');
    assert.equal(body.importMode, 'auto');
    assert.equal(body.replaceExistingFiles, true);
    assert.deepEqual(body.confirmedReplacementFiles, []);
    assert.equal(body.files[0].selectionSource, 'userMetadataSuggestion');
    assert.equal(body.files.length, 1);
    assert.equal(body.files[0].bookId, 0);
    assert.equal(body.files[0].foreignAuthorId, 'hc:author-1');
    assert.equal(body.files[0].foreignBookId, 'hc:book-1');
    assert.equal(body.files[0].foreignEditionId, 'hc:edition-1');
    assert.equal(body.files[0].releaseGroup, 'Selected Audit Group');
    assert.deepEqual(body.files[0].quality, quality);
    assert.equal(body.files[0].indexerFlags, 0);
    await page.waitForFunction(() => window.manualImportCloseCount === 1);
    assert.deepEqual(errors, [], `Browser errors during metadata-only import. Requests: ${requestSummary(captured)}`);
    return screenshotPath;
  } finally {
    await page.close();
  }
}

async function verifyReplacementDefaultsOff(browser) {
  const { page, captured, errors, commandReceived } = await openFixture(browser, [localItem()], { replaceExisting: false });
  try {
    await page.getByRole('button', { name: strings.Import, exact: true }).click();
    await page.waitForFunction(() => window.manualImportCloseCount === 1);
    await waitForCommand(commandReceived);
    assert.equal(captured.commands.length, 1);
    assert.equal(captured.commands[0].body.replaceExistingFiles, false);
    assert.equal(captured.commands[0].body.files[0].bookId, 20);
    assert.equal(captured.bookFiles.length, 0);
    assert.deepEqual(errors, []);
  } finally {
    await page.close();
  }
}

async function verifyScopedConfirmation(browser) {
  const { page, captured, errors } = await openFixture(browser, [localItem()]);
  try {
    const rowCheckbox = page.locator('input[name="10"]');
    await assertEventuallyChecked(page, rowCheckbox, true);
    await page.getByRole('button', { name: strings.Import, exact: true }).click();
    await page.getByRole('button', { name: strings.Proceed, exact: true }).waitFor();
    await saveScreenshot(page, 'local-confirmation', 1440, 'dark');
    assert.deepEqual(captured.bookFiles.map((request) => request.bookIds), [['20']], `Replacement lookup must stay scoped to the selected local book. ${requestSummary(captured)}`);

    await page.getByRole('button', { name: strings.Cancel, exact: true }).last().click();
    await page.getByRole('button', { name: strings.Proceed, exact: true }).waitFor({ state: 'detached' });
    assert.equal(captured.commands.length, 0, 'Cancel must leave the import command unsubmitted');
    assert.equal(await page.evaluate(() => window.manualImportCloseCount), 0, 'Cancel closes only the confirmation and leaves import selection open');
    assert.deepEqual(errors, []);
  } finally {
    await page.close();
  }
}

async function verifyMixedProceed(browser) {
  const { page, captured, errors, commandReceived } = await openFixture(browser, [localItem(), metadataItem(11)]);
  try {
    const metadataCheckbox = page.locator('input[name="11"]');
    await metadataCheckbox.focus();
    await page.keyboard.press('Space');
    await assertEventuallyChecked(page, metadataCheckbox, true);
    await page.getByRole('button', { name: strings.Import, exact: true }).click();
    await page.getByRole('button', { name: strings.Proceed, exact: true }).waitFor();
    assert.deepEqual(captured.bookFiles.map((request) => request.bookIds), [['20']], `Mixed replacement lookup must include only the selected local book. ${requestSummary(captured)}`);
    await page.getByText('/library/Other Narration/Local Book.epub', { exact: true }).waitFor();
    await page.getByText('/library/cover.jpg', { exact: true }).waitFor();

    await page.getByRole('button', { name: strings.Proceed, exact: true }).click();
    await page.waitForFunction(() => window.manualImportCloseCount === 1);
    await waitForCommand(commandReceived);
    assert.equal(captured.commands.length, 1, `Proceed must send one ManualImport command. ${requestSummary(captured)}`);
    assert.equal(captured.commands[0].body.files.length, 2);
    assert.deepEqual(captured.commands[0].body.confirmedReplacementFiles, [{ id: 500, bookId: 20, path: '/library/Local Book.epub', associatedPaths: ['/library/Other Narration/Local Book.epub', '/library/cover.jpg'] }]);
    for (const file of captured.commands[0].body.files) {
      assert.equal(file.releaseGroup, 'Selected Audit Group');
      assert.deepEqual(file.quality, quality);
      assert.equal(file.indexerFlags, 0);
    }
    assert.equal(captured.commands[0].body.files.find((file) => file.path.includes('suggested-11'))?.foreignAuthorId, 'hc:author-1');
    assert.deepEqual(errors, [], `Browser errors during mixed confirmation. ${requestSummary(captured)}`);
  } finally {
    await page.close();
  }
}

async function verifyReplacementDetails(browser) {
  for (const permanent of [true, false]) {
    const { page, captured, errors } = await openFixture(browser, [{ ...localItem(), path: '/downloads/Dark Age.m4b' }], {
      width: permanent ? 320 : 1440,
      theme: permanent ? 'dark' : 'light',
      replacementDetails: { path: '/library/Dark Age - Full Cast/Dark Age.m4b', editionId: 31,
        editionTitle: 'Dark Age (Dramatized Adaptation)', narrator: 'Full Cast', quality: 'M4B', mediaType: 'audiobook',
        isGraphicAudio: true, isPermanentDelete: permanent }
    });
    try {
      await page.getByRole('button', { name: strings.Import, exact: true }).click();
      await page.getByText(`Dark Age (Dramatized Adaptation) — Full Cast — M4B — ${strings.RecordingTypeDramatized}`, { exact: true }).waitFor();
      await page.getByText(permanent ? strings.ConfirmImportPermanentDeletion : strings.ConfirmImportRecyclingBin, { exact: true }).waitFor();
      assert.equal(captured.commands.length, 0, 'Displaying the deletion details must not import anything');
      await saveScreenshot(page, `replacement-${permanent ? 'permanent' : 'recycled'}`, permanent ? 320 : 1440, permanent ? 'dark' : 'light');
      await page.getByRole('button', { name: strings.Cancel, exact: true }).last().click();
      assert.equal(captured.commands.length, 0, 'Cancelling must preserve both recordings');
      assert.deepEqual(errors, []);
    } finally {
      await page.close();
    }
  }
}

async function verifyUpgradeControls(browser) {
  for (const width of [320, 1440]) {
    const { page, captured, errors, commandReceived } = await openFixture(browser, [{ ...localItem(), path: '/downloads/Dark Age.m4b', isGraphicAudio: true }], {
      width, theme: width === 320 ? 'dark' : 'light', existingFiles: false, waiting: true
    });
    try {
      await page.getByText(strings.InteractiveImportReplaceExistingMode, { exact: true }).waitFor();
      assert.equal(captured.commands.length, 0);
      const recording = page.locator('select[name="audioProductionOverride"]').first();
      assert.equal(await recording.inputValue(), 'auto');
      await recording.selectOption('standard');
      await page.getByRole('button', { name: strings.Import, exact: true }).click();
      await page.waitForFunction(() => window.manualImportCloseCount === 1);
      await waitForCommand(commandReceived);
      assert.equal(captured.commands.length, 1);
      assert.equal(captured.commands[0].body.replaceExistingFiles, true);
      assert.equal(captured.commands[0].body.files[0].audioProductionOverride, false);

      for (const profileType of ['audiobook', 'ebook']) {
        await page.evaluate((type) => window.renderUpgradeProfileFixture(type), profileType);
        const action = page.getByText(strings.ReplaceAutomatically, { exact: true });
        await action.waitFor();
        assert.deepEqual(await page.evaluate(() => window.profileChanges), []);
        await action.click();
        await page.getByText(strings.WaitForReplacementConfirmation, { exact: true }).click();
        assert.deepEqual(await page.evaluate(() => window.profileChanges), [{ name: 'confirmAutomaticUpgrades', value: true }]);
        await saveScreenshot(page, `upgrade-${profileType}`, width, width === 320 ? 'dark' : 'light');
      }
      assert.deepEqual(errors, []);
    } finally {
      await page.close();
    }
  }
  console.log('Upgrade profile defaults, typed changes, queue Replace default and recording correction passed');
}

async function verifyEditionRecordingControls(browser) {
  for (const width of [320, 800, 1440]) {
    const { page, errors } = await openFixture(browser, [localItem()], { width, theme: width === 320 ? 'dark' : 'light' });
    try {
      for (const mediaType of ['audiobook', 'ebook']) {
        await page.evaluate((type) => window.renderFileRecordingFixture(type), mediaType);
        await page.getByRole('cell').filter({ hasText: `/books/recording.${mediaType === 'audiobook' ? 'm4b' : 'epub'}` }).waitFor();
        assert.equal(await page.locator('select[name="audioProductionOverride"]').count(), 0,
          'The Files tab has no individual or bulk recording override');
        assert.equal(await page.locator('select[name="quality"]').count(), 1);
      }
      for (const [before, after, changedEdition, expected] of [
        [false, true, false, 1], [true, false, false, 1], [true, true, true, 1], [true, true, false, 0]
      ]) {
        assert.deepEqual(await page.evaluate(([a, b, c]) => window.verifyEditionFileReload(a, b, c),
          [before, after, changedEdition]), { fetches: expected, clears: expected });
      }
      assert.equal(await page.evaluate(() => window.verifyAllowedHostsRestart()), true, 'Changing Allowed Hosts prompts for a restart');
      await page.evaluate(() => window.renderEditionPickerFixture());
      const picker = page.locator('select[name="editions"]');
      await picker.waitFor();
      assert.match(await picker.locator('option[value="solo"]').textContent(), /Narrator: Jim Dale/);
      assert.match(await picker.locator('option[value="cast"]').textContent(), /Alice Reader, \+2/);
      assert.doesNotMatch(await picker.locator('option[value="text"]').textContent(), /Should not be shown/);
      await picker.focus();
      await picker.selectOption('cast');
      const changes = await page.evaluate(() => window.editionPickerChanges);
      assert.equal(changes[0].value.find((edition) => edition.foreignEditionId === 'cast').monitored, true);
      assert.deepEqual(changes[1], { name: 'anyEditionOk', value: false });
      const wantedRequests = [];
      const genericBookAdds = [];
      let existingCopy = false;
      let monitored = true;
      let narratorNames = [];
      let returnedBookId = 21;
      let refusal = null;
      let heldRequest = null;
      await page.route('**/book', (route) => {
        genericBookAdds.push(route.request().method());
        return route.fulfill({ json: { id: 99 } });
      });
      await page.route('**/book/20/editions/wanted', async(route) => {
        if (heldRequest) {
          await heldRequest;
        }
        wantedRequests.push({ method: route.request().method(), body: route.request().postDataJSON() });
        if (refusal) {
          return route.fulfill(refusal);
        }
        return route.fulfill({ json: { id: returnedBookId, title: 'A Book', narratorNames, existingCopy, monitored } });
      });
      const cases = [false, true].flatMap((duplicate) => [0, 3].flatMap((bookFileCount) =>
        [false, true].flatMap((anyEditionOk) => [false, true].map((returnedMonitored) => ({ duplicate, bookFileCount, anyEditionOk, returnedMonitored })))));
      for (const { duplicate, bookFileCount, anyEditionOk, returnedMonitored } of cases) {
        existingCopy = duplicate;
        monitored = returnedMonitored;
        narratorNames = anyEditionOk && !bookFileCount ? [] : ['Existing Reader'];
        const outcome = await page.evaluate((input) => window.selectNarratorFixture(input), {
          bookFileCount, anyEditionOk
        });
        assert.deepEqual(outcome.result, { id: 21, title: 'A Book', narratorNames, existingCopy, monitored });
        assert.deepEqual(outcome.original, {
          id: 20, authorId: 3, anyEditionOk, statistics: { bookFileCount }
        });
        assert.equal(outcome.messages.length, existingCopy ? 1 : 0);
        const state = outcome.discovery[20];
        assert.deepEqual(state.filteredNarrators.map((edition) => edition.editionId), [32], 'A different edition with the same narrator stays available');
        assert.equal(state.totalExisting, 1);
        assert.equal(state.totalAvailable, 1);
        assert.equal(state.totalFiltered, 1);
        assert.equal(state.existingCopyNarrators.length, 1);
        assert.equal(state.existingCopyNarrators[0].editionId, 30);
        assert.equal(state.existingCopyNarrators[0].existingCopyId, 21);
        assert.equal(state.existingCopyNarrators[0].existingCopyMonitored, monitored, 'Use the returned book monitoring without changing the edition monitoring');
        assert.equal(state.existingCopyNarrators[0].monitored, false);
        assert.equal(state.existingCopyNarrators[0].status, 'existing');
        if (existingCopy) {
          assert.equal(outcome.messages[0].message, `This edition is already in your library: A Book, narrated by Selected Reader, +2. ${monitored ? '' : 'This book isn\'t monitored. '}Nothing was changed or searched.`);
          assert.equal(outcome.messages[0].hideAfter, 10);
        }
      }
      assert.deepEqual(wantedRequests, Array.from({ length: 16 }, () => ({
        method: 'POST', body: { editionId: 30, searchForNewBook: true }
      })), 'The microphone requests a separate wanted copy for both automatic and pinned rows, without editing the original');
      const repeated = await page.evaluate(() => window.selectNarratorFixture({ bookFileCount: 0, anyEditionOk: true, repeat: true }));
      assert.equal(repeated.discovery[20].existingCopyNarrators.length, 1, 'Repeated success responses do not duplicate the row');
      assert.equal(repeated.discovery[20].totalExisting, 1);
      const closed = await page.evaluate(() => window.selectNarratorFixture({ bookFileCount: 0, anyEditionOk: true, closeBeforeResponse: true }));
      assert.deepEqual(closed.discovery, {}, 'A response after closing the picker does not recreate its cached list');
      const beforeInvalid = wantedRequests.length;
      const invalid = await page.evaluate(() => window.selectNarratorWithoutEditionFixture());
      assert.equal(invalid.additions['20:null'].isSaving, false);
      assert.equal(invalid.additions['20:null'].error.message, strings.SelectEdition);
      assert.equal(wantedRequests.length, beforeInvalid, 'No edition ID means no add request');
      assert.deepEqual(genericBookAdds, [], 'The name-only POST /book fallback is gone');
      await page.route('**/fixture-edition-cover.svg', (route) => route.fulfill({
        contentType: 'image/svg+xml',
        body: '<svg xmlns="http://www.w3.org/2000/svg" width="120" height="180" viewBox="0 0 120 180"><rect width="120" height="180" fill="#24466b"/><rect x="16" y="20" width="88" height="140" rx="4" fill="#f0d28a"/><path d="M30 65h60M30 85h60M30 105h40" stroke="#24466b" stroke-width="6"/></svg>'
      }));
      await page.route('**/edition?bookId=20', (route) => route.fulfill({ json: [
        { id: 30, title: 'Unmonitored copy', readingFormatId: 2, narratorNames: ['First Reader'], existingCopyId: 21, existingCopyMonitored: false,
          images: [{ coverType: 'cover', url: '/fixture-edition-cover.svg' }] },
        { id: 31, title: 'Monitored copy', readingFormatId: 2, narratorNames: ['Second Reader'], existingCopyId: 22, existingCopyMonitored: true },
        { id: 32, title: 'Different edition, same narrator', readingFormatId: 2, narratorNames: ['First Reader'], monitored: false },
        { id: 33, title: 'ExtraordinarilyLongUnbrokenEditionTitleForWrapping', readingFormatId: 2, narratorNames: ['Fourth Reader'] },
        { id: 34, title: 'Ebook with narrator credits', readingFormatId: 3, narratorNames: ['First Reader'] },
        { id: 35, title: 'Print with narrator credits', readingFormatId: 1, narratorNames: ['First Reader'] },
        { id: 36, title: 'Unknown format with narrator credits', readingFormatId: 0, narratorNames: ['First Reader'] }
      ] }));
      const discovery = await page.evaluate(() => window.renderNarratorDiscoveryFixture());
      assert.equal(wantedRequests.length, beforeInvalid, 'Opening the picker must not change monitoring or search');
      assert.deepEqual(discovery.existingCopyNarrators.map((edition) => edition.editionId), [30, 31]);
      assert.deepEqual(discovery.filteredNarrators.map((edition) => edition.editionId), [32, 33]);
      const existingRows = page.getByRole('row').filter({ hasText: strings.WantedEditionAlreadyHaveCopy });
      assert.equal(await existingRows.count(), 2);
      await existingRows.filter({ has: page.getByText('Unmonitored copy', { exact: true }) }).getByText(`(${strings.WantedEditionBookNotMonitored})`, { exact: false }).waitFor();
      assert.equal(await existingRows.filter({ has: page.getByText('Monitored copy', { exact: true }) }).getByText(`(${strings.WantedEditionBookNotMonitored})`, { exact: false }).count(), 0);
      assert.equal(await page.getByRole('row').filter({ hasText: 'with narrator credits' }).count(), 0, 'Non-audio editions never become selectable because they carry narrator credits');
      await page.addStyleTag({ content: '*, *::before, *::after { transition: none !important; animation: none !important; }' });
      const addButtons = page.getByRole('button', { name: strings.AddToLibrary, exact: true });
      assert.equal(await addButtons.count(), 2, 'Editions without an existing copy remain available, including one with the same narrator');
      await assertAddLabelsFit(page);
      const covers = page.getByRole('table').getByRole('img');
      await page.waitForFunction(() => {
        const cover = document.querySelector('img[src$="/fixture-edition-cover.svg"]');
        return cover?.complete && cover.naturalWidth > 0;
      });
      assert.equal(await covers.count(), 4, 'Every edition keeps its own cover visible, including on phones');
      for (const cover of await covers.all()) {
        assert.equal(await cover.isVisible(), true);
        const bounds = await cover.boundingBox();
        assert.equal(bounds.width, width === 320 ? 35 : 150);
        assert.equal(bounds.height, width === 320 ? 35 : 150);
      }
      if (width === 320) {
        for (const row of await page.getByRole('row').filter({ has: page.getByRole('img') }).all()) {
          const cover = await row.getByRole('img').boundingBox();
          const title = await row.locator('[class*="NarratorSearchRow-editionTitle"]').boundingBox();
          assert(title.y >= cover.y + cover.height, 'Every mobile row stacks its cover above its details');
          assert(Math.abs(title.x - cover.x) <= 1, 'Every mobile cover and title share the same left edge');
          assert.equal(await row.locator('[class*="NarratorSearchRow-narratorDetails"]').evaluate((details) => details.scrollWidth <= details.clientWidth + 1), true, 'Long unbroken titles wrap inside the text column');
          const cell = await row.locator('[class*="NarratorSearchRow-narratorDetails"]').boundingBox();
          const bounds = await row.boundingBox();
          assert(cell.width >= bounds.width * 0.45, 'Edition details receive at least 45% of each mobile row');
        }
        const brokenWords = await page.locator('[class*="NarratorSearchRow-narratorDetails"]').evaluateAll((columns) => columns.flatMap((details) => {
          const broken = [];
          const walker = document.createTreeWalker(details, NodeFilter.SHOW_TEXT);
          while (walker.nextNode()) {
            const node = walker.currentNode;
            for (const word of [...node.textContent.matchAll(/\p{L}+/gu)].filter((match) => match[0] !== 'ExtraordinarilyLongUnbrokenEditionTitleForWrapping')) {
              const range = document.createRange();
              range.setStart(node, word.index);
              range.setEnd(node, word.index + word[0].length);
              const tops = [...range.getClientRects()].filter((rect) => rect.width > 0).map((rect) => Math.round(rect.top));
              if (new Set(tops).size > 1) {
                broken.push(word[0]);
              }
            }
          }
          return broken;
        }));
        assert.deepEqual(brokenWords, [], 'Ordinary title and narrator words must not break mid-word');
      }
      const tableFits = await page.getByRole('table').evaluate((table) =>
        table.getBoundingClientRect().width <= table.parentElement.clientWidth + 1);
      assert.equal(tableFits, true, 'Copy status and Add controls must fit without horizontal scrolling');
      if (width === 320) {
        const originals = await page.evaluate(() => {
          const elements = [...document.querySelectorAll('[class*="NarratorSearchRow-addButton"] [class*="SpinnerButton-label"], [class*="NarratorSearchRow-inLibraryStatus"]')];
          const texts = elements.map((element) => element.textContent);
          elements.forEach((element) => {
            element.textContent = element.className.includes('SpinnerButton') ? 'Zur Bibliothek hinzufügen' : 'In der Bibliothek, Buch nicht überwacht';
          });
          return texts;
        });
        await assertAddLabelsFit(page);
        for (const row of await page.getByRole('row').filter({ has: page.getByRole('img') }).all()) {
          const details = await row.locator('[class*="NarratorSearchRow-narratorDetails"]').boundingBox();
          assert(details.width >= (await row.boundingBox()).width * 0.45, 'Long translated actions preserve the details width');
        }
        const broken = await page.locator('[class*="NarratorSearchRow-addButton"] [class*="SpinnerButton-label"], [class*="NarratorSearchRow-inLibraryStatus"]').evaluateAll((elements) => elements.flatMap((element) => {
          const node = element.firstChild;
          return [...node.textContent.matchAll(/\p{L}+/gu)].filter((word) => {
            const range = document.createRange();
            range.setStart(node, word.index);
            range.setEnd(node, word.index + word[0].length);
            return new Set([...range.getClientRects()].map((rect) => Math.round(rect.top))).size > 1;
          }).map((word) => word[0]);
        }));
        assert.deepEqual(broken, [], 'Translated action and status words wrap at spaces');
        await saveScreenshot(page, 'narrator-copy-translated', width, 'dark');
        await page.evaluate((texts) => [...document.querySelectorAll('[class*="NarratorSearchRow-addButton"] [class*="SpinnerButton-label"], [class*="NarratorSearchRow-inLibraryStatus"]')].forEach((element, index) => {
          element.textContent = texts[index];
        }), originals);
      }
      existingCopy = true;
      monitored = false;
      returnedBookId = 24;
      let releaseHeld = null;
      heldRequest = new Promise((resolve) => {
        releaseHeld = resolve;
      });
      await addButtons.first().focus();
      await page.keyboard.press('Enter');
      await page.waitForFunction(() => document.querySelectorAll('button:disabled').length > 0);
      assert.equal(await page.evaluate(() => document.activeElement?.className.includes('narratorDetails')), true, 'Keyboard focus stays on the edition while its Add button is disabled');
      assert.equal(await page.getByRole('row').filter({ hasText: 'ExtraordinarilyLongUnbrokenEditionTitleForWrapping' }).getByRole('button', { name: strings.AddToLibrary, exact: true }).isEnabled(), true, 'Only the clicked edition is busy');
      releaseHeld();
      heldRequest = null;
      const pickedRow = page.getByRole('row').filter({ hasText: 'Different edition, same narrator' });
      await pickedRow.getByText(strings.WantedEditionAlreadyHaveCopy, { exact: false }).waitFor();
      assert.equal(await pickedRow.getByRole('button', { name: strings.AddToLibrary, exact: true }).count(), 0, 'An existing-copy response removes the stale Add button');
      await pickedRow.getByText(`(${strings.WantedEditionBookNotMonitored})`, { exact: false }).waitFor();
      assert.equal(await addButtons.count(), 1);
      assert.equal(await existingRows.count(), 3);
      assert.deepEqual(wantedRequests.slice(beforeInvalid), [{ method: 'POST', body: { editionId: 32, searchForNewBook: false } }]);
      const updated = await page.evaluate(() => window.narratorDiscoveryState());
      assert.deepEqual(updated.existingCopyNarrators.map((edition) => edition.editionId), [30, 31, 32]);
      assert.equal(await page.evaluate(() => document.activeElement?.className.includes('narratorDetails')), true, 'The same edition keeps keyboard focus after moving into the library group');
      assert.equal(updated.existingCopyNarrators[2].existingCopyId, 24);
      assert.equal(updated.existingCopyNarrators[2].existingCopyMonitored, false);
      assert.deepEqual(updated.filteredNarrators.map((edition) => edition.editionId), [33]);
      assert.deepEqual(genericBookAdds, []);
      await saveScreenshot(page, 'narrator-copy-status', width, width === 320 ? 'dark' : 'light');

      const refusedRow = page.getByRole('row').filter({ hasText: 'ExtraordinarilyLongUnbrokenEditionTitleForWrapping' });
      const refusalCases = [
        { response: { status: 400, json: [{ propertyName: 'Edition', errorMessage: 'Wanted edition copies require an audiobook edition.', errorCode: '' }] }, expected: 'Wanted edition copies require an audiobook edition.' },
        { response: { status: 400, json: [{ propertyName: 'Edition', errorMessage: 'The requested edition cannot be monitored because its author has no compatible root folder for its media type.', errorCode: '' }] }, expected: 'The requested edition cannot be monitored because its author has no compatible root folder for its media type.' },
        { response: { status: 502, contentType: 'text/html', body: '<html><body>Proxy failure</body></html>' }, expected: strings.WantedEditionAddFailed }
      ];
      for (const test of refusalCases) {
        refusal = test.response;
        await refusedRow.getByRole('button', { name: strings.AddToLibrary, exact: true }).click();
        const visibleError = refusedRow.getByText(test.expected, { exact: true });
        await visibleError.waitFor();
        assert.equal(await refusedRow.getByRole('alert').textContent(), test.expected);
        await page.waitForFunction(() => {
          const error = document.querySelector('[class*="NarratorSearchRow"] [class*="Alert-danger"]');
          const bounds = error?.getBoundingClientRect();
          return bounds && bounds.top >= 0 && bounds.bottom <= innerHeight;
        });
        assert.equal(await refusedRow.getByRole('button', { name: strings.AddToLibrary, exact: true }).isEnabled(), true);
        assert.deepEqual((await page.evaluate(() => window.narratorDiscoveryState())).filteredNarrators.map((edition) => edition.editionId), [33], 'A refusal keeps the edition available for retry');
      }
      refusal = null;
      existingCopy = false;
      monitored = true;
      returnedBookId = 25;
      await refusedRow.getByRole('button', { name: strings.AddToLibrary, exact: true }).click();
      await refusedRow.getByText(strings.WantedEditionAlreadyHaveCopy, { exact: false }).waitFor();
      assert.equal(await page.locator('[class*="Alert-danger"]').count(), 0, 'A successful retry clears the error');
      await page.evaluate(() => window.injectInvalidNarratorFixture());
      const invalidRow = page.getByRole('row').filter({ hasText: 'Invalid edition' });
      await invalidRow.getByRole('button', { name: strings.AddToLibrary, exact: true }).click();
      await invalidRow.getByRole('alert').getByText(strings.SelectEdition, { exact: true }).waitFor();
      await page.route('**/edition?bookId=20', (route) => route.fulfill({ status: 500, json: { message: 'The edition list could not be loaded.' } }));
      await page.evaluate(() => window.renderNarratorDiscoveryFixture());
      await page.getByText('The edition list could not be loaded.', { exact: true }).waitFor();
      const addMessage = 'This quality profile is incompatible with the selected media type.';
      await page.evaluate((message) => window.renderAddBookValidationFixture({ status: 400, responseJSON: [{ propertyName: 'QualityProfileId', errorMessage: message, errorCode: '' }] }), addMessage);
      await page.getByText(addMessage, { exact: true }).waitFor();
      assert.deepEqual(errors, []);
    } finally {
      await page.close();
    }
  }
  console.log('Edition corrections reload files; Files has no recording override on either media side');
}

async function verifySelectionGuards(browser) {
  for (const invalid of [false, true]) {
    const item = metadataItem();
    if (invalid) {
      item.quality = null;
    }
    const { page, captured, errors } = await openFixture(browser, [item]);
    try {
      const checkbox = page.locator('input[name="1"]');
      assert.equal(await checkbox.isChecked(), false, 'Metadata suggestions require explicit selection');
      if (invalid) {
        await checkbox.focus();
        await page.keyboard.press('Space');
        await assertEventuallyChecked(page, checkbox, true);
      }
      assert.equal(await page.getByRole('button', { name: strings.Import, exact: true }).isDisabled(), true);
      assert.equal(captured.commands.length, 0);
      assert.equal(captured.bookFiles.length, 0);
      assert.deepEqual(errors, []);
    } finally {
      await page.close();
    }
  }
}

async function verifyLocalWithoutExistingFiles(browser) {
  const { page, captured, errors, commandReceived } = await openFixture(browser, [localItem()], { existingFiles: false });
  try {
    await page.getByRole('button', { name: strings.Import, exact: true }).click();
    await page.waitForFunction(() => window.manualImportCloseCount === 1);
    assert.deepEqual(captured.bookFiles.map((request) => request.bookIds), [['20']]);
    await waitForCommand(commandReceived);
    assert.equal(captured.commands.length, 1);
    assert.equal(captured.commands[0].body.files[0].bookId, 20);
    assert.equal(captured.commands[0].body.files[0].releaseGroup, 'Selected Audit Group');
    assert.deepEqual(errors, []);
  } finally {
    await page.close();
  }
}

async function verifyConvertedSelection(browser, successful, chooseOriginals = false) {
  const originals = [10, 11].map((id) => ({
    ...localItem(), id, path: `/downloads/Dune - Part ${id - 9}.mp3`,
    selectByDefault: successful ? false : null
  }));
  const converted = { ...localItem(), id: 12, path: '/library/.chaptarr-conversions/queue-download/work/Dune.m4b', selectByDefault: true };
  const { page, captured, errors, commandReceived } = await openFixture(browser, successful ? [...originals, converted] : originals,
    { replaceExisting: false });
  try {
    for (const original of originals) {
      await assertEventuallyChecked(page, page.locator(`input[name="${original.id}"]`), !successful);
    }
    if (successful) {
      await assertEventuallyChecked(page, page.locator('input[name="12"]'), true);
    }
    if (chooseOriginals) {
      await page.locator('input[name="12"]').focus();
      await page.keyboard.press('Space');
      await assertEventuallyChecked(page, page.locator('input[name="12"]'), false);
      for (const original of originals) {
        const checkbox = page.locator(`input[name="${original.id}"]`);
        await checkbox.focus();
        await page.keyboard.press('Space');
        await assertEventuallyChecked(page, checkbox, true);
      }
      // A preview refresh remounts rows; it must retain the explicit choice.
      await page.getByText(strings.InteractiveImportCombineWithExistingMode, { exact: true }).click();
      await page.getByText(strings.InteractiveImportReplaceExisting, { exact: true }).click();
      await page.getByText(strings.InteractiveImportReplaceExistingMode, { exact: true }).waitFor();
      await assertEventuallyChecked(page, page.locator('input[name="12"]'), false);
      await page.getByText(strings.InteractiveImportReplaceExistingMode, { exact: true }).click();
      await page.getByText(strings.InteractiveImportCombineWithExisting, { exact: true }).click();
      await page.getByText(strings.InteractiveImportCombineWithExistingMode, { exact: true }).waitFor();
    }
    await page.getByRole('button', { name: strings.Import, exact: true }).click();
    await page.waitForFunction(() => window.manualImportCloseCount === 1);
    await waitForCommand(commandReceived);
    assert.equal(captured.commands.length, 1);
    assert.deepEqual(captured.commands[0].body.files.map((file) => file.path),
      successful && !chooseOriginals ? [converted.path] : originals.map((file) => file.path));
    assert.deepEqual(errors, []);
  } finally {
    await page.close();
  }
}

async function verifyParentFolderConversions(browser) {
  const items = [
    { ...localItem(), id: 10, path: '/downloads/Dune/part.mp3', downloadId: 'dune', selectByDefault: false },
    { ...localItem(), id: 11, path: '/library/.chaptarr-conversions/dune/work/Dune.m4b', downloadId: 'dune', selectByDefault: true },
    { ...localItem(), id: 12, path: '/downloads/Messiah/part.mp3', downloadId: 'messiah', selectByDefault: false },
    { ...localItem(), id: 13, path: '/library/.chaptarr-conversions/messiah/work/Messiah.m4b', downloadId: 'messiah', selectByDefault: true }
  ];
  const { page, captured, errors, commandReceived } = await openFixture(browser, items, { folderMode: true, replaceExisting: false });
  try {
    for (const item of items) {
      await assertEventuallyChecked(page, page.locator(`input[name="${item.id}"]`), item.selectByDefault);
    }
    assert(captured.manualImports.every((url) => !new URL(url).searchParams.get('downloadId')));
    await page.locator('select[name="importMode"]').selectOption('copy');
    await page.getByRole('button', { name: strings.Import, exact: true }).click();
    await page.waitForFunction(() => window.manualImportCloseCount === 1);
    await waitForCommand(commandReceived);
    assert.equal(captured.commands.length, 1);
    const command = captured.commands[0].body;
    assert.deepEqual(command.files.map((file) => ({ path: file.path, downloadId: file.downloadId })),
      items.filter((item) => item.selectByDefault).map((file) => ({ path: file.path, downloadId: file.downloadId })));
    const ownership = (files) => files.map((file) => ({ path: file.path, downloadId: file.downloadId }))
      .sort((left, right) => left.path.localeCompare(right.path));
    assert.deepEqual(ownership(command.previewFiles), ownership(items));
    assert.deepEqual(errors, []);
    console.log('Parent-folder conversion selection and per-download command payload passed');
  } finally {
    await page.close();
  }
}

async function verifyQueueRemovalDefaults(browser) {
  const { page, errors } = await openFixture(browser, [localItem()]);
  try {
    for (const bulk of [false, true]) {
      for (const states of [
        ['importBlocked'], ['imported'],
        ...(bulk ? [['imported', 'delay'], ['imported', 'downloadClientUnavailable']] : [])
      ]) {
        const imported = states.includes('imported');
        await page.evaluate(({ bulkMode, rows }) => window.renderQueueRemovalFixture(
          bulkMode ? [...rows, 'importPending'] : rows, bulkMode
        ), { bulkMode: bulk, rows: states });
        const removal = page.locator('#portal-root button[class*="EnhancedSelectInput"]').first();
        const ignoreLabel = bulk ? strings.IgnoreDownloads : strings.IgnoreDownload;
        await removal.waitFor();
        assert((await removal.textContent()).includes(imported ? ignoreLabel : strings.RemoveFromDownloadClient));
        await page.getByRole('button', { name: strings.Remove, exact: true }).last().click();
        assert.equal(await page.evaluate(() => window.queueRemovalCalls[0].remove), !imported);
        assert.equal(await page.evaluate(() => window.queueRemovalCalls[0].blocklist), false);

        await page.evaluate(() => window.openQueueRemoval());
        await removal.waitFor();
        await removal.click();
        await page.locator('[class*="EnhancedSelectInput-optionsContainer"]:visible').getByText(imported ? strings.RemoveFromDownloadClient : ignoreLabel, { exact: true }).click();
        await page.evaluate(() => window.refreshQueueRemoval());
        assert((await removal.textContent()).includes(imported ? strings.RemoveFromDownloadClient : ignoreLabel), 'A refresh preserves the user choice');
        await page.getByRole('button', { name: strings.Remove, exact: true }).last().click();
        assert.equal(await page.evaluate(() => window.queueRemovalCalls[1].remove), imported, 'Explicit user choice wins');

        await page.evaluate(() => window.openQueueRemoval());
        await removal.waitFor();
        assert((await removal.textContent()).includes(imported ? ignoreLabel : strings.RemoveFromDownloadClient), 'Reopen restores the safe default');
        await page.getByRole('button', { name: strings.Close, exact: true }).click();
        assert.equal(await page.evaluate(() => window.queueRemovalCalls.length), 2, 'Cancel never removes anything');
      }
    }
    assert.deepEqual(errors, []);
    console.log('Queue removal: row and bulk, safe defaults, explicit override, refresh, reopen and cancel passed');
  } finally {
    await page.close();
  }
}

(async() => {
  let browser = null;
  try {
    await compileFixture();
    browser = await chromium.launch();
    if (process.argv.includes('--edition-recording')) {
      await verifyEditionRecordingControls(browser);
      return;
    }
    await verifyQueueRemovalDefaults(browser);
    if (process.argv.includes('--queue-removal')) {
      return;
    }
    if (process.argv.includes('--upgrade-controls')) {
      await verifyUpgradeControls(browser);
      await verifyReplacementDetails(browser);
      return;
    }
    if (process.argv.includes('--replacement-details')) {
      await verifyReplacementDetails(browser);
      return;
    }
    if (process.argv.includes('--parent-conversions')) {
      await verifyParentFolderConversions(browser);
      return;
    }

    for (const theme of ['light', 'dark']) {
      for (const width of [320, 1440]) {
        await verifyMetadataOnly(browser, width, theme);
      }
    }

    await verifyReplacementDefaultsOff(browser);
    await verifyScopedConfirmation(browser);
    await verifyMixedProceed(browser);
    await verifyReplacementDetails(browser);
    await verifyUpgradeControls(browser);
    await verifyEditionRecordingControls(browser);
    await verifySelectionGuards(browser);
    await verifyLocalWithoutExistingFiles(browser);
    await verifyConvertedSelection(browser, true);
    await verifyConvertedSelection(browser, true, true);
    await verifyConvertedSelection(browser, false);
    await verifyParentFolderConversions(browser);
    const failedPreview = await openFixture(browser, [localItem()], { failPreview: true });
    try {
      await failedPreview.page.getByRole('button', { name: strings.Import, exact: true }).click();
      await failedPreview.page.getByText(strings.UnableToLoadReplacementFiles, { exact: true }).waitFor();
      assert.equal(failedPreview.captured.commands.length, 0, 'A failed preview must not submit a replacement');
      assert.equal(await failedPreview.page.getByRole('button', { name: strings.Import, exact: true }).count(), 1,
        'The failed confirmation must not offer a Proceed button');
      assert.deepEqual(failedPreview.errors, []);
    } finally {
      await failedPreview.page.close();
    }
    console.log(`Manual-import browser checks passed. Screenshot evidence: ${artifactRoot}`);
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
