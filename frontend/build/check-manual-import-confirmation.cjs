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

async function openFixture(browser, items, { width = 1440, theme = 'dark', existingFiles = true, replaceExisting = true, failPreview = false, folderMode = false } = {}) {
  const page = await browser.newPage({ viewport: { width, height: 900 }, colorScheme: theme });
  const captured = { manualImports: [], bookFiles: [], commands: [] };
  const errors = [];
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
        return route.fulfill({ json: existingFiles ? [{ id: 500, bookId: 20, path: '/library/Local Book.epub', associatedPaths: ['/library/Other Narration/Local Book.epub', '/library/cover.jpg'], size: 2048 }] : [] });
      }

      return route.fulfill({
        status: 400,
        contentType: 'application/json',
        body: JSON.stringify({ message: 'A bookId filter is required for the test response' })
      });
    }

    if (pathname.endsWith('/command') && request.method() === 'POST') {
      captured.commands.push({ url: request.url(), body: request.postDataJSON() });
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
        body: '<!doctype html><html><head><link rel="stylesheet" href="/style.css"></head><body><div id="root"></div><div id="portal-root"></div><script>window.Chaptarr={urlBase:"",apiRoot:"/api/v1",isProduction:true,instanceName:"Chaptarr"};</script><script src="/fixture.js"></script></body></html>'
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
  await page.evaluate(({ themeName, folder }) => window.renderManualImportFixture(themeName, folder), { themeName: theme, folder: folderMode });
  if (!folderMode) {
    await page.locator('table').getByRole('button').first().click();
  }
  await page.locator('input[name="1"], input[name="10"]').first().waitFor();
  await page.getByText('Ready now (local):', { exact: false }).waitFor();
  if (!folderMode) {
    await page.getByText(strings.InteractiveImportCombineWithExistingMode, { exact: true }).waitFor();
  }
  if (replaceExisting) {
    await page.getByText(strings.InteractiveImportCombineWithExistingMode, { exact: true }).click();
    await page.getByText(strings.InteractiveImportReplaceExisting, { exact: true }).click();
    await page.getByText(strings.InteractiveImportReplaceExistingMode, { exact: true }).waitFor();
  }
  return { page, captured, errors };
}

function waitForRelevantResponse(page) {
  return page.waitForResponse((response) => {
    const pathname = new URL(response.url()).pathname;
    return pathname.endsWith('/manualimport/replacementfiles') || (pathname.endsWith('/command') && response.request().method() === 'POST');
  }, { timeout: 5000 });
}

async function saveScreenshot(page, label, width, theme) {
  const screenshotPath = path.join(artifactRoot, `${label}-${theme}-${width}.png`);
  await page.screenshot({ path: screenshotPath, fullPage: true });
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
  const { page, captured, errors } = await openFixture(browser, [metadataItem()], { width, theme });
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
  const { page, captured, errors } = await openFixture(browser, [localItem()], { replaceExisting: false });
  try {
    await page.getByRole('button', { name: strings.Import, exact: true }).click();
    await page.waitForFunction(() => window.manualImportCloseCount === 1);
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
  const { page, captured, errors } = await openFixture(browser, [localItem(), metadataItem(11)]);
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
  const { page, captured, errors } = await openFixture(browser, [localItem()], { existingFiles: false });
  try {
    await page.getByRole('button', { name: strings.Import, exact: true }).click();
    await page.waitForFunction(() => window.manualImportCloseCount === 1);
    assert.deepEqual(captured.bookFiles.map((request) => request.bookIds), [['20']]);
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
  const { page, captured, errors } = await openFixture(browser, successful ? [...originals, converted] : originals,
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
  const { page, captured, errors } = await openFixture(browser, items, { folderMode: true, replaceExisting: false });
  try {
    for (const item of items) {
      await assertEventuallyChecked(page, page.locator(`input[name="${item.id}"]`), item.selectByDefault);
    }
    assert(captured.manualImports.every((url) => !new URL(url).searchParams.get('downloadId')));
    await page.locator('select[name="importMode"]').selectOption('copy');
    await page.getByRole('button', { name: strings.Import, exact: true }).click();
    await page.waitForFunction(() => window.manualImportCloseCount === 1);
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

(async() => {
  let browser = null;
  try {
    await compileFixture();
    browser = await chromium.launch();
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
