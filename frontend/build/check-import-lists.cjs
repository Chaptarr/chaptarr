/* Verify import-list controls, root defaults, and saved options through the real app store. */
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const webpack = require('webpack');
const { chromium } = require('playwright');

const output = fs.mkdtempSync(path.join(os.tmpdir(), 'chaptarr-import-lists-'));
const config = require('./webpack.config')({ production: true });

config.entry = { fixture: path.join(__dirname, 'import-list-fixture.js') };
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
const roots = [
  { id: 1, name: 'Audio', path: '/data/audio', folderType: 1, audiobookQualityProfileId: 1,
    audiobookMetadataProfileId: 1, audiobookTags: [1], audiobookMonitored: false, audiobookMonitorExistingMode: 'missing', audiobookMonitorNewItems: 'new' },
  { id: 2, name: 'Other', path: '/data/other', folderType: 1, audiobookQualityProfileId: 2,
    audiobookMetadataProfileId: 2, audiobookTags: [2], audiobookMonitored: true, audiobookMonitorExistingMode: 'all', audiobookMonitorNewItems: 'none' },
  { id: 3, name: 'Ebooks', path: '/data/ebooks', folderType: 2, ebookQualityProfileId: 3,
    ebookMetadataProfileId: 3, ebookTags: [3], ebookMonitored: true, ebookMonitorExistingMode: 'none', ebookMonitorNewItems: 'all' }
];
const field = (name, value, label, type = 'textbox') => ({
  name, value, label, type, advanced: false, pending: false, errors: [], warnings: []
});
const mediaFields = (both = false) => [
  field('monitorAudiobooks', true, 'Monitor Audiobooks', 'checkbox'),
  field('monitorEbooks', both, 'Monitor Ebooks', 'checkbox'),
  field('audiobookRootFolderPath', '/data/audio', 'Audiobook root folder', 'path'),
  field('ebookRootFolderPath', '/data/ebooks', 'Ebook root folder', 'path'),
  ...['audiobook', 'ebook'].flatMap((mediaType) => [
    { ...field(`${mediaType}QualityProfileId`, 1, `${mediaType} quality profile`, 'select'), advanced: true, selectOptions: [{ value: 0, name: strings.UseRootFolderDefaults }, { value: 1, name: `${mediaType} profile` }, { value: 2, name: `${mediaType} other profile` }, { value: 3, name: `${mediaType} third profile` }] },
    { ...field(`${mediaType}MetadataProfileId`, 1, `${mediaType} metadata profile`, 'select'), advanced: true, selectOptions: [{ value: 0, name: strings.UseRootFolderDefaults }, { value: 1, name: 'Standard' }, { value: 2, name: 'Other metadata' }, { value: 3, name: 'Ebook metadata' }] },
    { ...field(`${mediaType}Tags`, [1], `${mediaType} tags`, 'tagSelect'), selectOptions: [{ value: 1, name: 'list-tag' }] }
  ])
];
const withField = (list, name, value) => ({
  ...list, fields: list.fields.map((item) => (item.name === name ? { ...item, value } : item))
});
const initialList = {
  id: 1, name: 'Example list', implementation: 'ChaptarrImport', implementationName: 'Chaptarr',
  enableAutomaticAdd: true, shouldMonitor: 'specificBook', shouldMonitorExisting: true, shouldSearch: false,
  rootFolderPath: '/data/audio', qualityProfileId: 1, metadataProfileId: 1, tags: [], fields: [...mediaFields(), field('baseUrl', 'http://remote.test', 'Remote URL')],
  monitoringOptions: { addMissingAuthors: true, resumePausedAuthors: false, audiobookMonitorListedBooks: false, ebookMonitorListedBooks: false }
};

async function verifyGrouping(page, addingEnabled = true) {
  const existing = page.getByText(strings.ImportListExistingAuthorSettings, { exact: true }).locator('..');
  const adding = page.getByText(strings.ImportListNewAuthorSettings, { exact: true }).locator('..');
  await existing.waitFor();
  assert.equal(await existing.locator('input[name="shouldMonitorExisting"]').count(), 1);
  assert.equal(await existing.locator('input[name="resumePausedAuthors"]').count(), 1);
  assert.equal(await existing.locator('input[name="monitorListedBooks"]').count(), 0);
  assert.equal(await adding.locator('input[name="addMissingAuthors"]').count(), 1);
  assert.equal(await adding.locator('input[name="monitorListedBooks"]').count(), 0);
  assert.equal(await adding.locator('input[name="shouldMonitorExisting"]').count(), 0);
  assert.equal(await page.getByText(strings.ImportListKeepListedBooks, { exact: true }).count(), 1);
  assert.equal(await page.getByText(strings.ImportListResumeAuthors, { exact: true }).count(), 1);
  assert.equal(await page.getByText(strings.ImportListInitialBooksHelp, { exact: true }).count() > 0, addingEnabled);
  assert.doesNotMatch(await page.locator('body').innerText(), /\bpaused\b/i);
  const headings = await page.locator('fieldset > legend').allTextContents();
  assert.ok(headings.includes(strings.ImportListExistingAuthorSettings));
  assert.ok(headings.includes(strings.ImportListNewAuthorSettings));
  assert.ok(headings.indexOf(strings.ImportListExistingAuthorSettings) < headings.indexOf(strings.ImportListNewAuthorSettings));
  const general = page.getByText(strings.ImportListSettings, { exact: true }).locator('..');
  assert.equal(await general.locator('[class*="SegmentedToggle-toggle"][role="group"]').count(), 1, 'Format scope applies to existing and created authors');
}

async function verify(browser, width) {
  const page = await browser.newPage({ viewport: { width, height: 900 } });
  const errors = [];
  const writes = [];
  let availableRoots = roots;
  let holdRoots = false;
  let failRoots = false;
  let heldRootRequest = null;
  let rootRequestReady = null;
  let bulkFailure = null;
  let bulkResult = [];
  const bulkWrites = [];
  const catalogs = {
    quality: [{ id: 1, name: 'Audio Any', mediaType: 0 }, { id: 2, name: 'Audio High', mediaType: 0 }, { id: 3, name: 'Ebook EPUB', mediaType: 1 }],
    metadata: [{ id: 1, name: 'General', profileType: 'General' }, { id: 2, name: 'Audio Metadata', profileType: 'Audiobook' }, { id: 3, name: 'Ebook Metadata', profileType: 'Ebook' }]
  };
  page.on('pageerror', (error) => errors.push(error.message));
  const cssName = fs.readdirSync(path.join(output, 'Content')).find((name) => name.endsWith('.css'));
  await page.route('**/*', (route) => {
    const pathname = new URL(route.request().url()).pathname;
    if (pathname.endsWith('/localization')) {
      return route.fulfill({ json: { Strings: strings } });
    }
    if (pathname.toLowerCase().endsWith('/rootfolder')) {
      if (failRoots) {
        return route.fulfill({ status: 500, json: { message: 'Root folders unavailable' } });
      }
      if (holdRoots) {
        heldRootRequest = route;
        rootRequestReady();
        return;
      }
      return route.fulfill({ json: availableRoots });
    }
    if (pathname.endsWith('/qualityprofile')) {
      return route.fulfill({ json: catalogs.quality });
    }
    if (pathname.endsWith('/metadataprofile')) {
      return route.fulfill({ json: catalogs.metadata });
    }
    if (pathname.endsWith('/importlist/bulk')) {
      bulkWrites.push({ method: route.request().method(), body: route.request().postDataJSON() });
      return route.fulfill(bulkFailure ? { status: 400, json: bulkFailure } : { json: bulkResult });
    }
    if (pathname.endsWith('/importlist/1') || pathname.endsWith('/importlist')) {
      const body = route.request().postDataJSON();
      writes.push(body);
      return route.fulfill({ json: { ...body, id: 1 } });
    }
    if (pathname === '/fixture.js') {
      return route.fulfill({ path: path.join(output, 'fixture.js'), contentType: 'text/javascript' });
    }
    if (pathname === '/style.css') {
      return route.fulfill({ path: path.join(output, 'Content', cssName), contentType: 'text/css' });
    }
    if (pathname === '/') {
      return route.fulfill({ contentType: 'text/html', body: '<!doctype html><html><head><meta name="viewport" content="width=device-width, initial-scale=1"><link rel="stylesheet" href="/style.css"></head><body><div id="root"></div><div id="portal-root"></div><script>window.Chaptarr={urlBase:"",apiRoot:"/api/v1",isProduction:true,instanceName:"Chaptarr"};</script><script src="/fixture.js"></script></body></html>' });
    }
    const asset = path.resolve(output, `.${decodeURIComponent(pathname)}`);
    if (asset.startsWith(`${output}${path.sep}`) && fs.existsSync(asset) && fs.statSync(asset).isFile()) {
      return route.fulfill({ path: asset });
    }
    return route.fulfill({ json: [] });
  });
  try {
    await page.goto('http://import-list.test/');
    await page.waitForFunction(() => window.fixtureReady);
    const initialField = () => page.getByText(strings.InitialBookMonitoring, { exact: true }).first().locator('..');
    const initialValue = () => initialField().locator('[class*="HintedSelectInputSelectedValue-valueText"]').innerText();
    const render = async(list) => {
      const hydrated = {
        ...list, fields: list.fields.map((item) => ({ ...initialList.fields.find((schema) => schema.name === item.name), ...item }))
      };
      await page.evaluate((value) => window.renderList(value), hydrated);
      await page.locator('input[name="addMissingAuthors"]').waitFor();
      if (!holdRoots && !failRoots) {
        await page.waitForFunction(() => window.listRootState().isPopulated);
      }
    };
    const save = async(label = strings.Save) => {
      const before = writes.length;
      await page.getByRole('button', { name: label, exact: true }).click();
      await page.waitForFunction(() => window.listCloseCount === 1);
      assert.equal(writes.length, before + 1);
      return writes[before];
    };
    const noOptionsDraft = async() => assert.equal(
      await page.evaluate(() => window.listState().pendingChanges.monitoringOptions), undefined,
      'Displaying inherited defaults must not create fixed list overrides'
    );

    const setup = () => page.evaluate(() => {
      const state = window.listState();
      const item = state.items[0] || state.selectedSchema;
      return {
        fields: { ...Object.fromEntries(item.fields.map(({ name, value }) => [name, value])), ...state.pendingChanges.fields },
        options: state.pendingChanges.monitoringOptions || item.monitoringOptions
      };
    });
    const chooseRoot = async(from, to, apply = true) => {
      const before = await setup();
      await page.getByText(from, { exact: true }).click();
      await page.getByText(to, { exact: true }).click();
      await page.getByText(strings.ApplyRootFolderDefaultsConfirm, { exact: true }).waitFor();
      assert.equal((await setup()).fields.audiobookTags.join(), before.fields.audiobookTags.join(), 'Root selection cannot change tags before confirmation');
      await page.getByRole('button', { name: apply ? strings.Apply : strings.KeepCurrentSettings, exact: true }).click();
    };
    const showAdvanced = () => page.getByTitle(strings.HiddenClickToShow, { exact: true }).click();
    const hideAdvanced = () => page.getByTitle(strings.ShownClickToHide, { exact: true }).click();
    const audiobookDefaults = {
      audiobookMonitored: false, audiobookMonitor: 'missing', audiobookMonitorNewItems: 'new'
    };
    const ebookDefaults = {
      ebookMonitored: true, ebookMonitor: 'none', ebookMonitorNewItems: 'all'
    };
    const assertRootSetup = async(mediaType, root) => {
      const value = await setup();
      for (const suffix of ['QualityProfileId', 'MetadataProfileId', 'Tags']) {
        assert.deepEqual(value.fields[`${mediaType}${suffix}`], root[`${mediaType}${suffix}`], `Selected root loads ${mediaType}${suffix}, including hidden controls`);
      }
      assert.equal(value.options[`${mediaType}Monitored`], root[`${mediaType}Monitored`]);
      assert.equal(value.options[`${mediaType}Monitor`], root[`${mediaType}MonitorExistingMode`]);
      assert.equal(value.options[`${mediaType}MonitorNewItems`], root[`${mediaType}MonitorNewItems`]);
    };

    // A failed load cannot save incomplete settings; Retry uses the existing GET.
    failRoots = true;
    await render({ ...withField(initialList, 'audiobookQualityProfileId', 0), monitoringOptions: {
      ...initialList.monitoringOptions, audiobookMonitored: null, audiobookMonitor: null, audiobookMonitorNewItems: null
    } });
    await page.getByText(strings.UnableToLoadRootFolders, { exact: true }).waitFor();
    assert.equal(await page.getByRole('button', { name: strings.Save, exact: true }).isDisabled(), true);
    failRoots = false;
    await page.getByRole('button', { name: strings.Retry, exact: true }).click();
    await page.waitForFunction(() => window.listRootState().isPopulated);
    assert.equal(await page.getByRole('button', { name: strings.Save, exact: true }).isEnabled(), true);
    await chooseRoot('[Audio] /data/audio', '[Other] /data/other', false);
    assert.equal((await setup()).fields.audiobookRootFolderPath, '/data/other');
    assert.equal((await setup()).fields.audiobookQualityProfileId, 1);
    assert.deepEqual((await setup()).fields.audiobookTags, [1]);
    assert.equal((await setup()).options.audiobookMonitored, false);
    assert.equal((await setup()).options.audiobookMonitor, 'missing', 'Declining keeps even inherited monitoring from the old root');
    assert.equal(writes.length, 0);

    // A new list's prepopulated roots load complete defaults after the roots arrive.
    holdRoots = true;
    const heldRootReady = new Promise((resolve) => {
      rootRequestReady = resolve;
    });
    const newDualRoots = { ...initialList, id: undefined, fields: mediaFields(true).map((item) => (
      item.name.endsWith('ProfileId') ? { ...item, value: 0 } : item
    )) };
    await render(newDualRoots);
    await page.waitForFunction(() => !window.listState().isSaving);
    assert.equal((await setup()).fields.audiobookQualityProfileId, 0);
    assert.equal(writes.length, 0);
    await heldRootReady;
    const loadingSave = page.getByRole('button', { name: strings.AddListAudiobooksAndEbooks, exact: true });
    assert.equal(await loadingSave.isDisabled(), true, 'Creation cannot save prepopulated root paths before their defaults arrive');
    assert.equal(await page.locator('input[name="audiobookMonitored"]').count(), 0, 'Creation defaults stay hidden until roots arrive');
    await page.locator('input[name="addMissingAuthors"]').locator('..').click();
    assert.equal(await loadingSave.isEnabled(), true, 'Existing-author actions do not require creation root defaults');
    assert.equal(await page.locator('input[name="shouldMonitorExisting"]').isEnabled(), true);
    await page.locator('input[name="addMissingAuthors"]').locator('..').click();
    assert.equal(await loadingSave.isDisabled(), true);
    holdRoots = false;
    await heldRootRequest.fulfill({ json: roots });
    await page.getByText(strings.MissingBooks, { exact: true }).first().waitFor();
    assert.equal(await loadingSave.isEnabled(), true, 'Creation becomes saveable only after defaults arrive');
    await assertRootSetup('audiobook', roots[0]);
    await assertRootSetup('ebook', roots[2]);
    assert.deepEqual((await setup()).options, { ...initialList.monitoringOptions, ...audiobookDefaults, ...ebookDefaults });
    assert.equal(await page.getByText('audiobook quality profile', { exact: true }).count(), 0);
    assert.equal(await page.getByText('ebook metadata profile', { exact: true }).count(), 0);
    const beforeAdvanced = await setup();
    await showAdvanced();
    assert.equal(await page.getByText('audiobook profile', { exact: true }).count(), 1);
    assert.equal(await page.getByText('Standard', { exact: true }).count(), 1);
    assert.equal(await page.getByText('Ebook metadata', { exact: true }).count(), 1);
    await hideAdvanced();
    assert.deepEqual(await setup(), beforeAdvanced, 'Showing and hiding Advanced does not reset setup choices');
    assert.equal(writes.length, 0, 'Auto-selection and Advanced do not save automatically');

    // Both compatible root dropdowns can select their initial paths in one mount.
    await render(withField(withField(newDualRoots, 'audiobookRootFolderPath', ''), 'ebookRootFolderPath', ''));
    await page.getByText('[Audio] /data/audio', { exact: true }).waitFor();
    await page.getByText('[Ebooks] /data/ebooks', { exact: true }).waitFor();
    await assertRootSetup('audiobook', roots[0]);
    await assertRootSetup('ebook', roots[2]);
    const autoSelected = await save(strings.AddListAudiobooksAndEbooks);
    assert.deepEqual(autoSelected.monitoringOptions, { ...initialList.monitoringOptions, ...audiobookDefaults, ...ebookDefaults });
    const autoFields = Object.fromEntries(autoSelected.fields.map(({ name, value }) => [name, value]));
    assert.equal(autoFields.audiobookQualityProfileId, 1);
    assert.equal(autoFields.ebookMetadataProfileId, 3);
    assert.deepEqual(autoFields.audiobookTags, [1]);
    assert.deepEqual(autoFields.ebookTags, [3]);

    // A prepopulated inactive root also loads defaults before a new list selects both formats.
    await render({ ...newDualRoots, fields: newDualRoots.fields.map((item) => (
      item.name === 'monitorEbooks' ? { ...item, value: false } : item
    )) });
    await assertRootSetup('ebook', roots[2]);
    await page.locator('[class*="SegmentedToggle-toggle"][role="group"]').getByRole('button', { name: 'Both', exact: true }).click();
    await showAdvanced();
    assert.equal(await page.getByText('ebook third profile', { exact: true }).count(), 1);
    await hideAdvanced();
    await assertRootSetup('ebook', roots[2]);

    await render(initialList);
    assert.equal(await initialValue(), strings.MissingBooks);
    assert.equal(await page.locator('input[name="audiobookMonitored"]').isChecked(), false);
    assert.equal(await page.locator('input[name="shouldMonitorExisting"]').isChecked(), true);
    assert.equal(await page.locator('input[name="resumePausedAuthors"]').isChecked(), false);
    await verifyGrouping(page);
    await noOptionsDraft();
    await chooseRoot('[Audio] /data/audio', '[Other] /data/other');
    assert.equal(await page.locator('input[name="audiobookMonitored"]').isChecked(), true);
    assert.equal(await initialValue(), strings.AllBooks);
    await assertRootSetup('audiobook', roots[1]);
    await chooseRoot('[Other] /data/other', '[Audio] /data/audio');
    await page.locator('input[name="addMissingAuthors"]').uncheck({ force: true });
    assert.equal(writes.length, 1, 'Draft controls must not save automatically');
    const inherited = await save();
    assert.deepEqual(inherited.monitoringOptions, { ...initialList.monitoringOptions, ...audiobookDefaults, addMissingAuthors: false },
      'Saving retains the setup copied from the last selected root');
    await render(inherited);
    await page.locator('input[name="enableAutomaticAdd"]').uncheck({ force: true });
    const disabled = await save();
    assert.equal(disabled.enableAutomaticAdd, false);
    assert.deepEqual(disabled.monitoringOptions, inherited.monitoringOptions, 'Disabling retains the list configuration');

    const explicit = { ...withField(initialList, 'audiobookRootFolderPath', '/data/other'), monitoringOptions: {
      ...initialList.monitoringOptions, audiobookMonitored: false, audiobookMonitor: 'none', audiobookMonitorNewItems: 'new'
    } };
    await render(explicit);
    assert.equal(await initialValue(), strings.None);
    assert.equal(await page.locator('input[name="audiobookMonitored"]').isChecked(), false);
    await chooseRoot('[Other] /data/other', '[Audio] /data/audio');
    assert.equal(await initialValue(), strings.MissingBooks, 'Changing an existing root replaces earlier overrides');
    await assertRootSetup('audiobook', roots[0]);
    await initialField().getByRole('button', { name: strings.MissingBooks, exact: true }).click();
    await page.locator('[class*="HintedSelectInputOption-optionText"]').getByText(strings.None, { exact: true }).click();
    for (const name of ['shouldMonitorExisting', 'resumePausedAuthors', 'shouldSearch']) {
      assert.equal(await page.locator(`input[name="${name}"]`).isDisabled(), false);
    }
    await page.locator('input[name="shouldMonitorExisting"]').uncheck({ force: true });
    await page.locator('input[name="resumePausedAuthors"]').focus();
    await page.keyboard.press('Space');
    assert.equal(await page.locator('input[name="resumePausedAuthors"]').isChecked(), true);
    assert.equal(await page.locator('input[name="shouldMonitorExisting"]').isChecked(), false);
    const savedExplicit = await save();
    assert.equal(Object.hasOwn(savedExplicit, 'shouldMonitor'), false, 'Modern UI saves omit legacy monitoring intent');
    assert.equal(savedExplicit.shouldMonitorExisting, false);
    assert.deepEqual(savedExplicit.monitoringOptions, { ...explicit.monitoringOptions, ...audiobookDefaults, audiobookMonitor: 'none', resumePausedAuthors: true },
      'Existing-author actions do not overwrite lower author or book policies');

    const dualList = {
      ...initialList, implementation: 'GoodreadsListImportList', fields: mediaFields(true)
    };
    await render(dualList);
    await noOptionsDraft();
    assert.equal(await page.locator('input[name="audiobookMonitored"]').isChecked(), false);
    assert.equal(await page.locator('input[name="ebookMonitored"]').isChecked(), true);
    await verifyGrouping(page);
    assert.equal(await page.getByText(strings.InitialBookMonitoring, { exact: true }).count(), 2);
    await page.locator('input[name="ebookMonitored"]').uncheck({ force: true });
    const savedDual = await save();
    assert.deepEqual(savedDual.monitoringOptions, { ...initialList.monitoringOptions, ebookMonitored: false },
      'An ebook override does not freeze inherited policies on either side');

    // A saved catalog-wide mode can be changed to list-only on one format.
    const migratedAll = { ...dualList, shouldMonitor: 'entireAuthor', monitoringOptions: {
      addMissingAuthors: true, resumePausedAuthors: false,
      audiobookMonitor: 'all', ebookMonitor: 'all',
      audiobookMonitorListedBooks: null, ebookMonitorListedBooks: null
    } };
    await render(migratedAll);
    await noOptionsDraft();
    const unchangedAll = await save();
    assert.deepEqual(unchangedAll.monitoringOptions, migratedAll.monitoringOptions, 'No-op saves retain nullable legacy flags');
    assert.equal(Object.hasOwn(unchangedAll, 'shouldMonitor'), false);
    await render(migratedAll);
    await initialField().getByRole('button', { name: strings.AllBooks, exact: true }).focus();
    await page.keyboard.press('ArrowDown');
    const listChoices = page.locator('[class*="HintedSelectInputOption-optionText"]');
    assert.equal(await listChoices.count(), 5, 'The existing dropdown explicitly includes list-only monitoring');
    assert.equal(await initialField().getByRole('button', { name: strings.AllBooks, exact: true }).evaluate((button) => button === document.activeElement), true,
      'Opening the options retains focus on the control that handles navigation');
    await page.keyboard.press('ArrowDown');
    assert.equal(await initialValue(), strings.ImportListOnlyListedBooks, 'Arrow navigation previews the next choice');
    await noOptionsDraft();
    await page.keyboard.press('Enter');
    assert.equal(await initialValue(), strings.ImportListOnlyListedBooks, 'The list-only choice works with the existing keyboard select');
    assert.equal(await page.locator('input[name="audiobookMonitorListedBooks"]').count(), 0, 'List-only needs no second setting');
    const listOnly = await save();
    assert.equal(listOnly.monitoringOptions.audiobookMonitor, 'specificBook');
    assert.equal(listOnly.monitoringOptions.audiobookMonitorListedBooks, true);
    assert.equal(listOnly.monitoringOptions.ebookMonitor, 'all', 'Audiobook list-only does not change ebook initial monitoring');
    assert.equal(listOnly.monitoringOptions.ebookMonitorListedBooks, null, 'The untouched format retains its legacy flag');
    assert.equal(Object.hasOwn(listOnly, 'shouldMonitor'), false);
    await render({ ...listOnly, shouldMonitor: migratedAll.shouldMonitor });
    await noOptionsDraft();
    assert.equal(await initialValue(), strings.ImportListOnlyListedBooks, 'Saved list-only intent renders on reopen');

    // Old additive selections display their complete meaning without rewriting saved data.
    const legacyCombinations = { ...dualList, monitoringOptions: {
      addMissingAuthors: true, resumePausedAuthors: false,
      audiobookMonitor: 'missing', ebookMonitor: 'existing'
    } };
    await render(legacyCombinations);
    await noOptionsDraft();
    assert.equal(await initialValue(), strings.MissingBooks);
    assert.equal(await page.locator('input[name="audiobookMonitorListedBooks"]').isChecked(), true);
    const ebookInitialField = () => page.getByText(strings.InitialBookMonitoring, { exact: true }).nth(1).locator('..');
    assert.equal(await ebookInitialField().getByRole('button', { name: strings.BooksWithFiles, exact: true }).count(), 1);
    assert.equal(await page.locator('input[name="ebookMonitorListedBooks"]').isChecked(), true);
    const unchangedCombinations = await save();
    assert.deepEqual(unchangedCombinations.monitoringOptions, legacyCombinations.monitoringOptions);
    await render(legacyCombinations);
    await ebookInitialField().getByRole('button', { name: strings.BooksWithFiles, exact: true }).click();
    await listChoices.getByText(strings.ImportListOnlyListedBooks, { exact: true }).click();
    const ebookListOnly = await save();
    assert.equal(ebookListOnly.monitoringOptions.ebookMonitor, 'specificBook');
    assert.equal(ebookListOnly.monitoringOptions.ebookMonitorListedBooks, true);
    assert.equal(ebookListOnly.monitoringOptions.audiobookMonitor, 'missing');
    assert.equal(Object.hasOwn(ebookListOnly.monitoringOptions, 'audiobookMonitorListedBooks'), false);
    await render({ ...legacyCombinations, monitoringOptions: {
      ...legacyCombinations.monitoringOptions, audiobookMonitor: 'none', ebookMonitor: 'none'
    } });
    assert.equal(await initialValue(), strings.ImportListOnlyListedBooks, 'Legacy None plus listed books displays list-only');
    await noOptionsDraft();

    await render({ ...legacyCombinations, shouldMonitor: undefined });
    await noOptionsDraft();
    assert.equal(await page.locator('input[name="audiobookMonitorListedBooks"]').isChecked(), false, 'An omitted legacy policy uses the API None default');
    assert.equal(await page.locator('input[name="ebookMonitorListedBooks"]').isChecked(), false);

    // Changing one format can remove or retain additive list intent without touching the other.
    await render(legacyCombinations);
    await page.locator('input[name="audiobookMonitorListedBooks"]').uncheck({ force: true });
    const missingOnly = await save();
    assert.equal(missingOnly.monitoringOptions.audiobookMonitor, 'missing');
    assert.equal(missingOnly.monitoringOptions.audiobookMonitorListedBooks, false);
    assert.equal(missingOnly.monitoringOptions.ebookMonitor, 'existing');
    await render({ ...missingOnly, shouldMonitor: 'specificBook' });
    await page.locator('input[name="audiobookMonitorListedBooks"]').focus();
    await page.keyboard.press('Space');
    assert.equal(await page.locator('input[name="audiobookMonitorListedBooks"]').isChecked(), true);
    const missingAndListed = await save();
    assert.equal(missingAndListed.monitoringOptions.audiobookMonitor, 'missing');
    assert.equal(missingAndListed.monitoringOptions.audiobookMonitorListedBooks, true);
    const allWithoutListed = { ...dualList, monitoringOptions: {
      ...initialList.monitoringOptions, audiobookMonitor: 'all', ebookMonitor: 'all'
    } };
    await render(allWithoutListed);
    await noOptionsDraft();
    assert.equal(await page.locator('input[name="audiobookMonitorListedBooks"]').isChecked(), false);
    assert.deepEqual((await save()).monitoringOptions, allWithoutListed.monitoringOptions, 'All retains an explicit listed flag off');
    await render(allWithoutListed);
    await page.locator('input[name="audiobookMonitorListedBooks"]').check({ force: true });
    const allAndListed = await save();
    assert.equal(allAndListed.monitoringOptions.audiobookMonitor, 'all');
    assert.equal(allAndListed.monitoringOptions.audiobookMonitorListedBooks, true, 'All can independently request listed editions');
    assert.equal(allAndListed.monitoringOptions.ebookMonitorListedBooks, false);
    await render({ ...allAndListed, shouldMonitor: 'specificBook' });
    await initialField().getByRole('button', { name: strings.AllBooks, exact: true }).click();
    await listChoices.getByText(strings.None, { exact: true }).click();
    assert.equal(await page.locator('input[name="audiobookMonitorListedBooks"]').count(), 0, 'None has no additive listed-books trap');
    const noneSelected = await save();
    assert.equal(noneSelected.monitoringOptions.audiobookMonitor, 'none');
    assert.equal(noneSelected.monitoringOptions.audiobookMonitorListedBooks, false);

    await render({ ...initialList, id: undefined });
    await assertRootSetup('audiobook', roots[0]);
    await assertRootSetup('ebook', roots[2]);
    await chooseRoot('[Audio] /data/audio', '[Other] /data/other');
    assert.deepEqual(await page.evaluate(() => window.listState().pendingChanges.monitoringOptions), {
      ...initialList.monitoringOptions, ...ebookDefaults, audiobookMonitored: true, audiobookMonitor: 'all', audiobookMonitorNewItems: 'none'
    }, 'Choosing a root for a new list fills only its supported media side');
    await initialField().getByRole('button', { name: strings.AllBooks, exact: true }).click();
    await page.locator('[class*="HintedSelectInputOption-optionText"]').getByText(strings.None, { exact: true }).click();
    await page.locator('input[name="audiobookMonitored"]').uncheck({ force: true });
    const newOverride = await save(strings.AddListAudiobooks);
    assert.equal(newOverride.monitoringOptions.audiobookMonitor, 'none', 'The list can select None even when the root uses All');
    assert.equal(newOverride.monitoringOptions.audiobookMonitored, false);
    assert.equal(Object.hasOwn(newOverride, 'shouldMonitor'), false);
    assert.equal(newOverride.monitoringOptions.audiobookMonitorListedBooks, false, 'None does not also request listed books');

    const ebookOverrides = { ebookMonitored: false, ebookMonitor: 'missing', ebookMonitorNewItems: 'new' };
    await render({ ...dualList, id: undefined });
    await page.locator('input[name="ebookMonitored"]').uncheck({ force: true });
    await page.getByText(strings.InitialBookMonitoring, { exact: true }).nth(1).locator('..').getByRole('button', { name: strings.None, exact: true }).click();
    await page.locator('[class*="HintedSelectInputOption-optionText"]').getByText(strings.MissingBooks, { exact: true }).click();
    await page.getByText(strings.MonitorNewBooks, { exact: true }).nth(1).locator('..').getByRole('button', { name: strings.AllNewBooks, exact: true }).click();
    await page.locator('[class*="HintedSelectInputOption-optionText"]').getByText(strings.FutureReleases, { exact: true }).click();
    const untouchedEbook = (await setup()).fields;
    await chooseRoot('[Audio] /data/audio', '[Other] /data/other');
    assert.equal((await setup()).fields.ebookMetadataProfileId, untouchedEbook.ebookMetadataProfileId);
    assert.deepEqual((await setup()).fields.ebookTags, untouchedEbook.ebookTags);
    const newDual = await save(strings.AddListAudiobooksAndEbooks);
    assert.deepEqual(newDual.monitoringOptions, {
      ...initialList.monitoringOptions, ...ebookOverrides,
      audiobookMonitored: true, audiobookMonitor: 'all', audiobookMonitorNewItems: 'none'
    }, 'A new-list audiobook root change leaves explicit ebook choices alone');

    // Saved-list root changes replace that side's earlier setup, then later explicit overrides persist.
    const savedSetup = { ...dualList, monitoringOptions: {
      ...initialList.monitoringOptions, ...audiobookDefaults, ...ebookOverrides
    }, fields: mediaFields(true).map((item) => (
      item.name === 'ebookQualityProfileId' || item.name === 'ebookMetadataProfileId' ? { ...item, value: 3 } : item
    )) };
    await render(savedSetup);
    await noOptionsDraft();
    const ebookBefore = await setup();
    await chooseRoot('[Audio] /data/audio', '[Other] /data/other');
    await assertRootSetup('audiobook', roots[1]);
    assert.equal((await setup()).fields.ebookQualityProfileId, ebookBefore.fields.ebookQualityProfileId);
    assert.deepEqual((await setup()).fields.ebookTags, ebookBefore.fields.ebookTags);
    for (const suffix of ['Monitored', 'Monitor', 'MonitorNewItems']) {
      assert.equal((await setup()).options[`ebook${suffix}`], ebookBefore.options[`ebook${suffix}`]);
    }
    await showAdvanced();
    const qualityGroup = page.getByText('audiobook quality profile', { exact: true }).locator('..');
    await qualityGroup.getByRole('button', { name: 'audiobook other profile', exact: true }).click();
    await page.locator('[class*="HintedSelectInputOption-optionText"]').getByText('audiobook profile', { exact: true }).click();
    const metadataGroup = page.getByText('audiobook metadata profile', { exact: true }).locator('..');
    await metadataGroup.getByRole('button', { name: 'Other metadata', exact: true }).click();
    await page.locator('[class*="HintedSelectInputOption-optionText"]').getByText('Standard', { exact: true }).click();
    await page.locator('input[name="audiobookTags"]').press('Backspace');
    await initialField().getByRole('button', { name: strings.AllBooks, exact: true }).click();
    await page.locator('[class*="HintedSelectInputOption-optionText"]').getByText(strings.None, { exact: true }).click();
    await page.locator('input[name="audiobookMonitored"]').uncheck({ force: true });
    const overridesAfterRoot = await setup();
    await hideAdvanced();
    assert.deepEqual(await setup(), overridesAfterRoot, 'Advanced visibility retains overrides made after root selection');
    const savedAfterRoot = await save();
    const savedAfterFields = Object.fromEntries(savedAfterRoot.fields.map(({ name, value }) => [name, value]));
    assert.equal(savedAfterFields.audiobookQualityProfileId, 1);
    assert.equal(savedAfterFields.audiobookMetadataProfileId, 1);
    assert.deepEqual(savedAfterFields.audiobookTags, [], 'An explicit empty tag override stays empty');
    assert.equal(savedAfterRoot.monitoringOptions.audiobookMonitor, 'none');
    assert.equal(savedAfterRoot.monitoringOptions.audiobookMonitored, false);
    await render(savedAfterRoot);
    await noOptionsDraft();
    assert.deepEqual(await setup(), overridesAfterRoot, 'Reopening a saved list retains later overrides until its root changes');

    const hiddenPolicies = {
      ...initialList.monitoringOptions, audiobookMonitored: false, audiobookMonitor: 'none',
      audiobookMonitorNewItems: 'none', ebookMonitored: false, ebookMonitor: 'none', ebookMonitorNewItems: 'new'
    };
    const retained = { ...dualList, monitoringOptions: hiddenPolicies };
    await render(retained);
    const creationLabels = mediaFields(true).filter((item) => !item.name.startsWith('monitor') && !item.advanced).map((item) => item.label);
    for (const label of creationLabels) {
      assert.equal(await page.getByText(label, { exact: true }).count(), 1, `Visible creation field ${label}`);
    }
    assert.equal(await page.getByText(strings.InitialBookMonitoring, { exact: true }).count(), 2);
    assert.equal(await page.getByText(strings.MonitorNewBooks, { exact: true }).count(), 2);
    await page.locator('input[name="addMissingAuthors"]').uncheck({ force: true });
    await verifyGrouping(page, false);
    for (const label of creationLabels) {
      assert.equal(await page.getByText(label, { exact: true }).count(), 0, `Hidden creation field ${label}`);
    }
    assert.equal(await page.getByText(strings.InitialBookMonitoring, { exact: true }).count(), 0);
    assert.equal(await page.getByText(strings.MonitorNewBooks, { exact: true }).count(), 0);
    assert.equal(await page.getByText('Audiobook root folder', { exact: true }).count(), 0);
    assert.equal(await page.getByText('Ebook root folder', { exact: true }).count(), 0);
    for (const mediaType of ['audiobook', 'ebook']) {
      for (const suffix of ['Monitored', 'Monitor', 'MonitorNewItems', 'QualityProfileId', 'MetadataProfileId', 'Tags']) {
        assert.equal(await page.locator(`[name="${mediaType}${suffix}"]`).count(), 0, `Hidden creation control ${mediaType}${suffix}`);
      }
    }
    const formats = page.locator('[class*="SegmentedToggle-toggle"][role="group"]');
    await formats.getByRole('button', { name: 'eBooks' }).click();
    assert.equal(await formats.getByRole('button', { name: 'eBooks' }).getAttribute('aria-pressed'), 'true');
    await formats.getByRole('button', { name: 'Both', exact: true }).click();
    await page.locator('input[name="addMissingAuthors"]').check({ force: true });
    await verifyGrouping(page);
    assert.equal(await initialValue(), strings.None);
    assert.equal(await page.locator('input[name="audiobookMonitored"]').isChecked(), false);
    assert.equal(await page.locator('input[name="ebookMonitored"]').isChecked(), false);
    assert.deepEqual(await page.evaluate(() => window.listState().pendingChanges.monitoringOptions), hiddenPolicies,
      'Toggling creation back on changes no lower monitoring policy');
    await page.locator('input[name="addMissingAuthors"]').uncheck({ force: true });
    const hiddenSaved = await save();
    assert.deepEqual(hiddenSaved.fields, retained.fields.map(({ name, value }) => ({ name, value })), 'Hiding and restoring retains roots, profiles, tags and format selection');
    assert.deepEqual(hiddenSaved.monitoringOptions, { ...hiddenPolicies, addMissingAuthors: false });
    await render(hiddenSaved);
    await page.locator('input[name="addMissingAuthors"]').focus();
    await page.keyboard.press('Space');
    assert.equal(await page.locator('input[name="addMissingAuthors"]').isChecked(), true);
    assert.equal(await initialValue(), strings.None, 'Saved hidden None reappears without root default replacement');

    // The root menus use the actual ROOT_FOLDER_SELECT compatible-format filter.
    await page.getByText('[Audio] /data/audio', { exact: true }).click();
    assert.equal(await page.locator('[class*="HintedSelectInputOption-optionText"]').getByText('[Ebooks] /data/ebooks', { exact: true }).count(), 0);
    await page.keyboard.press('Escape');
    await page.getByText('[Ebooks] /data/ebooks', { exact: true }).click();
    assert.equal(await page.locator('[class*="HintedSelectInputOption-optionText"]').getByText('[Audio] /data/audio', { exact: true }).count(), 0);
    await page.keyboard.press('Escape');

    // With no compatible roots, Add off still permits an existing-format-only list.
    availableRoots = [];
    const noRoots = withField(withField(dualList, 'audiobookRootFolderPath', ''), 'ebookRootFolderPath', '');
    await render({ ...noRoots, monitoringOptions: { ...hiddenPolicies, addMissingAuthors: false } });
    const saveButton = page.getByRole('button', { name: strings.Save, exact: true });
    assert.equal(await saveButton.isDisabled(), false, 'Hidden roots cannot disable Save');
    await page.locator('input[name="addMissingAuthors"]').check({ force: true });
    assert.equal(await saveButton.isDisabled(), true, 'Creation needs roots for selected formats');
    await page.locator('input[name="addMissingAuthors"]').uncheck({ force: true });
    assert.equal(await saveButton.isDisabled(), false);
    const noRootSaved = await save();
    assert.equal(noRootSaved.monitoringOptions.addMissingAuthors, false);
    assert.equal(noRootSaved.fields.find((item) => item.name === 'audiobookRootFolderPath').value, '');
    assert.equal(noRootSaved.fields.find((item) => item.name === 'ebookRootFolderPath').value, '');

    // A configured audiobook root does not satisfy creation of an ebook side.
    const audioOnlyRoot = withField(dualList, 'ebookRootFolderPath', '');
    await render(audioOnlyRoot);
    assert.equal(await saveButton.isDisabled(), true);
    await formats.getByRole('button', { name: 'Audiobooks' }).click();
    assert.equal(await saveButton.isDisabled(), false);
    await formats.getByRole('button', { name: 'eBooks' }).click();
    assert.equal(await saveButton.isDisabled(), true);
    assert.equal(await page.getByText('Audiobook root folder', { exact: true }).count(), 0);
    availableRoots = roots;

    // A migrated unconfigured root keeps both scope flags false until an explicit choice.
    const noFormat = withField(withField(initialList, 'monitorAudiobooks', false), 'monitorEbooks', false);
    await render({ ...noFormat, monitoringOptions: { ...hiddenPolicies, addMissingAuthors: false } });
    assert.equal(await formats.locator('button[aria-pressed="true"]').count(), 0);
    assert.equal(await saveButton.isDisabled(), true);
    assert.equal(await page.getByRole('alert').getByText(strings.AuthorDeleteSelectFormat, { exact: true }).count(), 1);
    await noOptionsDraft();
    await formats.getByRole('button', { name: 'eBooks' }).click();
    assert.equal(await saveButton.isDisabled(), false);
    const selectedFormat = await save();
    assert.equal(selectedFormat.fields.find((item) => item.name === 'monitorAudiobooks').value, false);
    assert.equal(selectedFormat.fields.find((item) => item.name === 'monitorEbooks').value, true);

    // Both previously generic providers reuse the shared format selector and keep their connection fields.
    for (const implementation of ['ChaptarrImport', 'LazyLibrarianImport']) {
      await render({ ...initialList, implementation, monitoringOptions: { ...hiddenPolicies, addMissingAuthors: false } });
      await verifyGrouping(page, false);
      assert.equal(await formats.getByRole('button', { name: 'Audiobooks' }).count(), 1);
      assert.equal(await page.getByText('Remote URL', { exact: true }).count(), 1);
      assert.equal(await page.getByText(strings.RootFolder, { exact: true }).count(), 0);
      await formats.getByRole('button', { name: 'eBooks' }).click();
      await page.locator('input[name="addMissingAuthors"]').check({ force: true });
      assert.equal(await page.getByText('Ebook root folder', { exact: true }).count(), 1);
      assert.equal(await page.getByText('Audiobook root folder', { exact: true }).count(), 0);
    }

    await render({ ...dualList, implementation: 'HardcoverLibraryImportList' });
    await page.getByText(strings.ImportListHardcoverNarrationHelp, { exact: true }).waitFor();
    assert.equal(await page.getByText(strings.ImportListHardcoverFormatsHelp, { exact: true }).count(), 1);
    assert.equal(await page.getByText(strings.ImportListKeepHardcoverBooksHelp, { exact: true }).count(), 1);
    for (const theme of ['dark', 'light']) {
      await page.evaluate((value) => window.setListTheme(value), theme);
      for (const addingEnabled of [true, false]) {
        await page.locator('input[name="addMissingAuthors"]').setChecked(addingEnabled, { force: true });
        await page.locator('input[name="addMissingAuthors"]').scrollIntoViewIfNeeded();
        await verifyGrouping(page, addingEnabled);
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, 'No horizontal scrolling');
        if (process.env.CHAPTARR_LAYOUT_ARTIFACTS) {
          fs.mkdirSync(process.env.CHAPTARR_LAYOUT_ARTIFACTS, { recursive: true });
          await page.screenshot({ path: path.join(process.env.CHAPTARR_LAYOUT_ARTIFACTS, `import-list-${width}-${theme}-add-${addingEnabled ? 'on' : 'off'}.png`), fullPage: true });
          if (addingEnabled && theme === 'light') {
            await page.getByText('Audiobook root folder', { exact: true }).scrollIntoViewIfNeeded();
            await page.screenshot({ path: path.join(process.env.CHAPTARR_LAYOUT_ARTIFACTS, `import-list-${width}-${theme}-overrides.png`), fullPage: true });
          }
        }
      }
    }
    // Explicit bulk profile selections must reach the existing endpoint as numeric IDs.
    const bothList = withField(withField(withField({
      ...initialList, name: 'Both formats'
    }, 'monitorEbooks', true), 'ebookQualityProfileId', 3), 'ebookMetadataProfileId', 3);
    const ebookList = { ...withField(bothList, 'monitorAudiobooks', false), id: 8, name: 'Ebook only' };
    await page.evaluate((value) => window.renderManageLists(value.lists, value.catalogs), { lists: [bothList, ebookList], catalogs });
    await page.getByText(strings.ManageImportLists, { exact: true }).waitFor();
    const rows = page.locator('tbody tr');
    const check = rows.filter({ hasText: 'Both formats' }).locator('input[type="checkbox"]');
    await check.focus();
    await page.keyboard.press('Space');
    assert.equal(await check.isChecked(), true, 'Manage selection works with keyboard');
    await rows.filter({ hasText: 'Ebook only' }).locator('input[type="checkbox"]').check({ force: true });
    await page.getByRole('button', { name: strings.Edit, exact: true }).click();
    const editDialog = page.getByText(strings.EditSelectedImportLists, { exact: true }).locator('..').locator('..');
    await editDialog.locator('select[name="qualityProfileId"]').selectOption('2');
    bulkFailure = [{ propertyName: 'qualityProfileId', errorMessage: 'Ebook only: choose an existing quality profile compatible with an enabled format' }];
    await page.getByRole('button', { name: strings.ApplyChanges, exact: true }).click();
    await page.waitForFunction(() => !!window.manageListState().saveError);
    assert.equal(bulkWrites.length, 1, 'Apply sends one bulk request');
    assert.deepEqual(bulkWrites[0], { method: 'PUT', body: { ids: [1, 8], qualityProfileId: 2 } }, 'Bulk quality selection is sent as a number');
    assert.equal(await check.isChecked(), true, 'Rejected save keeps selection');
    assert.equal(await rows.count(), 2, 'Rejected save keeps the list table');
    assert.equal(await page.evaluate(() => window.manageListState().items[0].fields.find((item) => item.name === 'audiobookQualityProfileId').value), 1, 'Rejected save keeps persisted values');
    await page.getByRole('button', { name: strings.Edit, exact: true }).click();
    const editRetry = page.getByText(strings.EditSelectedImportLists, { exact: true }).locator('..').locator('..');
    await editRetry.locator('select[name="metadataProfileId"]').selectOption('1');
    bulkFailure = null;
    bulkResult = [withField(withField(bothList, 'audiobookMetadataProfileId', 1), 'ebookMetadataProfileId', 1), withField(ebookList, 'ebookMetadataProfileId', 1)];
    await page.getByRole('button', { name: strings.ApplyChanges, exact: true }).click();
    await page.waitForFunction(() => !window.manageListState().isSaving && !window.manageListState().saveError);
    assert.deepEqual(bulkWrites[1], { method: 'PUT', body: { ids: [1, 8], metadataProfileId: 1 } }, 'Bulk metadata selection is sent as a number');
    assert.equal(bulkWrites.length, 2);
    assert.equal(await page.evaluate(() => window.manageListState().items[0].fields.find((item) => item.name === 'ebookMetadataProfileId').value), 1, 'Successful response uses existing store update');
    assert.equal(await page.evaluate(() => window.manageListCloseCount), 0, 'Bulk actions never close Manage');
    await page.getByRole('button', { name: strings.Edit, exact: true }).click();
    const noChangeDialog = page.getByText(strings.EditSelectedImportLists, { exact: true }).locator('..').locator('..');
    await noChangeDialog.locator('select[name="qualityProfileId"]').selectOption('2');
    await noChangeDialog.locator('select[name="qualityProfileId"]').selectOption('noChange');
    await noChangeDialog.locator('select[name="metadataProfileId"]').selectOption('3');
    await noChangeDialog.locator('select[name="metadataProfileId"]').selectOption('noChange');
    await page.getByRole('button', { name: strings.ApplyChanges, exact: true }).click();
    assert.equal(bulkWrites.length, 2, 'No-change Apply does not submit a request');
    await page.getByRole('button', { name: strings.Edit, exact: true }).click();
    const unchangedProfiles = page.getByText(strings.EditSelectedImportLists, { exact: true }).locator('..').locator('..');
    await unchangedProfiles.locator('[class*="HintedSelectInputSelectedValue-valueText"]').first().click();
    await page.getByText(strings.Disabled, { exact: true }).click();
    bulkResult = bulkResult.map((item) => ({ ...item, enableAutomaticAdd: false }));
    await page.getByRole('button', { name: strings.ApplyChanges, exact: true }).click();
    await page.waitForFunction(() => !window.manageListState().isSaving && window.manageListState().items[0].enableAutomaticAdd === false);
    assert.deepEqual(bulkWrites[2], { method: 'PUT', body: { ids: [1, 8], enableAutomaticAdd: false } }, 'No-change profiles and root stay omitted from a real request');
    assert.deepEqual(errors, []);
    if (process.env.CHAPTARR_LAYOUT_ARTIFACTS) {
      fs.writeFileSync(path.join(process.env.CHAPTARR_LAYOUT_ARTIFACTS, `manage-wire-results-${width}.json`), JSON.stringify({ width, checks: 'numeric profile IDs and no-change guards', bulkWrites }, null, 2));
    }
  } finally {
    await page.close();
  }
}

(async() => {
  let browser = null;
  try {
    await compileFixture();
    browser = await chromium.launch();
    await verify(browser, 1440);
    await verify(browser, 375);
    console.log('Import-list browser checks passed at desktop and phone widths.');
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
