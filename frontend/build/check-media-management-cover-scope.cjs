/* Render the real media-management settings and confirmation flow in Chromium. */
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const webpack = require('webpack');
const { chromium } = require('playwright');
const createRouteCapture = require('./capture-browser-route.cjs');

const root = path.resolve(__dirname, '../..');
const appRootStyle = fs.readFileSync(path.join(root, 'frontend/src/index.ejs'), 'utf8').match(/<style>([\s\S]*?)<\/style>/)[1];
const output = fs.mkdtempSync(path.join(os.tmpdir(), 'chaptarr-cover-scope-ui-'));
const config = require('./webpack.config')({ production: true });

config.entry = { fixture: path.join(__dirname, 'media-management-fixture.js') };
config.output = { ...config.output, path: output, filename: 'fixture.js', publicPath: '/' };
config.optimization = { ...config.optimization, minimize: false, splitChunks: false, runtimeChunk: false };
config.plugins = config.plugins.filter((plugin) => ['DefinePlugin', 'MiniCssExtractPlugin'].includes(plugin.constructor.name));
for (const rule of config.module.rules) {
  if (Array.isArray(rule.use)) {
    rule.use = rule.use.filter((loader) => !String(typeof loader === 'string' ? loader : loader.loader).includes('css-modules-typescript-loader'));
  }
}
if (process.env.CHAPTARR_C18_BASELINE) {
  config.module.rules.unshift({ test: /\.js$/, enforce: 'pre', use: path.join(process.env.CHAPTARR_C18_BASELINE, 'before-source-loader.cjs') });
}
const emptyStub = path.join(__dirname, 'MediaManagementEmptyStub.js');
const toolbarStub = path.join(__dirname, 'MediaManagementToolbarStub.js');
config.plugins.push(new webpack.NormalModuleReplacementPlugin(/Connector$/, (resource) => {
  if (!resource.context.endsWith(path.join('Settings', 'MediaManagement'))) {
    return;
  }

  const request = path.basename(resource.request);
  if (request === 'SettingsToolbarConnector') {
    resource.request = toolbarStub;
  } else if (['NamingConnector', 'RootFoldersConnector', 'RemotePathMappingsConnector'].includes(request)) {
    resource.request = emptyStub;
  }
}));
config.plugins.push(new webpack.NormalModuleReplacementPlugin(/(?:SettingsToolbarConnector|BackupSettings|HostSettings|LoggingSettings|ProxySettings|SecuritySettings|UpdateSettings)$/, (resource) => {
  if (resource.context.endsWith(path.join('Settings', 'General'))) {
    resource.request = emptyStub;
  }
}));

function compileFixture() {
  const compiler = webpack(config);
  return new Promise((resolve, reject) => compiler.run((error, stats) => {
    compiler.close(() => {});
    if (error || stats.hasErrors()) {
      reject(error || new Error(stats.toString({ all: false, errors: true })));
    } else {
      console.log('Media-management cover-scope browser fixture compiled.');
      resolve();
    }
  }));
}

async function routeFixture(page, strings, cssPath) {
  const estimates = [];
  const estimateCapture = createRouteCapture('cover savings estimate');
  const settingsWrites = [];
  const rootWrites = [];
  const generalWrites = [];
  const proxyWrites = [];
  let resetReplyStatus = 'queued';
  let resetEarlyStatus = null;
  const heldConfigSaves = [];
  const heldRootSaves = [];
  const heldConfigFetches = [];
  let holdConfigFetches = false;
  let holdRootSaves = false;
  let holdProfiles = false;
  const heldProfiles = [];
  const catalogs = {
    quality: [
      { id: 11, name: 'Audio first', profileType: 'audiobook' }, { id: 12, name: 'Audio second', profileType: 'audiobook' },
      { id: 21, name: 'Ebook first', profileType: 'ebook' }, { id: 22, name: 'Ebook second', profileType: 'ebook' }
    ],
    metadata: [
      { id: 31, name: 'Audio metadata first', profileType: 1 }, { id: 32, name: 'Audio metadata second', profileType: 1 },
      { id: 39, name: 'None', profileType: 1 },
      { id: 41, name: 'Ebook metadata first', profileType: 2 }, { id: 42, name: 'Ebook metadata second', profileType: 2 },
      { id: 49, name: 'None', profileType: 2 }, { id: 50, name: 'Shared metadata', profileType: 0 }
    ]
  };
  const profiles = (pathname, media) => {
    const items = catalogs[pathname.endsWith('/qualityprofile') ? 'quality' : 'metadata'];
    return !media ? items : items.filter((p) => p.profileType === 0 || p.profileType === media || p.profileType === (media === 'ebook' ? 2 : 1));
  };
  const savedRoots = [
    { id: 1, name: 'Books', path: '/data/audiobooks', folderType: 'audiobook' },
    { id: 2, name: 'Other books', path: '/data/other', folderType: 'audiobook' }
  ];
  let holdConfigSaves = false;
  let savedGeneral = { id: 1, port: 8789, proxyMode: 'disabled', globalProxyId: null, logLevel: 'info' };
  let savedSettings = {
    bookMatchingStrictness: 'balanced', usePathAsTagsFallback: true,
    coverDownloadScope: 'allBooks', defaultAudiobookRootFolderPath: '/data/other',
    defaultEbookRootFolderPath: ''
  };
  await page.route('**/*', async(route) => {
    const requestUrl = new URL(route.request().url());
    const pathname = requestUrl.pathname;
    if (pathname.endsWith('/command') && route.request().method() === 'POST') {
      const body = route.request().postDataJSON();
      const command = { id: 803, name: body.name, status: resetReplyStatus, body };
      if (resetEarlyStatus) {
        await page.evaluate((value) => window.receiveSettingsCommand(value), { ...command, status: resetEarlyStatus });
      }
      return route.fulfill({ json: command });
    }
    if (pathname.endsWith('/qualityprofile') || pathname.endsWith('/metadataprofile')) {
      if (route.request().method() === 'POST') {
        const item = { ...route.request().postDataJSON(), id: 80 + catalogs.metadata.length };
        catalogs.metadata.push(item);
        return route.fulfill({ json: item });
      }
      const data = profiles(pathname, requestUrl.searchParams.get('mediaType'));
      if (holdProfiles) {
        heldProfiles.push({ route, data });
        await page.evaluate((count) => {
          window.fixtureProfileFetchCount = count;
        }, heldProfiles.length);
        return;
      }
      return route.fulfill({ json: data });
    }
    if (pathname.endsWith('/tag')) {
      return route.fulfill({ json: [{ id: 51, label: 'audio-tag' }, { id: 52, label: 'ebook-tag' }] });
    }
    if (pathname.endsWith('/config/mediamanagement/covercleanup')) {
      assert.equal(route.request().method(), 'GET', 'Estimating savings must be read-only');
      estimates.push(route);
      estimateCapture.capture(route);
      return;
    }
    if (route.request().method() === 'GET' && holdConfigFetches &&
        ['/config/mediamanagement', '/config/host'].some((suffix) => pathname.endsWith(suffix))) {
      heldConfigFetches.push({ route, general: pathname.endsWith('/config/host') });
      await page.evaluate((count) => {
        window.fixtureConfigFetchCount = count;
      }, heldConfigFetches.length);
      return;
    }
    if (pathname.endsWith('/config/mediamanagement')) {
      if (route.request().method() === 'PUT') {
        const data = route.request().postDataJSON();
        settingsWrites.push(data);
        if (holdConfigSaves) {
          heldConfigSaves.push({ route, data, resource: 'mediaManagement' });
          await page.evaluate((count) => {
            window.fixtureConfigSaveCount = count;
          }, heldConfigSaves.length);
          return;
        }
        savedSettings = data;
      }
      return route.fulfill({ json: savedSettings });
    }
    if ((/\/rootfolder(?:\/\d+)?$/i).test(pathname)) {
      if (['POST', 'PUT'].includes(route.request().method())) {
        const data = route.request().postDataJSON();
        const id = Number(pathname.match(/\/(\d+)$/)?.[1]) || 3;
        rootWrites.push({ ...data, method: route.request().method(), id });
        if (holdRootSaves) {
          heldRootSaves.push({ route, data: { ...data, id } });
          await page.evaluate((count) => {
            window.fixtureRootSaveCount = count;
          }, heldRootSaves.length);
          return;
        }
        const index = savedRoots.findIndex((item) => item.id === id);
        if (index === -1) {
          savedRoots.push({ ...data, id });
        } else {
          savedRoots[index] = { ...data, id };
        }
        return route.fulfill({ json: { ...data, id } });
      }
      return route.fulfill({ json: savedRoots });
    }
    if (pathname.endsWith('/config/host')) {
      if (route.request().method() === 'PUT') {
        const data = route.request().postDataJSON();
        generalWrites.push(data);
        if (holdConfigSaves) {
          heldConfigSaves.push({ route, data, resource: 'general' });
          await page.evaluate((count) => {
            window.fixtureConfigSaveCount = count;
          }, heldConfigSaves.length);
          return;
        }
        savedGeneral = data;
      }
      return route.fulfill({ json: savedGeneral });
    }
    if (pathname.endsWith('/settings/proxy/test')) {
      return route.fulfill({ json: { isValid: true } });
    }
    if ((/\/settings\/proxy(?:\/\d+)?$/).test(pathname)) {
      if (['POST', 'PUT'].includes(route.request().method())) {
        const proxy = { ...route.request().postDataJSON(), id: 7, method: route.request().method() };
        proxyWrites.push(proxy);
        return route.fulfill({ json: proxy });
      }
      return route.fulfill({ json: proxyWrites });
    }
    if (pathname.endsWith('/filesystem')) {
      return route.fulfill({ json: { parent: null, directories: [], files: [] } });
    }
    if (pathname.endsWith('/queue')) {
      return route.fulfill({ json: { records: [], totalRecords: 0, page: 1, pageSize: 20 } });
    }
    if (pathname.endsWith('/queue/status')) {
      return route.fulfill({ json: { totalCount: 0, unknownCount: 0 } });
    }
    if (pathname.endsWith('/author') || pathname.endsWith('/book') || pathname.endsWith('/manualimport')) {
      return route.fulfill({ json: [] });
    }
    if (pathname.endsWith('/log/file') || pathname.endsWith('/downloadclient')) {
      return route.fulfill({ json: [] });
    }
    if (pathname.endsWith('/localization')) {
      return route.fulfill({ json: { Strings: strings } });
    }
    if (pathname === '/fixture.js') {
      return route.fulfill({ path: path.join(output, 'fixture.js'), contentType: 'text/javascript' });
    }
    if (pathname === '/style.css') {
      return route.fulfill({ path: cssPath, contentType: 'text/css' });
    }
    if (pathname === '/') {
      return route.fulfill({
        contentType: 'text/html',
        body: `<!doctype html><html><head><meta name="viewport" content="width=device-width, initial-scale=1"><link rel="stylesheet" href="/style.css"><style>${appRootStyle}</style></head><body><div id="root" class="root"></div><div id="portal-root"></div><script>window.Chaptarr={isProduction:true,apiRoot:"/api/v1",instanceName:"Chaptarr"};</script><script src="/fixture.js"></script></body></html>`
      });
    }

    const assetPath = path.resolve(output, `.${decodeURIComponent(pathname)}`);
    if (assetPath.startsWith(`${output}${path.sep}`) && fs.existsSync(assetPath) && fs.statSync(assetPath).isFile()) {
      return route.fulfill({ path: assetPath });
    }

    return route.fulfill({ status: 204, body: '' });
  });
  return {
    seedRoot: (overrides) => Object.assign(savedRoots[0], overrides),
    holdProfiles: () => {
      holdProfiles = true;
    },
    completeProfiles: async() => {
      holdProfiles = false;
      for (const held of heldProfiles) {
        await held.route.fulfill({ json: held.data });
      }
    },
    heldProfiles,
    estimates,
    nextEstimate: () => estimateCapture.next(),
    settingsWrites,
    rootWrites,
    generalWrites,
    proxyWrites,
    heldConfigSaves,
    heldRootSaves,
    holdConfigFetches: () => {
      holdConfigFetches = true;
    },
    async completeConfigFetch(index, status = 200) {
      await page.waitForFunction((count) => window.fixtureConfigFetchCount > count, index);
      const held = heldConfigFetches[index];
      const settings = held.general ? savedGeneral : savedSettings;
      await held.route.fulfill({ status, json: status < 400 ? settings : { message: 'Settings load failed' } });
    },
    holdRootSaves: () => {
      holdRootSaves = true;
    },
    async completeRootSave(index, status = 200, message = 'Root save failed') {
      await page.waitForFunction((count) => window.fixtureRootSaveCount > count, index);
      const held = heldRootSaves[index];
      if (status < 400) {
        const savedIndex = savedRoots.findIndex((item) => item.id === held.data.id);
        if (savedIndex === -1) {
          savedRoots.push(held.data);
        } else {
          savedRoots[savedIndex] = held.data;
        }
      }
      const failure = Array.isArray(message) ? message : [{ message }];
      await held.route.fulfill({ status, json: status < 400 ? held.data : failure });
    },
    setResetReply: (status, earlyStatus = null) => {
      resetReplyStatus = status;
      resetEarlyStatus = earlyStatus;
    },
    holdConfigSaves: () => {
      holdConfigSaves = true;
    },
    async completeConfigSave(index, status = 200, responseData) {
      await page.waitForFunction((count) => window.fixtureConfigSaveCount > count, index);
      const held = heldConfigSaves[index];
      const data = responseData || held.data;
      if (status < 400) {
        if (held.resource === 'mediaManagement') {
          savedSettings = data;
        } else {
          savedGeneral = data;
        }
      }
      await held.route.fulfill({ status, json: data });
    }
  };
}

async function openFixture(browser, strings, cssPath, options = {}) {
  const context = await browser.newContext(options);
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', (error) => errors.push(error.message));
  page.on('console', (message) => {
    if (message.type() === 'error') {
      errors.push(message.text());
    }
  });
  const requests = await routeFixture(page, strings, cssPath);
  await page.goto('http://media-management.test/');
  await page.waitForFunction(() => window.fixtureReady && window.renderMediaManagementFixture);
  await page.waitForTimeout(100);
  if (!(await page.getByText(strings.CoverDownloadSettings, { exact: true }).count())) {
    console.error('Fixture did not render settings content:', await page.locator('body').innerText(), errors);
  }
  return { context, page, errors, ...requests };
}

async function verifySaveFailures(browser, strings, cssPath) {
  const results = [];
  const frenchReload = JSON.parse(fs.readFileSync(path.join(root, 'src/NzbDrone.Core/Localization/Core/fr.json'))).Reload;
  const run = async(name, check, width = 1280, localeStrings = strings) => {
    console.log(`C18.6 starting ${name}`);
    const fixture = await openFixture(browser, localeStrings, cssPath, { viewport: { width, height: 900 } });
    fixture.page.setDefaultTimeout(3000);
    try {
      await check(fixture);
      results.push({ name, passed: true, errors: fixture.errors });
      console.log(`C18.6 passed ${name}`);
    } catch (error) {
      results.push({ name, passed: false, message: error.message, errors: fixture.errors });
      console.log(`C18.6 failed ${name}: ${error.message}`);
    } finally {
      await fixture.context.close();
    }
  };
  await run('reverted-full-page-failure', async(fixture) => {
    const { page } = fixture;
    await page.evaluate(() => window.renderSettingsIsolationFixture());
    await page.waitForFunction(() => window.getSettingsState()?.isPopulated);
    fixture.holdConfigSaves();
    await page.evaluate(() => {
      window.setSettingsDraft('defaultAudiobookRootFolderPath', '/data/rejected');
      window.saveSettings({}, false);
    });
    await fixture.completeConfigSave(0, 500, { message: 'Full-page failure' });
    await page.waitForFunction(() => !window.getSettingsState().isSaving);
    await page.evaluate(() => {
      window.saveSettings({ defaultEbookRootFolderPath: '/data/ebooks' });
    });
    await fixture.completeConfigSave(1);
    await page.waitForFunction(() => !window.getSettingsState().isSaving);
    assert.equal(await page.evaluate(() => window.getSettingsState().saveError.responseJSON.message), 'Full-page failure', 'A still-pending failed field keeps its error');
    await page.evaluate(() => {
      window.setSettingsDraft('defaultAudiobookRootFolderPath', '/data/other');
      window.saveSettings({ coverDownloadScope: 'libraryBooks' });
      window.setSettingsDraft('defaultEbookRootFolderPath', '/data/newer-edit');
    });
    await fixture.completeConfigSave(2);
    await page.waitForFunction(() => !window.getSettingsState().isSaving);
    assert.equal(await page.evaluate(() => !!window.getSettingsState().saveError), false, 'Reverted failed fields cannot keep an unrelated successful save in error');
    assert.deepEqual(await page.evaluate(() => window.getSettingsDraft()), { defaultEbookRootFolderPath: '/data/newer-edit' });
  });
  await run('restart-from-saved-values', async(fixture) => {
    const { page } = fixture;
    await page.evaluate(() => window.renderSettingsIsolationFixture('general-settings'));
    await page.waitForFunction(() => window.getGeneralSettingsState()?.isPopulated);
    fixture.holdConfigSaves();
    await page.evaluate(() => {
      window.setGeneralDraft('port', 9999);
      window.saveGeneralSetting({ logLevel: 'debug' });
    });
    await fixture.completeConfigSave(0);
    await page.waitForFunction(() => !window.getGeneralSettingsState().isSaving);
    assert.equal(await page.getByRole('button', { name: strings.RestartNow, exact: true }).count(), 0, 'An unsaved port draft cannot prompt a restart after a log-only save');
    assert.equal(fixture.generalWrites[0].port, 8789);
    assert.deepEqual(await page.evaluate(() => window.getGeneralDraft()), { port: 9999 });
    await page.evaluate(() => {
      window.saveGeneralSetting({}, false);
      window.setGeneralDraft('port', 7777);
    });
    await fixture.completeConfigSave(1);
    await page.getByRole('button', { name: strings.RestartNow, exact: true }).waitFor();
    assert.deepEqual(await page.evaluate(() => window.getGeneralDraft()), { port: 7777 }, 'A later edit remains pending while the saved port triggers restart');
  });
  await run('queued-general-restart', async(fixture) => {
    const { page } = fixture;
    await page.evaluate(() => window.renderSettingsIsolationFixture('general-settings'));
    await page.waitForFunction(() => window.getGeneralSettingsState()?.isPopulated);
    fixture.holdConfigSaves();
    await page.evaluate(() => {
      window.setGeneralDraft('port', 9999);
      window.saveGeneralSetting({}, false);
      window.saveGeneralSetting({ logLevel: 'debug' });
    });
    await fixture.completeConfigSave(0);
    await page.waitForFunction(() => window.fixtureConfigSaveCount === 2);
    assert.equal(fixture.generalWrites.length, 2);
    assert.equal(fixture.generalWrites[1].port, 9999, 'The immediately following save uses the accepted port');
    assert.equal(await page.evaluate(() => window.getGeneralSettingsState().isSaving), true);
    await fixture.completeConfigSave(1);
    await page.waitForFunction(() => !window.getGeneralSettingsState().isSaving);
    assert.equal(await page.getByRole('button', { name: strings.RestartNow, exact: true }).isVisible(), true,
      'The port restart survives the complete queued port and unrelated save sequence');
  });
  for (const width of [1280, 375]) {
    const rootStrings = width === 375 ? { ...strings, Reload: frenchReload } : strings;
    await run(`root-load-retry-${width}`, async(fixture) => {
      const { page } = fixture;
      fixture.holdConfigFetches();
      await page.evaluate(() => window.renderSettingsIsolationFixture());
      await fixture.completeConfigFetch(0, 500);
      await page.waitForFunction(() => !window.getSettingsState().isFetching);
      await page.getByRole('button', { name: 'Edit default root folder', exact: true }).click();
      await fixture.completeConfigFetch(1, 500);
      const modal = page.locator('[class*="ModalContent-modalContent"]');
      await modal.getByRole('alert').filter({ hasText: strings.UnableToLoadMediaManagementSettings }).waitFor();
      const save = modal.getByRole('button', { name: 'Update Audiobook Folder', exact: true });
      assert.equal(await save.isDisabled(), true);
      await modal.locator('input[name="name"]').fill('Retained root draft');
      await page.evaluate((theme) => window.setFixtureTheme(theme), width === 375 ? 'dark' : 'light');
      if (process.env.CHAPTARR_C18_SCREENSHOTS) {
        await modal.screenshot({ path: path.join(process.env.CHAPTARR_C18_SCREENSHOTS, `root-load-error-${width}.png`) });
      }
      await modal.getByRole('button', { name: rootStrings.Reload, exact: true }).focus();
      await page.keyboard.press('Enter');
      await page.waitForFunction(() => window.getSettingsState().isFetching);
      assert.equal(await save.isDisabled(), true);
      await fixture.completeConfigFetch(2);
      await page.waitForFunction(() => window.getSettingsState().isPopulated && !window.getSettingsState().isFetching);
      assert.equal(await modal.getByRole('alert').count(), 0);
      assert.equal(await modal.locator('input[name="name"]').inputValue(), 'Retained root draft');
      assert.equal(await save.isDisabled(), false);
      assert.equal(fixture.rootWrites.length + fixture.settingsWrites.length, 0, 'Retry only reads settings');
      const bounds = await modal.boundingBox();
      assert.ok(bounds.x >= 0 && bounds.x + bounds.width <= width, 'The modal stays within the viewport');
    }, width, rootStrings);
  }
  for (const [surface, field, value, width] of [
    ['queue', 'autoAddMissingAuthorsFromCompletedDownloads', true, 1280],
    ['interactive', 'usePathAsTagsFallback', false, 1280],
    ['folder', 'usePathAsTagsFallback', false, 375]
  ]) {
    await run(`visible-quick-failure-${surface}`, async(fixture) => {
      const { page } = fixture;
      await page.evaluate((name) => window.renderSettingsIsolationFixture(name), surface);
      await page.waitForFunction(() => window.getSettingsState()?.isPopulated);
      fixture.holdConfigSaves();
      const toggle = surface === 'queue' ? page.getByRole('button', { name: strings.AutoAddAuthors, exact: true }) : page.locator('input[name="usePathAsTagsFallback"]').locator('..');
      const press = async() => {
        await toggle.click();
        if (surface === 'queue') {
          await page.getByRole('button', { name: strings.Enable, exact: true }).click();
        }
      };
      await press();
      await page.evaluate(() => window.setSettingsDraft('defaultAudiobookRootFolderPath', '/data/newer-edit'));
      await fixture.completeConfigSave(0, 500, { message: 'Quick setting failure' });
      await page.getByRole('alert').filter({ hasText: 'Quick setting failure' }).waitFor();
      const errorBounds = await page.getByRole('alert').filter({ hasText: 'Quick setting failure' }).boundingBox();
      assert.ok(errorBounds.y >= 0 && errorBounds.y + errorBounds.height <= 900, 'The request error is visible within the viewport');
      if (process.env.CHAPTARR_C18_SCREENSHOTS) {
        await page.screenshot({ path: path.join(process.env.CHAPTARR_C18_SCREENSHOTS, `quick-failure-${surface}-${width}.png`) });
      }
      assert.equal(await page.evaluate((name) => window.getSettingsState().item[name], field), surface === 'queue' ? undefined : !value);
      assert.deepEqual(await page.evaluate(() => window.getSettingsDraft()), { defaultAudiobookRootFolderPath: '/data/newer-edit' });
      await page.evaluate(() => {
        window.saveSettings({ coverDownloadScope: 'libraryBooks' });
      });
      await fixture.completeConfigSave(1);
      await page.waitForFunction(() => !window.getSettingsState().isSaving);
      assert.equal(await page.getByRole('alert').filter({ hasText: 'Quick setting failure' }).isVisible(), true, 'An unrelated success does not dismiss this surface request failure');
      await press();
      await fixture.completeConfigSave(2);
      await page.waitForFunction((name) => window.getSettingsState().item[name] !== undefined && !window.getSettingsState().isSaving, field);
      assert.equal(await page.getByRole('alert').filter({ hasText: 'Quick setting failure' }).count(), 0);
      assert.equal(await page.evaluate((name) => window.getSettingsState().item[name], field), value);
      assert.deepEqual(await page.evaluate(() => window.getSettingsDraft()), { defaultAudiobookRootFolderPath: '/data/newer-edit' });
    }, width);
  }
  for (const failed of [false, true]) {
    await run(`save-observer-${failed ? 'failure' : 'success'}`, async(fixture) => {
      const { page } = fixture;
      await page.evaluate(() => window.renderSettingsIsolationFixture());
      await page.waitForFunction(() => window.getSettingsState()?.isPopulated);
      fixture.holdConfigSaves();
      await page.evaluate(() => window.runSaveObserverControl());
      await fixture.completeConfigSave(0, failed ? 500 : 200, failed ? { message: 'Request failed' } : undefined);
      await page.waitForFunction(() => window.observerSaveResults.length > 0, null, { timeout: 3000 });
      await fixture.completeConfigSave(1);
      await page.waitForFunction(() => window.observerSaveResults.includes('next:done'));
      assert.deepEqual(await page.evaluate(() => window.observerSaveResults), [failed ? 'first:fail' : 'first:done', 'next:done']);
      assert.equal(await page.evaluate(() => window.observerDispatchErrors), 1, 'The completion observer actually threw');
    });
  }
  await run('construction-error-xhr-shape', async(fixture) => {
    const { page } = fixture;
    await page.evaluate(() => window.renderSettingsIsolationFixture());
    await page.waitForFunction(() => window.getSettingsState()?.isPopulated);
    await page.evaluate(() => {
      const original = window.XMLHttpRequest;
      window.XMLHttpRequest = function() {
        throw new Error('Request construction failed');
      };
      window.saveSettings({ defaultEbookRootFolderPath: '/data/retry' });
      window.XMLHttpRequest = original;
    });
    const error = await page.evaluate(() => window.getSettingsState().saveError);
    assert.equal(error.status, 0);
    assert.equal(error.responseJSON.message, 'Request construction failed');
    assert.equal(await page.evaluate(() => window.getSettingsState().isSaving), false);
  });
  console.log(JSON.stringify({ suite: 'C18.6 causal browser controls', baseline: !!process.env.CHAPTARR_C18_BASELINE, results }, null, 2));
  assert.equal(results.filter((result) => !result.passed).length, 0, 'All C18.6 controls must pass');
}

async function verifyProfilePayloads(browser, strings, cssPath) {
  const results = [];
  const run = async(name, check) => {
    if (process.env.CHAPTARR_PROFILE_CASE && !new RegExp(process.env.CHAPTARR_PROFILE_CASE).test(name)) {
      return;
    }
    const fixture = await openFixture(browser, strings, cssPath, { viewport: { width: 1280, height: 1000 } });
    fixture.page.setDefaultTimeout(10000);
    try {
      const observation = await check(fixture);
      results.push({ name, passed: true, observation });
    } catch (error) {
      results.push({ name, passed: false, error: error.message });
    } finally {
      await fixture.context.close();
    }
  };
  const render = async(page, props) => {
    await page.evaluate((data) => window.renderProfileContractFixture(data), props);
    await page.locator('select[name="quality"] option[value="12"]').waitFor({ state: 'attached' });
    await page.locator('select[name="metadata"] option[value="32"]').waitFor({ state: 'attached' });
    if (props.quality == null && props.metadata == null) {
      await page.waitForFunction(() => window.profileChanges.length === 2);
    }
  };
  for (const name of ['quality', 'metadata']) {
    await run(`manual ${name} emits numeric ID`, async({ page }) => {
      await render(page, { quality: 11, metadata: 31, isDisabled: false });
      await page.locator(`select[name="${name}"]`).selectOption(name === 'quality' ? '12' : '32');
      const changes = await page.evaluate(() => window.profileChanges);
      assert.equal(changes.at(-1).type, 'number');
      assert.equal(changes.at(-1).value, name === 'quality' ? 12 : 32);
      return changes;
    });
  }
  for (const value of [999, 42]) {
    await run(`missing or other-format saved profile ${value} is never replaced`, async({ page }) => {
      await render(page, { quality: value, metadata: value });
      assert.deepEqual(await page.evaluate(() => window.profileChanges), []);
      assert.equal(await page.locator('select[name="metadata"]').inputValue(), String(value));
    });
  }
  await run('a saved clone appears in an already open dropdown', async({ page }) => {
    await render(page, { quality: 11, metadata: 31 });
    await page.evaluate(() => window.cloneMetadataProfile(31));
    await page.locator('select[name="metadata"] option[value="87"]').waitFor({ state: 'attached' });
    await page.locator('select[name="metadata"]').selectOption('87');
    assert.equal(await page.locator('select[name="metadata"]').inputValue(), '87');
    assert.equal((await page.evaluate(() => window.profileChanges)).at(-1).value, 87);
  });
  await run('persisted null tags do not crash the tag control', async({ page, errors }) => {
    await render(page, { quality: 11, metadata: 31 });
    await page.evaluate(() => window.renderNullTags());
    assert.equal(errors.length, 0, errors.join('\n'));
  });
  await run('Add Book sends selected profiles without invented IDs', async({ page }) => {
    await render(page, { quality: 11, metadata: 31 });
    const absent = await page.evaluate(() => window.getAddBookPayload({}));
    assert.equal(absent.audiobookMetadataProfileId, undefined);
    assert.equal(absent.ebookMetadataProfileId, undefined);
    const chosen = await page.evaluate(() => window.getAddBookPayload({ audiobookMetadataProfileId: { value: 31 }, ebookMetadataProfileId: { value: 42 } }));
    assert.equal(chosen.audiobookMetadataProfileId, 31);
    assert.equal(chosen.ebookMetadataProfileId, 42);
  });
  for (const create of [false, true]) {
    await run(`${create ? 'new' : 'cloned'} metadata profile saves from the root dialog without reload`, async({ page, rootWrites }) => {
      await page.evaluate(() => window.renderSettingsIsolationFixture());
      await page.waitForFunction(() => window.getMetadataProfiles().isPopulated);
      await page.evaluate((isNew) => (isNew ? window.createMetadataProfile(1) : window.cloneMetadataProfile(31)), create);
      await page.waitForFunction(() => window.getMetadataProfiles().items.some((p) => p.id === 87));
      await page.getByRole('button', { name: 'Edit root folder', exact: true }).click();
      const dialog = page.locator('[class*="ModalContent-modalContent"]');
      await dialog.locator('select[name="audiobookMetadataProfileId"]').selectOption('87');
      if (process.env.CHAPTARR_PROFILE_SCREENSHOTS) {
        await page.setViewportSize({ width: create ? 1280 : 375, height: 1000 });
        await dialog.locator('select[name="audiobookMetadataProfileId"]').scrollIntoViewIfNeeded();
        fs.mkdirSync(process.env.CHAPTARR_PROFILE_SCREENSHOTS, { recursive: true });
        await page.screenshot({ path: path.join(process.env.CHAPTARR_PROFILE_SCREENSHOTS, create ? 'root-desktop.png' : 'root-mobile.png') });
      }
      await dialog.getByRole('button', { name: 'Update Audiobook Folder', exact: true }).click();
      await page.waitForFunction(() => window.rootCompletions === 1);
      assert.equal(rootWrites[0].audiobookMetadataProfileId, 87);
    });
  }
  await run('automatic initial defaults remain numeric', async({ page }) => {
    await render(page, { isDisabled: false });
    const changes = await page.evaluate(() => window.profileChanges);
    assert.deepEqual(changes.map(({ value }) => value).sort((a, b) => a - b), [11, 31]);
    assert(changes.every(({ type }) => type === 'number'));
    return changes;
  });
  for (const value of ['noChange', 'mixed']) {
    await run(`${value} initialization is retained`, async({ page }) => {
      await render(page, { quality: value, metadata: value, isDisabled: false });
      assert.equal(await page.locator('select[name="quality"]').inputValue(), value);
      assert.equal(await page.locator('select[name="metadata"]').inputValue(), value);
      assert.deepEqual(await page.evaluate(() => window.profileChanges), []);
      if (value === 'mixed') {
        assert.equal(await page.locator('select[name="quality"] option[value="mixed"]').isDisabled(), true);
        assert.equal(await page.locator('select[name="metadata"] option[value="mixed"]').isDisabled(), true);
      }
      return { value, changes: [] };
    });
  }
  await run('explicit noChange selection retains sentinel', async({ page }) => {
    await render(page, { quality: 11, metadata: 31, isDisabled: false });
    await page.locator('select[name="quality"]').selectOption('noChange');
    await page.locator('select[name="metadata"]').selectOption('noChange');
    const changes = await page.evaluate(() => window.profileChanges);
    assert.deepEqual(changes.map(({ value }) => value), ['noChange', 'noChange']);
    return changes;
  });
  await run('None keeps quality sentinel and numeric metadata identity', async({ page }) => {
    await render(page, { quality: 'none', metadata: 39, includeNone: true, isDisabled: false });
    assert.equal(await page.locator('select[name="quality"]').inputValue(), 'none');
    assert.equal(await page.locator('select[name="metadata"]').inputValue(), '39');
    assert.deepEqual(await page.evaluate(() => window.profileChanges), []);
    await page.locator('select[name="quality"]').selectOption('12');
    await page.locator('select[name="quality"]').selectOption('none');
    await page.locator('select[name="metadata"]').selectOption('32');
    await page.locator('select[name="metadata"]').selectOption('39');
    const changes = await page.evaluate(() => window.profileChanges);
    assert.equal(changes[1].value, 'none');
    assert.equal(changes[3].value, 39);
    assert.equal(changes[3].type, 'number');
    return changes;
  });
  await run('caller disabled remains effective during and after fetch', async(fixture) => {
    const { page } = fixture;
    fixture.holdProfiles();
    await page.evaluate(() => window.renderProfileContractFixture({ quality: 11, metadata: 31, isDisabled: true }));
    await page.waitForFunction(() => window.fixtureProfileFetchCount === 2);
    assert.equal(await page.locator('select[name="quality"]').isDisabled(), true);
    assert.equal(await page.locator('select[name="metadata"]').isDisabled(), true);
    await fixture.completeProfiles();
    await page.locator('select[name="quality"] option[value="12"]').waitFor({ state: 'attached' });
    await page.locator('select[name="metadata"] option[value="32"]').waitFor({ state: 'attached' });
    assert.equal(await page.locator('select[name="quality"]').isDisabled(), true);
    assert.equal(await page.locator('select[name="metadata"]').isDisabled(), true);
    return { qualityDisabled: true, metadataDisabled: true };
  });
  await run('fetching disables even when caller says enabled', async(fixture) => {
    fixture.holdProfiles();
    await fixture.page.evaluate(() => window.renderProfileContractFixture({ quality: 11, metadata: 31, isDisabled: false }));
    await fixture.page.waitForFunction(() => window.fixtureProfileFetchCount === 2);
    assert.equal(fixture.heldProfiles.length, 2);
    const observed = { qualityDisabled: await fixture.page.locator('select[name="quality"]').isDisabled(), metadataDisabled: await fixture.page.locator('select[name="metadata"]').isDisabled() };
    await fixture.completeProfiles();
    assert.equal(observed.qualityDisabled, true);
    assert.equal(observed.metadataDisabled, true);
    return observed;
  });
  for (const folderType of [1, 2, 0]) {
    await run(`root folder type ${folderType} manual IDs survive actual JSON save`, async(fixture) => {
      fixture.seedRoot({ folderType });
      await fixture.page.evaluate((type) => window.renderSettingsIsolationFixture('quickstart', { folderType: type }), folderType);
      await fixture.page.getByRole('button', { name: 'Edit root folder', exact: true }).click();
      const dialog = fixture.page.locator('[class*="ModalContent-modalContent"]');
      const sides = folderType === 0 ? ['audiobook', 'ebook'] : [folderType === 1 ? 'audiobook' : 'ebook'];
      for (const side of sides) {
        if (side === 'ebook' && folderType === 0) {
          await dialog.getByRole('button', { name: /eBooks/ }).click();
        }
        const quality = dialog.locator(`select[name="${side}QualityProfileId"]`);
        const metadata = dialog.locator(`select[name="${side}MetadataProfileId"]`);
        await quality.selectOption(side === 'audiobook' ? '12' : '22');
        await metadata.selectOption(side === 'audiobook' ? '32' : '42');
      }
      const state = await fixture.page.evaluate(() => window.getRootFolderState());
      await dialog.getByRole('button', { name: /^Update (Audiobook|Ebook|Mixed) Folder$/ }).click();
      await fixture.page.waitForFunction(() => window.rootCompletions === 1);
      assert.equal(fixture.rootWrites.length, 1);
      const payload = fixture.rootWrites[0];
      const observed = { state, payload };
      for (const side of sides) {
        assert.equal(payload[`${side}QualityProfileId`], side === 'audiobook' ? 12 : 22, JSON.stringify(observed));
        assert.equal(payload[`${side}MetadataProfileId`], side === 'audiobook' ? 32 : 42, JSON.stringify(observed));
      }
      assert.equal(payload.folderType, folderType);
      if (folderType !== 0) {
        const other = folderType === 1 ? 'ebook' : 'audiobook';
        assert.equal(payload[`${other}QualityProfileId`], null);
        assert.equal(payload[`${other}MetadataProfileId`], null);
      }
      return observed;
    });
  }
  console.log(JSON.stringify({ suite: 'modal-profile-payload-browser', results }, null, 2));
  assert.equal(results.filter(({ passed }) => !passed).length, 0, 'Profile payload controls must pass');
}

async function verifyUiGrabbag(browser, strings, cssPath) {
  for (const width of [1280, 375]) {
    for (const surface of ['interactive', 'logs']) {
      const fixture = await openFixture(browser, strings, cssPath, { viewport: { width, height: 900 } });
      const { page } = fixture;
      try {
        fixture.holdConfigFetches();
        await page.evaluate((name) => window.renderSettingsIsolationFixture(name), surface);
        const control = surface === 'interactive' ? page.locator('input[name="usePathAsTagsFallback"]') :
          page.getByRole('button', { name: 'Info', exact: true });
        await control.waitFor();
        assert.equal(await control.isDisabled(), true, 'No field-only save before GET succeeds');
        await fixture.completeConfigFetch(0, 500);
        await page.waitForFunction((name) => !(name === 'interactive' ? window.getSettingsState() : window.getGeneralSettingsState()).isFetching, surface);
        assert.equal(await control.isDisabled(), true, 'Failed loads stay disabled');
        assert.equal(fixture.settingsWrites.length + fixture.generalWrites.length, 0);
        await page.getByRole('button', { name: surface === 'interactive' ? strings.Retry : strings.Refresh, exact: true }).click();
        await fixture.completeConfigFetch(1);
        await page.waitForFunction((name) => (name === 'interactive' ? window.getSettingsState() : window.getGeneralSettingsState()).isPopulated, surface);
        assert.equal(await control.isEnabled(), true, 'Retry restores editing');
        if (surface === 'interactive') {
          await control.focus();
          await page.keyboard.press('Space');
          await page.waitForFunction(() => !window.getSettingsState().isSaving && window.getSettingsState().item.usePathAsTagsFallback === false);
          assert.equal(fixture.settingsWrites.length, 1);
          assert.equal(fixture.settingsWrites[0].coverDownloadScope, 'allBooks');
        } else {
          await control.click();
          await page.getByText('Debug', { exact: true }).click();
          await page.waitForFunction(() => window.getGeneralSettingsState().item.logLevel === 'debug');
          assert.equal(fixture.generalWrites.length, 1);
          assert.equal(fixture.generalWrites[0].port, 8789);
        }
        assert.equal(fixture.errors.filter((error) => !error.startsWith('Failed to load resource:')).length, 0, fixture.errors.join('\n'));
      } finally {
        await fixture.context.close();
      }
    }
    const fixture = await openFixture(browser, strings, cssPath, { viewport: { width, height: 900 } });
    try {
      const { page } = fixture;
      await page.evaluate(() => window.renderSettingsIsolationFixture());
      await page.waitForFunction(() => window.getSettingsState().isPopulated);
      await page.getByRole('button', { name: 'Edit root folder', exact: true }).click();
      const modal = page.locator('[class*="ModalContent-modalContent"]');
      const save = modal.getByRole('button', { name: 'Update Audiobook Folder', exact: true });
      await page.locator('input[name="name"]').fill('Changed root name');
      fixture.holdRootSaves();
      await save.click();
      await fixture.completeRootSave(0, 400, [{ propertyName: 'Path', errorMessage: 'Zugriff verweigert', errorCode: 'RootFolderNotWritable' }]);
      await page.getByText(strings.RootFolderDockerPermissionHelp, { exact: true }).waitFor();
      assert.equal(await save.isEnabled(), true);
      await save.click();
      await fixture.completeRootSave(1, 400, 'Unrelated permission policy validation failure');
      await page.getByText('Unrelated permission policy validation failure', { exact: true }).waitFor();
      assert.equal(await page.getByText(strings.RootFolderDockerPermissionHelp, { exact: true }).count(), 0);
    } finally {
      await fixture.context.close();
    }
  }
  console.log('PASS: Step75 field-only load/failure/retry/save guards and permission-only Docker help, desktop/mobile');
}

(async() => {
  await compileFixture();
  const cssName = fs.readdirSync(path.join(output, 'Content')).find((name) => name.endsWith('.css'));
  const cssPath = path.join(output, 'Content', cssName);
  const strings = JSON.parse(fs.readFileSync(path.join(root, 'src/NzbDrone.Core/Localization/Core/en.json')));
  const browser = await chromium.launch();
  try {
    if (process.env.CHAPTARR_UI_GRABBAG_ONLY) {
      await verifyUiGrabbag(browser, strings, cssPath);
      return;
    }
    await verifyProfilePayloads(browser, strings, cssPath);
    if (process.env.CHAPTARR_PROFILE_PAYLOAD_ONLY) {
      return;
    }
    await verifySaveFailures(browser, strings, cssPath);
    const desktop = await openFixture(browser, strings, cssPath, { viewport: { width: 1280, height: 900 } });
    const { page, estimates, nextEstimate } = desktop;
    const calculate = page.getByRole('button', { name: strings.CalculateCoverSavings, exact: true });
    const summary = strings.CoverCleanupEstimate.replace('{size}', '2 GiB');
    const dialog = page.locator('[class*="ModalContent-modalContent"]');
    assert.equal(await calculate.isVisible(), true, 'Calculate savings must be available before changing or saving settings');
    assert.equal(estimates.length, 0, 'Opening settings must not scan the cache');
    assert.equal(await page.getByRole('button', { name: strings.CoverDownloadScopeAllBooks, exact: true }).count(), 1);
    assert.equal(await page.getByText(strings.CoverDownloadScopeAllBooksHelpText, { exact: true }).isVisible(), true, 'All books must explain when covers download');

    await page.getByRole('button', { name: strings.CoverDownloadScopeAllBooks, exact: true }).click();
    await page.getByText(strings.CoverDownloadScopeLibraryBooks, { exact: true }).click();
    await page.getByText(strings.CoverDownloadScopeLibraryBooksHelpText, { exact: true }).waitFor();
    assert.equal(estimates.length, 0, 'Browsing options must not scan the cache');
    const firstEstimate = nextEstimate();
    firstEstimate.catch(() => {});
    await calculate.press('Enter');
    const firstEstimateRoute = await firstEstimate;
    assert.deepEqual(await page.evaluate(() => window.savedCalls), [], 'Calculate must never save settings');
    assert.equal(await dialog.count(), 0, 'Calculate must not open the cleanup confirmation');
    assert.equal(await calculate.isEnabled(), false, 'A pending estimate must disable duplicate calculations');
    await page.getByText(strings.CoverCleanupEstimateLoading, { exact: true }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Save Changes', exact: true }).isEnabled(), true);
    await firstEstimateRoute.fulfill({ json: { bytes: 2397483648, bookCount: 100 } });
    await page.getByText(summary, { exact: true }).waitFor();
    await page.screenshot({ path: path.join(os.tmpdir(), 'chaptarr-cover-scope-calculate-desktop.png'), fullPage: true });

    await page.getByRole('button', { name: 'Save Changes', exact: true }).click();
    await dialog.getByText(strings.CoverDownloadScopeConfirmMessage, { exact: true }).waitFor();
    assert.equal(await dialog.getByRole('button', { name: strings.CalculateCoverSavings, exact: true }).count(), 0, 'The confirmation must not hide a second Calculate action');
    await dialog.getByText(summary, { exact: true }).waitFor();
    assert.equal(estimates.length, 1, 'Save must reuse the optional estimate without another scan');
    await dialog.getByRole('button', { name: 'Cancel', exact: true }).press('Enter');
    await page.getByText(summary, { exact: true }).waitFor();
    assert.deepEqual(await page.evaluate(() => window.savedCalls), [], 'Cancelling must keep the draft and estimate without saving');
    await page.getByRole('button', { name: 'Save Changes', exact: true }).click();
    await dialog.getByRole('button', { name: 'Save', exact: true }).press('Enter');
    await page.waitForFunction(() => window.savedCalls.length === 2);
    assert.deepEqual(await page.evaluate(() => window.savedCalls), ['mediaManagement', 'naming']);
    assert.deepEqual(await page.evaluate(() => window.invalidations), []);
    await page.evaluate(() => window.resolveMediaSaveSuccess());
    assert.deepEqual(await page.evaluate(() => window.invalidations), ['coverScope']);

    await page.evaluate(() => window.renderMediaManagementFixture({ selectedScope: 'libraryBooks' }));
    const requestCount = estimates.length;
    await page.getByRole('button', { name: 'Save Changes', exact: true }).click();
    await dialog.getByText(strings.CoverDownloadScopeConfirmMessage, { exact: true }).click();
    assert.equal(await dialog.locator('[aria-live]').count(), 0, 'Confirmation must not show an estimate until Calculate was used');
    await dialog.getByRole('button', { name: 'Save', exact: true }).press('Enter');
    await page.waitForFunction(() => window.savedCalls.length === 2);
    assert.equal(estimates.length, requestCount, 'Saving without Calculate must never request an estimate');

    await page.evaluate(() => window.renderMediaManagementFixture({ selectedScope: 'libraryBooks' }));
    const failedEstimate = nextEstimate();
    await calculate.click();
    await (await failedEstimate).fulfill({ contentType: 'application/json', body: '{' });
    await page.getByText(strings.CoverCleanupEstimateError, { exact: true }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Save Changes', exact: true }).isEnabled(), true);
    const retryEstimate = nextEstimate();
    await calculate.click();
    await retryEstimate;
    await page.getByRole('button', { name: 'Save Changes', exact: true }).click();
    assert.equal(await dialog.getByRole('button', { name: 'Save', exact: true }).isEnabled(), true, 'Save must not wait for an estimate');
    await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
    assert.equal(await calculate.isEnabled(), false, 'Cancelling confirmation must preserve the inline calculation');
    await page.getByRole('button', { name: 'Save Changes', exact: true }).click();
    const cancelledEstimate = page.waitForEvent('requestfailed', (request) => request.url().endsWith('/config/mediamanagement/covercleanup'));
    await dialog.getByRole('button', { name: 'Save', exact: true }).click();
    await cancelledEstimate;
    await page.waitForFunction(() => window.savedCalls.length === 2);
    assert.equal(await calculate.isEnabled(), true, 'Saving must reset the inline spinner after cancelling the scan');

    await page.evaluate(() => window.renderMediaManagementFixture());
    const unmountEstimate = nextEstimate();
    await calculate.click();
    await unmountEstimate;
    const unmountedRequest = page.waitForEvent('requestfailed', (request) => request.url().endsWith('/config/mediamanagement/covercleanup'));
    await page.evaluate(() => window.renderMediaManagementFixture({ savedScope: 'libraryBooks', otherSettingPending: true }));
    await unmountedRequest;
    await page.getByRole('button', { name: 'Save Changes', exact: true }).click();
    await page.waitForFunction(() => window.savedCalls.length === 2);
    assert.equal(await dialog.count(), 0, 'Unrelated saves must not open the cleanup confirmation');
    assert.deepEqual(await page.evaluate(() => window.invalidations), []);
    await page.evaluate(() => window.renderMediaManagementFixture({ savedScope: 'libraryBooks', selectedScope: 'allBooks' }));
    await page.getByRole('button', { name: 'Save Changes', exact: true }).click();
    await page.waitForFunction(() => window.savedCalls.length === 2);
    assert.equal(await dialog.count(), 0, 'Switching to All books must not require cleanup confirmation');
    await page.evaluate(() => window.resolveMediaSaveSuccess());
    assert.deepEqual(await page.evaluate(() => window.invalidations), ['coverScope']);
    assert.deepEqual(desktop.errors, []);

    const mobile = await openFixture(browser, strings, cssPath, { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });
    await mobile.page.evaluate(() => window.renderMediaManagementFixture({ selectedScope: 'libraryBooks' }));
    const mobileCalculate = mobile.page.getByRole('button', { name: strings.CalculateCoverSavings, exact: true });
    await mobileCalculate.scrollIntoViewIfNeeded();
    assert.equal(await mobileCalculate.isVisible(), true, 'Mobile users must find Calculate before Save too');
    assert.equal(await mobile.page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true, 'Settings must fit the mobile viewport');
    const mobileEstimate = mobile.nextEstimate();
    await mobileCalculate.click();
    await (await mobileEstimate).fulfill({ json: { bytes: 0, bookCount: 0 } });
    await mobile.page.getByText(strings.CoverCleanupEstimate.replace('{size}', '0 B'), { exact: true }).scrollIntoViewIfNeeded();
    await mobile.page.screenshot({ path: path.join(os.tmpdir(), 'chaptarr-cover-scope-calculate-mobile.png'), fullPage: true });
    await mobile.page.getByRole('button', { name: 'Save Changes', exact: true }).click();
    const mobileSave = await mobile.page.locator('[class*="ModalContent-modalContent"]').getByRole('button', { name: 'Save', exact: true }).boundingBox();
    assert(mobileSave && mobileSave.y + mobileSave.height <= 844 && mobileSave.x + mobileSave.width <= 390);
    assert.deepEqual(mobile.errors, []);
    await mobile.context.close();
    for (const width of [1440, 375]) {
      const isolation = await openFixture(browser, strings, cssPath, { viewport: { width, height: 900 } });
      const settingsPage = isolation.page;
      settingsPage.setDefaultTimeout(10000);
      await settingsPage.evaluate(() => window.renderSettingsIsolationFixture());
      const matching = settingsPage.locator('[class*="Quickstart-quickstartMatchingControls"]');
      const mode = () => matching.getByRole('button', { name: /^(Strict|Balanced|Loose)$/, exact: true });
      const save = () => matching.getByRole('button', { name: strings.Save, exact: true });
      await mode().waitFor();
      await settingsPage.waitForFunction(() => getComputedStyle(document.querySelector('[data-quickstart-section="matching"]')).opacity === '1');
      assert.equal(await save().count(), 0);
      await mode().click();
      await settingsPage.locator('#portal-root').getByText(strings.BookMatchingStrictnessAggressive, { exact: true }).click();
      await save().waitFor();
      const selectBox = await mode().boundingBox();
      const saveBox = await save().boundingBox();
      assert(saveBox.x >= selectBox.x + selectBox.width - 2, 'Save belongs beside the selector');
      assert(saveBox.x + saveBox.width <= width, 'Inline Save must fit on mobile');
      if (process.env.CHAPTARR_LAYOUT_ARTIFACTS) {
        fs.mkdirSync(process.env.CHAPTARR_LAYOUT_ARTIFACTS, { recursive: true });
        await settingsPage.screenshot({ path: path.join(process.env.CHAPTARR_LAYOUT_ARTIFACTS, `quickstart-matching-${width}.png`), fullPage: true });
      }
      await mode().click();
      await settingsPage.locator('#portal-root').getByText(strings.BookMatchingStrictnessBalanced, { exact: true }).click();
      assert.equal(await save().count(), 0, 'Reverting to the saved value removes Save');
      assert.equal(isolation.settingsWrites.length, 0, 'Changing options alone must not save');

      await mode().click();
      await settingsPage.locator('#portal-root').getByText(strings.BookMatchingStrictnessStrict, { exact: true }).click();
      assert.equal(await settingsPage.locator('input[name="usePathAsTagsFallback"]').isChecked(), false);
      await mode().click();
      await settingsPage.locator('#portal-root').getByText(strings.BookMatchingStrictnessBalanced, { exact: true }).click();
      assert.equal(await settingsPage.locator('input[name="usePathAsTagsFallback"]').isChecked(), true, 'Leaving unsaved Strict restores the prior checkbox');
      assert.equal(await save().count(), 0, 'Strict round trip must remove Save');
      assert.deepEqual(await settingsPage.evaluate(() => window.getSettingsDraft()), {}, 'Strict round trip must leave no draft');
      assert.equal(isolation.settingsWrites.length, 0, 'Strict round trip must not write settings');

      await mode().click();
      await settingsPage.locator('#portal-root').getByText(strings.BookMatchingStrictnessStrict, { exact: true }).click();
      await settingsPage.evaluate(() => window.setSettingsDraft('coverDownloadScope', 'libraryBooks'));
      await settingsPage.getByRole('button', { name: 'Edit root folder', exact: true }).click();
      await settingsPage.locator('input[name="isDefaultAudiobookRootFolder"]').locator('..').click();
      const rootSettingsSave = settingsPage.waitForResponse((response) => response.url().endsWith('/config/mediamanagement') && response.request().method() === 'PUT');
      await settingsPage.getByRole('button', { name: 'Update Audiobook Folder', exact: true }).click();
      await rootSettingsSave;
      await settingsPage.waitForFunction(() => window.getSettingsDraft().defaultAudiobookRootFolderPath == null);
      assert.equal(isolation.rootWrites.length, 1);
      assert.equal(isolation.settingsWrites.length, 1);
      assert.equal(isolation.settingsWrites[0].defaultAudiobookRootFolderPath, '/data/audiobooks');
      assert.equal(isolation.settingsWrites[0].bookMatchingStrictness, 'balanced', 'Root save must not save matching drafts');
      assert.equal(isolation.settingsWrites[0].coverDownloadScope, 'allBooks', 'Root save must not remove covers');
      assert.deepEqual(await settingsPage.evaluate(() => window.getSettingsDraft()), {
        bookMatchingStrictness: 'strict', usePathAsTagsFallback: false, coverDownloadScope: 'libraryBooks'
      }, 'A modal save must retain unrelated drafts');

      await save().click();
      await settingsPage.waitForFunction(() => window.getSettingsDraft().bookMatchingStrictness == null);
      assert.equal(isolation.settingsWrites[1].bookMatchingStrictness, 'strict');
      assert.equal(isolation.settingsWrites[1].coverDownloadScope, 'allBooks', 'Matching Save must save only its own fields');
      assert.equal(await save().count(), 0, 'A different pending field must not show Matching Save');
      await settingsPage.getByRole('button', { name: 'Leave Quickstart', exact: true }).click();
      assert.deepEqual(await settingsPage.evaluate(() => window.getSettingsDraft()), { coverDownloadScope: 'libraryBooks' }, 'Leaving Quickstart preserves another section’s draft');
      await settingsPage.getByRole('button', { name: 'Return to Quickstart', exact: true }).click();
      await mode().waitFor();
      await mode().click();
      await settingsPage.locator('#portal-root').getByText(strings.BookMatchingStrictnessBalanced, { exact: true }).click();
      assert.equal(await settingsPage.locator('input[name="usePathAsTagsFallback"]').isChecked(), false, 'A saved Strict preference must stay off when leaving Strict');
      await settingsPage.getByRole('button', { name: 'Leave Quickstart', exact: true }).click();
      assert.deepEqual(await settingsPage.evaluate(() => window.getSettingsDraft()), { coverDownloadScope: 'libraryBooks' }, 'Leaving discards only the Matching draft');
      const coverSave = settingsPage.waitForResponse((response) => response.url().endsWith('/config/mediamanagement') && response.request().method() === 'PUT');
      await settingsPage.evaluate(() => {
        window.saveCoverSetting();
      });
      await coverSave;
      assert.equal(isolation.settingsWrites.at(-1).bookMatchingStrictness, 'strict', 'Later saves must use the saved matching mode');
      assert.deepEqual(isolation.errors, []);
      await isolation.context.close();
    }
    for (const width of [1440, 375]) {
      const settings = await openFixture(browser, strings, cssPath, { viewport: { width, height: 900 } });
      await settings.page.evaluate(() => window.renderSettingsIsolationFixture('settings'));
      const mode = settings.page.getByRole('button', { name: /^(Strict|Balanced|Loose)$/, exact: true });
      await mode.waitFor();
      const fallback = settings.page.locator('input[name="usePathAsTagsFallback"]');
      await mode.click();
      await settings.page.locator('#portal-root').getByText(strings.BookMatchingStrictnessStrict, { exact: true }).click();
      assert.equal(await fallback.isChecked(), false);
      await mode.click();
      await settings.page.locator('#portal-root').getByText(strings.BookMatchingStrictnessBalanced, { exact: true }).click();
      assert.equal(await fallback.isChecked(), true);
      assert.deepEqual(await settings.page.evaluate(() => window.getSettingsDraft()), {});
      assert.equal(await settings.page.getByRole('button', { name: strings.HasPendingChangesNoChanges, exact: true }).isEnabled(), false);
      assert.equal(settings.settingsWrites.length, 0);

      await fallback.locator('..').click();
      await mode.click();
      await settings.page.locator('#portal-root').getByText(strings.BookMatchingStrictnessStrict, { exact: true }).click();
      await mode.click();
      await settings.page.locator('#portal-root').getByText(strings.BookMatchingStrictnessBalanced, { exact: true }).click();
      assert.equal(await fallback.isChecked(), false, 'Restore the user draft before Strict, not the saved checkbox');
      assert.deepEqual(await settings.page.evaluate(() => window.getSettingsDraft()), { usePathAsTagsFallback: false });
      assert.deepEqual(settings.errors, []);
      await settings.context.close();

      const general = await openFixture(browser, strings, cssPath, { viewport: { width, height: 900 } });
      const generalPage = general.page;
      await generalPage.evaluate(() => window.renderSettingsIsolationFixture('general'));
      const proxyModal = generalPage.locator('[class*="ModalContent-modalContent"]');
      await proxyModal.locator('input[name="proxyHostname"]').waitFor();
      await generalPage.evaluate(() => window.setGeneralDraft('port', 9999));
      await proxyModal.locator('input[name="proxyName"]').fill('Example proxy');
      await proxyModal.locator('input[name="proxyHostname"]').fill('proxy.example.invalid');
      await proxyModal.getByRole('button', { name: strings.Test, exact: true }).click();
      await proxyModal.getByRole('button', { name: strings.Save, exact: true }).click();
      await proxyModal.waitFor({ state: 'hidden' });
      assert.equal(general.proxyWrites.length, 1);
      assert.equal(general.generalWrites.length, 1);
      assert.equal(general.generalWrites[0].port, 8789, 'Proxy save must not save the unsaved port');
      assert.equal(general.generalWrites[0].globalProxyId, 7);
      assert.equal(general.generalWrites[0].proxyMode, 'proxyEverything');
      assert.deepEqual(await generalPage.evaluate(() => window.getGeneralDraft()), { port: 9999 });
      await generalPage.getByRole('button', { name: 'Info', exact: true }).click();
      await generalPage.locator('#portal-root').getByText('Debug', { exact: true }).click();
      await generalPage.waitForFunction(() => document.querySelector('[class*="LogFiles-logLevelDropdown"]')?.textContent.includes('Debug'));
      assert.equal(general.generalWrites.length, 2);
      assert.equal(general.generalWrites[1].logLevel, 'debug');
      assert.equal(general.generalWrites[1].port, 8789, 'Log-level save must not save the unsaved port');
      assert.deepEqual(await generalPage.evaluate(() => window.getGeneralDraft()), { port: 9999 });
      assert.deepEqual(general.errors, []);
      await general.context.close();
    }
    for (const width of [1440, 375]) {
      console.log(`Checking request-owned settings controls at ${width}px.`);
      const rootOwner = await openFixture(browser, strings, cssPath, { viewport: { width, height: 900 } });
      const rootPage = rootOwner.page;
      await rootPage.evaluate((theme) => window.setFixtureTheme(theme), width === 375 ? 'light' : 'dark');
      await rootPage.evaluate(() => window.renderSettingsIsolationFixture());
      await rootPage.getByRole('button', { name: 'Balanced', exact: true }).waitFor();
      rootOwner.holdConfigSaves();
      rootOwner.holdRootSaves();
      await rootPage.getByRole('button', { name: 'Edit root folder', exact: true }).click();
      const rootModal = rootPage.locator('[class*="ModalContent-modalContent"]');
      await rootPage.locator('input[name="isDefaultAudiobookRootFolder"]').locator('..').click();
      const rootSave = () => rootModal.locator('button').filter({ hasText: 'Update Audiobook Folder' });
      const pressRootSave = async(button) => {
        const observed = rootPage.waitForRequest((request) => (/\/rootfolder/i).test(request.url()) && ['POST', 'PUT'].includes(request.method()));
        await button.click();
        await observed;
      };
      await pressRootSave(rootSave());
      await rootPage.waitForFunction(() => window.getSettingsState().isPopulated);
      assert.equal(rootOwner.rootWrites.length, 1);
      assert.equal(await rootSave().isDisabled(), true, 'Root spinner belongs to its held root save');
      await rootOwner.completeRootSave(0);
      await rootPage.waitForFunction(() => window.getSettingsState().isSaving);
      assert.equal(await rootModal.count(), 1, 'Root modal remains open until its default-folder save finishes');
      assert.equal(await rootSave().isDisabled(), true, 'Root spinner continues through its default-folder save');
      assert.equal(await rootPage.evaluate(() => window.rootCompletions), 0);
      await rootOwner.completeConfigSave(0, 500, { message: 'Default save failed' });
      await rootModal.getByText(strings.RootFolderSavedDefaultFailed, { exact: true }).waitFor();
      const rootFooter = rootModal.locator('[class*="ModalFooter-modalFooter"]');
      const footerBox = await rootFooter.boundingBox();
      const saveBox = await rootSave().boundingBox();
      assert(saveBox.width > 150 && saveBox.x + saveBox.width <= footerBox.x + footerBox.width + 1, 'Root error must not shrink or clip the unchanged Save control');
      if (process.env.CHAPTARR_LAYOUT_ARTIFACTS) {
        await rootPage.screenshot({ path: path.join(process.env.CHAPTARR_LAYOUT_ARTIFACTS, `root-partial-error-${width}.png`) });
      }
      assert.equal(await rootSave().isEnabled(), true, 'Partial root failure is retryable');
      assert.deepEqual(await rootPage.evaluate(() => window.getSettingsDraft()), {}, 'Root default choice is not left as a hidden global draft');
      await rootPage.evaluate(() => {
        window.saveSettings({ coverDownloadScope: 'libraryBooks' });
      });
      await rootPage.waitForFunction(() => window.getSettingsState().isSaving);
      await rootOwner.completeConfigSave(1);
      assert.equal(await rootModal.getByText(strings.RootFolderSavedDefaultFailed, { exact: true }).count(), 1, 'Foreign success does not erase the root partial-save error');
      await pressRootSave(rootSave());
      await rootPage.waitForFunction(() => window.getSettingsState().isSaving === false);
      await rootOwner.completeRootSave(1, 500);
      await rootModal.getByText('Root save failed', { exact: true }).waitFor();
      await pressRootSave(rootSave());
      await rootOwner.completeRootSave(2);
      await rootPage.waitForFunction(() => window.getSettingsState().isSaving);
      await rootOwner.completeConfigSave(2);
      await rootModal.waitFor({ state: 'hidden' });
      assert.equal(await rootPage.evaluate(() => window.rootCompletions), 1);
      console.log(`Edited root retry passed at ${width}px.`);

      await rootPage.getByRole('button', { name: 'Add root folder', exact: true }).click();
      await rootModal.locator('input[name="name"]').fill('New books');
      await rootModal.locator('input[name="path"]').fill('/data/new-books');
      await rootModal.locator('input[name="name"]').focus();
      await rootPage.locator('input[name="isDefaultAudiobookRootFolder"]').locator('..').click();
      const addSave = () => rootModal.locator('button').filter({ hasText: /Save Audiobook Folder|Update Audiobook Folder/ });
      await pressRootSave(addSave());
      await rootOwner.completeRootSave(3);
      await rootPage.waitForFunction(() => window.getSettingsState().isSaving);
      await rootOwner.completeConfigSave(3, 500, { message: 'Default save failed' });
      await rootModal.getByText(strings.RootFolderSavedDefaultFailed, { exact: true }).waitFor();
      assert.equal(await rootModal.locator('input[name="name"]').inputValue(), 'New books', 'Created root retains accepted fields after default failure');
      assert.equal(await rootModal.locator('input[name="path"]').inputValue(), '/data/new-books');
      await pressRootSave(addSave());
      assert.equal(rootOwner.rootWrites.at(-1).method, 'PUT', 'Retry uses the created root ID instead of creating another folder');
      assert.equal(rootOwner.rootWrites.at(-1).id, 3);
      assert.equal(rootOwner.rootWrites.filter((item) => item.method === 'POST').length, 1);
      await rootOwner.completeRootSave(4);
      await rootPage.waitForFunction(() => window.getSettingsState().isSaving);
      await rootOwner.completeConfigSave(4);
      await rootModal.waitFor({ state: 'hidden' });
      assert.equal(await rootPage.evaluate(() => window.rootAddCompletions), 1, 'Created-root callback runs only after complete success');
      assert.deepEqual(rootOwner.errors.filter((message) => !message.startsWith('Failed to load resource: the server responded with a status of 500')), []);
      await rootOwner.context.close();

      console.log(`Root save chain passed at ${width}px.`);
      const matchingOwner = await openFixture(browser, strings, cssPath, { viewport: { width, height: 900 } });
      const matchingPage = matchingOwner.page;
      await matchingPage.evaluate(() => window.renderSettingsIsolationFixture());
      await matchingPage.getByRole('button', { name: 'Balanced', exact: true }).waitFor();
      const matchingSave = () => matchingPage.locator('[class*="Quickstart-quickstartMatchingControls"] button').filter({ hasText: strings.Save });
      matchingOwner.holdConfigSaves();
      await matchingPage.evaluate(() => {
        window.setSettingsDraft('bookMatchingStrictness', 'strict');
        window.saveSettings({ coverDownloadScope: 'libraryBooks' });
      });
      await matchingPage.waitForFunction(() => window.getSettingsState().isSaving);
      assert.equal(await matchingSave().isEnabled(), true, 'Foreign save does not spin or disable Matching');
      await matchingSave().click();
      assert.equal(await matchingSave().isDisabled(), true, 'Matching spins while queued behind another save');
      await matchingOwner.completeConfigSave(0, 500, { message: 'Foreign save failed' });
      await matchingPage.waitForFunction(() => window.getSettingsState().isSaving);
      assert.equal(await matchingPage.evaluate(() => window.matchingCompletions), 0);
      assert.equal(await matchingPage.getByText(strings.UnableToSaveMediaManagementSettings, { exact: true }).count(), 0, 'Foreign failure does not appear as a Matching failure');
      await matchingOwner.completeConfigSave(1, 500, { message: 'Matching failed' });
      await matchingPage.getByText(strings.UnableToSaveMediaManagementSettings, { exact: true }).waitFor();
      if (process.env.CHAPTARR_LAYOUT_ARTIFACTS) {
        await matchingPage.waitForFunction(() => getComputedStyle(document.querySelector('[data-quickstart-section="matching"]')).opacity === '1');
        await matchingPage.screenshot({ path: path.join(process.env.CHAPTARR_LAYOUT_ARTIFACTS, `matching-own-error-${width}.png`) });
      }
      await matchingPage.evaluate(() => {
        window.saveSettings({ defaultEbookRootFolderPath: '/data/ebooks' });
      });
      await matchingPage.waitForFunction(() => window.getSettingsState().isSaving);
      await matchingOwner.completeConfigSave(2);
      assert.equal(await matchingPage.getByText(strings.UnableToSaveMediaManagementSettings, { exact: true }).count(), 1, 'Foreign success preserves the own Matching failure');
      await matchingSave().click();
      await matchingPage.waitForFunction(() => window.getSettingsState().isSaving);
      await matchingOwner.completeConfigSave(3);
      await matchingPage.waitForFunction(() => window.matchingCompletions === 1);
      assert.equal(await matchingPage.getByText(strings.UnableToSaveMediaManagementSettings, { exact: true }).count(), 0);
      await matchingPage.evaluate(() => {
        window.setSettingsDraft('bookMatchingStrictness', 'balanced');
        window.setSettingsDraft('coverDownloadScope', 'libraryBooks');
        window.batchedAcceptAndLeaveMatching();
      });
      assert.deepEqual(await matchingPage.evaluate(() => window.getSettingsDraft()), { coverDownloadScope: 'libraryBooks' }, 'Batched accepted-value update and unmount cannot recreate stale Matching drafts or clear sibling drafts');
      assert.equal(await matchingPage.evaluate(() => window.getSettingsState().item.bookMatchingStrictness), 'aggressive');
      assert.equal(await matchingPage.evaluate(() => window.getSettingsState().saveError.message), 'Other section failed', 'Scoped cleanup preserves another section’s shared error');
      await matchingPage.evaluate(() => window.clearAllSettingsDrafts());
      assert.deepEqual(await matchingPage.evaluate(() => window.getSettingsDraft()), {}, 'Existing clear-all callers still clear all pending fields');
      assert.equal(await matchingPage.evaluate(() => window.getSettingsState().saveError), null, 'Existing clear-all callers still clear the shared error');
      assert.deepEqual(matchingOwner.errors.filter((message) => !message.startsWith('Failed to load resource: the server responded with a status of 500')), []);
      await matchingOwner.context.close();

      console.log(`Matching save ownership passed at ${width}px.`);
      const logsOwner = await openFixture(browser, strings, cssPath, { viewport: { width, height: 900 } });
      const logsPage = logsOwner.page;
      await logsPage.evaluate((theme) => window.setFixtureTheme(theme), width === 375 ? 'light' : 'dark');
      await logsPage.evaluate(() => window.renderSettingsIsolationFixture('logs'));
      await logsPage.getByRole('button', { name: 'Info', exact: true }).waitFor();
      logsOwner.holdConfigSaves();
      await logsPage.evaluate(() => {
        window.saveGeneralSetting({ port: 9999 });
      });
      await logsPage.waitForFunction(() => window.getGeneralSettingsState().isSaving);
      const logLevel = () => logsPage.locator('[class*="LogFiles-logLevelDropdown"] button');
      assert.equal(await logLevel().isEnabled(), true, 'Foreign General save does not disable Logs');
      await logLevel().focus();
      await logsPage.keyboard.press('Enter');
      await logsPage.locator('#portal-root').getByText('Debug', { exact: true }).click();
      await logsPage.evaluate(() => window.setGeneralDraft('logLevel', 'trace'));
      assert.equal(await logLevel().isDisabled(), true, 'Logs disables only for its own request');
      await logsOwner.completeConfigSave(0);
      await logsPage.waitForFunction(() => window.getGeneralSettingsState().isSaving);
      await logsOwner.completeConfigSave(1, 500, { message: 'Log save failed' });
      await logsPage.getByText(strings.UnableToSaveLogLevel, { exact: true }).waitFor();
      if (process.env.CHAPTARR_LAYOUT_ARTIFACTS) {
        await logsPage.screenshot({ path: path.join(process.env.CHAPTARR_LAYOUT_ARTIFACTS, `logs-save-error-${width}.png`) });
      }
      assert.equal((await logLevel().innerText()).trim(), 'Info', 'Failure displays accepted log level, not another General draft');
      await logsPage.evaluate(() => {
        window.saveGeneralSetting({ bindAddress: '*' });
      });
      await logsPage.waitForFunction(() => window.getGeneralSettingsState().isSaving);
      await logsOwner.completeConfigSave(2);
      assert.equal(await logsPage.getByText(strings.UnableToSaveLogLevel, { exact: true }).count(), 1, 'Foreign success does not clear the Logs error');
      await logLevel().click();
      await logsPage.locator('#portal-root').getByText('Debug', { exact: true }).click();
      await logsPage.waitForFunction(() => window.getGeneralSettingsState().isSaving);
      await logsOwner.completeConfigSave(3);
      await logsPage.getByRole('button', { name: 'Debug', exact: true }).waitFor();
      assert.equal(await logsPage.getByText(strings.UnableToSaveLogLevel, { exact: true }).count(), 0);
      assert.equal(await logsPage.evaluate(() => window.getGeneralDraft().logLevel), 'trace', 'Later General draft remains preserved');
      if (process.env.CHAPTARR_LAYOUT_ARTIFACTS) {
        await logsPage.screenshot({ path: path.join(process.env.CHAPTARR_LAYOUT_ARTIFACTS, `logs-retry-${width}.png`) });
      }
      assert.deepEqual(logsOwner.errors.filter((message) => !message.startsWith('Failed to load resource: the server responded with a status of 500')), []);
      await logsOwner.context.close();

      console.log(`Logs save ownership passed at ${width}px.`);
      const proxyOwner = await openFixture(browser, strings, cssPath, { viewport: { width, height: 900 } });
      const proxyPage = proxyOwner.page;
      await proxyPage.evaluate(() => window.renderSettingsIsolationFixture('general'));
      const proxyModal = proxyPage.locator('[class*="ModalContent-modalContent"]');
      await proxyModal.locator('input[name="proxyHostname"]').fill('proxy.example.invalid');
      await proxyModal.getByRole('button', { name: strings.Test, exact: true }).click();
      const proxySave = () => proxyModal.locator('button').filter({ hasText: strings.Save });
      await proxySave().waitFor();
      proxyOwner.holdConfigSaves();
      await proxyPage.evaluate(() => {
        window.saveGeneralSetting({ logLevel: 'debug' });
      });
      await proxyPage.waitForFunction(() => window.getGeneralSettingsState().isSaving);
      assert.equal(await proxySave().isEnabled(), true, 'Foreign log save does not spin or disable Proxy Save');
      const proxyPost = proxyPage.waitForResponse((response) => response.url().endsWith('/settings/proxy') && response.request().method() === 'POST');
      await proxySave().click();
      await proxyPost;
      assert.equal(await proxySave().isDisabled(), true, 'Proxy spinner covers its own default save while queued');
      await proxyOwner.completeConfigSave(0);
      await proxyPage.waitForFunction(() => window.getGeneralSettingsState().isSaving);
      assert.equal(await proxyModal.count(), 1);
      await proxyOwner.completeConfigSave(1, 500, { message: 'Proxy default failed' });
      await proxyPage.waitForFunction(() => !window.getGeneralSettingsState().isSaving);
      assert.equal(await proxyModal.count(), 1, 'Own default failure keeps Proxy open');
      assert.equal(await proxySave().isEnabled(), true);
      if (process.env.CHAPTARR_LAYOUT_ARTIFACTS) {
        await proxySave().locator('[class*="SpinnerErrorButton-showIcon"]').waitFor();
        await proxyPage.screenshot({ path: path.join(process.env.CHAPTARR_LAYOUT_ARTIFACTS, `proxy-default-error-${width}.png`) });
        await proxySave().locator('[class*="SpinnerErrorButton-showIcon"]').waitFor({ state: 'hidden' });
        await proxyPage.waitForFunction(() => getComputedStyle(document.querySelector('[class*="ModalFooter-modalFooter"] [class*="SpinnerErrorButton-label"]')).opacity === '1');
        await proxyPage.screenshot({ path: path.join(process.env.CHAPTARR_LAYOUT_ARTIFACTS, `proxy-default-error-stable-${width}.png`) });
      }
      await proxySave().click();
      await proxyPage.waitForFunction(() => window.getGeneralSettingsState().isSaving);
      assert.deepEqual(proxyOwner.proxyWrites.map((item) => item.method), ['POST', 'PUT'], 'Proxy retry reuses returned ID');
      await proxyModal.getByRole('button', { name: strings.Cancel, exact: true }).click();
      await proxyPage.getByRole('button', { name: 'Open proxy', exact: true }).click();
      await proxyModal.locator('input[name="proxyHostname"]').waitFor();
      await proxyOwner.completeConfigSave(2);
      await proxyPage.waitForFunction(() => !window.getGeneralSettingsState().isSaving);
      assert.equal(await proxyModal.count(), 1, 'Old request cannot close a reopened Proxy modal');
      assert.deepEqual(proxyOwner.errors.filter((message) => !message.startsWith('Failed to load resource: the server responded with a status of 500')), []);
      await proxyOwner.context.close();
    }
    console.log('All request-owned surface regressions passed.');
    const ordering = await openFixture(browser, strings, cssPath, { viewport: { width: 1280, height: 900 } });
    const orderingPage = ordering.page;
    await orderingPage.evaluate(() => window.renderSettingsIsolationFixture());
    await orderingPage.waitForFunction(() => window.getSettingsState()?.isPopulated);
    ordering.holdConfigSaves();
    await orderingPage.evaluate(() => {
      window.settingsSaveResults = [];
      const record = (name, request) => request.done(() => window.settingsSaveResults.push(`${name}:done`))
        .fail(() => window.settingsSaveResults.push(`${name}:fail`));
      window.setSettingsDraft('bookMatchingStrictness', 'strict');
      record('normal', window.saveSettings({}, false));
      record('root', window.saveSettings({ defaultAudiobookRootFolderPath: '/data/audiobooks' }));
      record('cover', window.saveSettings({ coverDownloadScope: 'libraryBooks' }));
      window.setSettingsDraft('bookMatchingStrictness', 'aggressive');
      window.setSettingsDraft('defaultEbookRootFolderPath', '/data/ebooks');
      record('general', window.saveGeneralSetting({ logLevel: 'debug' }));
    });
    await orderingPage.waitForTimeout(150);
    assert.equal(ordering.settingsWrites.length, 1, 'Only the first same-resource full PUT may start before it settles');
    assert.equal(ordering.generalWrites.length, 1, 'A different config resource must save independently');
    assert.equal(ordering.settingsWrites[0].bookMatchingStrictness, 'strict', 'Normal Save captures the selected draft at dispatch');
    assert.equal(ordering.settingsWrites[0].defaultAudiobookRootFolderPath, '/data/other');
    await ordering.completeConfigSave(0);
    await orderingPage.waitForFunction(() => window.settingsSaveResults.includes('normal:done'));
    await orderingPage.waitForTimeout(100);
    assert.equal(ordering.settingsWrites.length, 2, 'The next requested save starts after the first response');
    assert.equal(await orderingPage.evaluate(() => window.getSettingsState().isSaving), true, 'Same-resource pending requests retain the saving state');
    assert.equal(ordering.settingsWrites[1].bookMatchingStrictness, 'strict', 'The root save uses the accepted matching setting');
    assert.equal(ordering.settingsWrites[1].defaultAudiobookRootFolderPath, '/data/audiobooks');
    assert.equal(ordering.settingsWrites[1].coverDownloadScope, 'allBooks', 'Later unrequested drafts must stay out of the root save');
    await ordering.completeConfigSave(2);
    await orderingPage.waitForFunction(() => window.settingsSaveResults.includes('root:done'));
    await orderingPage.waitForTimeout(100);
    assert.equal(ordering.settingsWrites.length, 3);
    assert.equal(ordering.settingsWrites[2].defaultAudiobookRootFolderPath, '/data/audiobooks', 'The cover save uses the accepted root setting');
    assert.equal(ordering.settingsWrites[2].bookMatchingStrictness, 'strict');
    assert.equal(ordering.settingsWrites[2].coverDownloadScope, 'libraryBooks', 'Queued Save retains its original requested value');
    await ordering.completeConfigSave(3);
    await ordering.completeConfigSave(1);
    await orderingPage.waitForFunction(() => !window.getSettingsState().isSaving && !window.getGeneralSettingsState().isSaving);
    const orderedState = await orderingPage.evaluate(() => window.getSettingsState());
    assert.equal(orderedState.item.bookMatchingStrictness, 'strict');
    assert.equal(orderedState.item.defaultAudiobookRootFolderPath, '/data/audiobooks');
    assert.equal(orderedState.item.coverDownloadScope, 'libraryBooks');
    assert.deepEqual(orderedState.pendingChanges, {
      bookMatchingStrictness: 'aggressive', defaultEbookRootFolderPath: '/data/ebooks'
    }, 'Both normal and scoped saves preserve drafts changed after dispatch');
    assert.deepEqual(await orderingPage.evaluate(() => window.settingsSaveResults), ['normal:done', 'root:done', 'cover:done', 'general:done']);
    assert.deepEqual(ordering.errors, []);
    await ordering.context.close();

    const recovery = await openFixture(browser, strings, cssPath, { viewport: { width: 1280, height: 900 } });
    const recoveryPage = recovery.page;
    await recoveryPage.evaluate(() => window.renderSettingsIsolationFixture('general'));
    await recoveryPage.locator('[class*="ModalContent-modalContent"]').getByRole('button', { name: strings.Cancel, exact: true }).click();
    await recoveryPage.waitForFunction(() => window.getGeneralSettingsState()?.isPopulated);
    recovery.holdConfigSaves();
    await recoveryPage.evaluate(() => {
      window.settingsSaveResults = [];
      const record = (name, request) => request.done(() => window.settingsSaveResults.push(`${name}:done`))
        .fail(() => window.settingsSaveResults.push(`${name}:fail`));
      record('log', window.saveGeneralSetting({ logLevel: 'debug' }));
      window.setGeneralDraft('port', 9999);
      record('normal', window.saveGeneralSetting({}, false));
      record('next-log', window.saveGeneralSetting({ logLevel: 'trace' }));
      window.setGeneralDraft('port', 8888);
    });
    await recoveryPage.waitForTimeout(150);
    assert.equal(recovery.generalWrites.length, 1, 'Scoped and normal General saves share the same request ordering');
    await recovery.completeConfigSave(0, 500, { message: 'Save failed for this test' });
    await recoveryPage.waitForFunction(() => window.settingsSaveResults.includes('log:fail'));
    await recoveryPage.waitForTimeout(100);
    assert.equal(recovery.generalWrites.length, 2, 'A failed request must not discard the next requested save');
    assert.equal(recovery.generalWrites[1].logLevel, 'info', 'The next full PUT must not pretend the failed setting was saved');
    assert.equal(recovery.generalWrites[1].port, 9999, 'Queued normal Save captures its original requested draft');
    assert.equal(await recoveryPage.evaluate(() => window.getGeneralSettingsState().isSaving), true);
    await recovery.completeConfigSave(1, 200, { ...recovery.generalWrites[1], port: 9998 });
    await recoveryPage.waitForFunction(() => window.settingsSaveResults.includes('normal:done'));
    await recoveryPage.waitForTimeout(100);
    assert.equal(recovery.generalWrites.length, 3);
    assert.equal(recovery.generalWrites[2].port, 9998, 'The next full PUT reuses the server-normalized saved value');
    assert.equal(recovery.generalWrites[2].logLevel, 'trace', 'The last requested value of the same field wins in request order');
    await recovery.completeConfigSave(2);
    await recoveryPage.waitForFunction(() => !window.getGeneralSettingsState().isSaving);
    const recoveredState = await recoveryPage.evaluate(() => window.getGeneralSettingsState());
    assert.equal(recoveredState.item.port, 9998);
    assert.equal(recoveredState.item.logLevel, 'trace');
    assert.equal(recoveredState.saveError, null);
    assert.deepEqual(recoveredState.pendingChanges, { port: 8888 }, 'Later drafts survive failed and successful requests');
    assert.deepEqual(await recoveryPage.evaluate(() => window.settingsSaveResults), ['log:fail', 'normal:done', 'next-log:done']);
    assert.deepEqual(recovery.errors, ['Failed to load resource: the server responded with a status of 500 (Internal Server Error)'], 'Only the deliberate failed-save response may produce a browser error');
    await recovery.context.close();

    for (const changeDefaultAfterFailure of [false, true]) {
      const rename = await openFixture(browser, strings, cssPath, { viewport: { width: changeDefaultAfterFailure ? 375 : 1440, height: 900 } });
      const renamePage = rename.page;
      await renamePage.evaluate(() => window.renderSettingsIsolationFixture());
      await renamePage.getByRole('button', { name: 'Balanced', exact: true }).waitFor();
      rename.holdConfigFetches();
      await renamePage.evaluate(() => window.setSettingsPopulated(false));
      await renamePage.getByRole('button', { name: 'Edit default root folder', exact: true }).click();
      const renameModal = renamePage.locator('[class*="ModalContent-modalContent"]');
      const renameSave = renameModal.locator('button').filter({ hasText: 'Update Audiobook Folder' });
      await renameModal.locator('input[name="path"]').waitFor();
      assert.equal(await renameSave.isDisabled(), true, 'Root Save waits for loaded default settings');
      await rename.completeConfigFetch(0);
      await renamePage.waitForFunction(() => window.getSettingsState().isPopulated);
      await renameModal.locator('input[name="path"]').fill('/data/renamed');
      await renameModal.locator('input[name="name"]').focus();
      assert.equal(await renamePage.locator('input[name="isDefaultAudiobookRootFolder"]').isChecked(), true, 'Renaming retains the intended default checkbox');
      rename.holdRootSaves();
      rename.holdConfigSaves();
      await renameSave.click();
      await renamePage.waitForFunction(() => window.fixtureRootSaveCount === 1);
      assert.equal(await renameModal.getByRole('button', { name: strings.Cancel, exact: true }).isDisabled(), true);
      await renamePage.keyboard.press('Escape');
      await renameModal.locator('[class*="ModalContent-closeButton"]').click();
      await renamePage.locator('[class*="Modal-modalBackdrop"]').click({ position: { x: 2, y: 2 }, force: true });
      assert.equal(await renameModal.count(), 1, 'Escape, X and backdrop cannot discard an in-flight root save');
      await rename.completeRootSave(0);
      await renamePage.waitForFunction(() => window.fixtureConfigSaveCount === 1);
      await renamePage.keyboard.press('Escape');
      assert.equal(await renameModal.count(), 1, 'Close stays guarded through the default-setting write');
      await rename.completeConfigSave(0, 500, { message: 'Default save failed' });
      await renameModal.getByText(strings.RootFolderSavedDefaultFailed, { exact: true }).waitFor();
      assert.equal(rename.settingsWrites[0].defaultAudiobookRootFolderPath, '/data/renamed');
      assert.equal(await renamePage.locator('input[name="isDefaultAudiobookRootFolder"]').isChecked(), true, 'Partial default failure preserves the intended checkbox');
      if (changeDefaultAfterFailure) {
        await renamePage.locator('input[name="isDefaultAudiobookRootFolder"]').locator('..').click();
        assert.equal(await renamePage.locator('input[name="isDefaultAudiobookRootFolder"]').isChecked(), false);
      }
      await renameSave.click();
      await rename.completeRootSave(1);
      await renamePage.waitForFunction(() => window.fixtureConfigSaveCount === 2);
      assert.equal(rename.settingsWrites[1].defaultAudiobookRootFolderPath, changeDefaultAfterFailure ? '' : '/data/renamed',
        'A partial-save retry preserves rename intent and respects the latest checkbox choice');
      await rename.completeConfigSave(1);
      await renameModal.waitFor({ state: 'hidden' });
      assert.equal(await renamePage.evaluate(() => window.rootCompletions), 1);
      await renamePage.getByRole('button', { name: 'Edit root folder', exact: true }).click();
      await renameModal.locator('input[name="path"]').waitFor();
      await renameModal.locator('button').filter({ hasText: 'Update Audiobook Folder' }).click();
      await rename.completeRootSave(2);
      await renameModal.waitFor({ state: 'hidden' });
      assert.equal(rename.settingsWrites.length, 2, 'A different root edit does not inherit the previous original path');
      await rename.context.close();
    }

    for (const inverseDefaultDraft of [false, true]) {
      const isolation = await openFixture(browser, strings, cssPath, { viewport: { width: inverseDefaultDraft ? 375 : 1440, height: 900 } });
      const isolationPage = isolation.page;
      await isolationPage.evaluate(() => window.renderSettingsIsolationFixture());
      await isolationPage.getByRole('button', { name: 'Balanced', exact: true }).waitFor();
      if (inverseDefaultDraft) {
        await isolationPage.evaluate(() => {
          window.saveSettings({ defaultAudiobookRootFolderPath: '/data/audiobooks' });
        });
        await isolationPage.waitForFunction(() => !window.getSettingsState().isSaving);
      }
      const unrelatedDraft = inverseDefaultDraft ? '/data/other' : '/data/audiobooks';
      await isolationPage.evaluate((value) => window.setSettingsDraft('defaultAudiobookRootFolderPath', value), unrelatedDraft);
      const defaultWriteCount = isolation.settingsWrites.length;
      await isolationPage.getByRole('button', { name: 'Edit default root folder', exact: true }).click();
      const isolationModal = isolationPage.locator('[class*="ModalContent-modalContent"]');
      await isolationModal.locator('input[name="path"]').fill('/data/renamed');
      await isolationModal.locator('input[name="name"]').focus();
      assert.equal(await isolationPage.locator('input[name="isDefaultAudiobookRootFolder"]').isChecked(), !inverseDefaultDraft, 'The root modal uses the saved default relationship');
      await isolationModal.locator('button').filter({ hasText: 'Update Audiobook Folder' }).click();
      await isolationPage.waitForFunction(() => window.rootCompletions === 1);
      assert.equal(await isolationPage.evaluate(() => window.getSettingsState().item.defaultAudiobookRootFolderPath), inverseDefaultDraft ? '/data/audiobooks' : '/data/renamed', 'Unrelated drafts cannot remove or retarget root default maintenance');
      assert.equal(isolation.settingsWrites.length - defaultWriteCount, inverseDefaultDraft ? 0 : 1);
      assert.deepEqual(await isolationPage.evaluate(() => window.getSettingsDraft()), { defaultAudiobookRootFolderPath: unrelatedDraft }, 'Root saving preserves the other surface draft');
      await isolation.context.close();
    }

    const scopedFailure = await openFixture(browser, strings, cssPath, { viewport: { width: 1280, height: 900 } });
    const scopedPage = scopedFailure.page;
    await scopedPage.evaluate(() => window.renderSettingsIsolationFixture());
    await scopedPage.getByRole('button', { name: 'Balanced', exact: true }).waitFor();
    scopedFailure.holdConfigSaves();
    await scopedPage.evaluate(() => window.setSettingsDraft('defaultAudiobookRootFolderPath', '/data/unsaved'));
    let quickSaveIndex = 0;
    for (const [surface, field, value] of [
      ['queue', 'autoAddMissingAuthorsFromCompletedDownloads', true],
      ['interactive', 'usePathAsTagsFallback', false],
      ['folder', 'usePathAsTagsFallback', true]
    ]) {
      const before = await scopedPage.evaluate((name) => window.getSettingsState().item[name], field);
      await scopedPage.evaluate(([name, nextValue]) => window.saveQuickSetting(name, nextValue), [surface, value]);
      await scopedFailure.completeConfigSave(quickSaveIndex++, 500, { message: 'Toggle failed' });
      await scopedPage.waitForFunction(() => !window.getSettingsState().isSaving);
      assert.deepEqual(await scopedPage.evaluate(() => window.getSettingsDraft()), { defaultAudiobookRootFolderPath: '/data/unsaved' }, `${surface} quick save does not create a hidden global draft`);
      assert.equal(await scopedPage.evaluate((name) => window.getSettingsState().item[name], field), before, 'Failed quick save retains the accepted value');
      await scopedPage.evaluate(() => {
        window.saveSettings({ defaultEbookRootFolderPath: '/data/ebooks' });
      });
      await scopedFailure.completeConfigSave(quickSaveIndex++);
      await scopedPage.waitForFunction(() => !window.getSettingsState().isSaving);
      assert.equal(await scopedPage.evaluate(() => window.getSettingsState().saveError), null, 'Scoped failures without pending fields are kept by their request surface');
      await scopedPage.evaluate(([name, nextValue]) => window.saveQuickSetting(name, nextValue), [surface, value]);
      await scopedFailure.completeConfigSave(quickSaveIndex++);
      await scopedPage.waitForFunction(() => !window.getSettingsState().isSaving);
      assert.equal(await scopedPage.evaluate((name) => window.getSettingsState().item[name], field), value, 'Successful quick save uses the accepted server response');
      assert.equal(await scopedPage.evaluate(() => window.getSettingsState().saveError), null, 'Saving the failed setting clears its error');
    }
    assert.equal(await scopedPage.evaluate(() => window.quickRefetches), 1, 'Interactive import refetches only after its successful setting write');
    const beforeStrictGuards = scopedFailure.settingsWrites.length;
    await scopedPage.evaluate(() => {
      window.setSettingsDraft('bookMatchingStrictness', 'strict');
      window.saveQuickSetting('interactive', true);
      window.saveQuickSetting('folder', true);
      window.setSettingsDraft('bookMatchingStrictness', 'balanced');
    });
    assert.equal(scopedFailure.settingsWrites.length, beforeStrictGuards, 'Both import handlers retain the Strict-mode fallback guard');
    await scopedPage.evaluate(() => {
      window.saveSettings({}, false);
    });
    await scopedFailure.completeConfigSave(quickSaveIndex++, 500, { message: 'Full page failed' });
    await scopedPage.waitForFunction(() => !window.getSettingsState().isSaving);
    assert.deepEqual(await scopedPage.evaluate(() => window.getSettingsDraft()), { defaultAudiobookRootFolderPath: '/data/unsaved' }, 'Full-page failures retain editable drafts');
    await scopedPage.evaluate(() => {
      window.saveSettings({}, false);
      window.setSettingsDraft('defaultAudiobookRootFolderPath', '/data/later-unsaved');
    });
    await scopedFailure.completeConfigSave(quickSaveIndex++);
    await scopedPage.waitForFunction(() => !window.getSettingsState().isSaving);
    await scopedPage.evaluate(() => {
      window.setSettingsDraft('autoAddMissingAuthorsFromCompletedDownloads', false);
      window.setSettingsDraft('usePathAsTagsFallback', false);
      window.saveSettings({ autoAddMissingAuthorsFromCompletedDownloads: false, usePathAsTagsFallback: false });
    });
    await scopedFailure.completeConfigSave(quickSaveIndex++, 500, { message: 'Two settings failed' });
    await scopedPage.waitForFunction(() => !window.getSettingsState().isSaving);
    await scopedPage.evaluate(() => {
      window.saveSettings({ autoAddMissingAuthorsFromCompletedDownloads: false });
    });
    await scopedFailure.completeConfigSave(quickSaveIndex++);
    await scopedPage.waitForFunction(() => !window.getSettingsState().isSaving);
    assert.equal(await scopedPage.evaluate(() => window.getSettingsState().saveError.responseJSON.message), 'Two settings failed', 'A partial retry retains the remaining failed setting error');
    await scopedPage.evaluate(() => {
      window.saveSettings({ usePathAsTagsFallback: false });
    });
    await scopedFailure.completeConfigSave(quickSaveIndex++);
    await scopedPage.waitForFunction(() => !window.getSettingsState().isSaving);
    assert.equal(await scopedPage.evaluate(() => window.getSettingsState().saveError), null, 'Independent successful retries clear all outstanding failed fields');
    await scopedPage.evaluate(() => {
      window.settingsSaveResults = [];
      const original = window.XMLHttpRequest;
      window.XMLHttpRequest = function() {
        throw new Error('Request construction failed');
      };
      window.saveSettings({ defaultEbookRootFolderPath: '/data/retry' }).fail(() => window.settingsSaveResults.push('construction:fail'));
      window.XMLHttpRequest = original;
      window.saveSettings({ defaultAudiobookRootFolderPath: '/data/unsaved' }).done(() => window.settingsSaveResults.push('next:done'));
    });
    await scopedFailure.completeConfigSave(quickSaveIndex++);
    await scopedPage.waitForFunction(() => window.settingsSaveResults.includes('next:done'));
    assert.deepEqual(await scopedPage.evaluate(() => window.settingsSaveResults), ['construction:fail', 'next:done'], 'Request construction failure settles and permits the next save');
    await scopedFailure.context.close();

    const reset = await openFixture(browser, strings, cssPath, { viewport: { width: 1280, height: 900 } });
    const resetPage = reset.page;
    await resetPage.evaluate(() => window.renderSettingsIsolationFixture('general-settings'));
    await resetPage.waitForFunction(() => window.getGeneralSettingsState()?.isPopulated);
    let reloads = 0;
    resetPage.on('framenavigated', (frame) => {
      if (frame === resetPage.mainFrame()) {
        reloads++;
      }
    });
    const command = { id: 801, name: 'ResetApiKey', trigger: 'manual', body: { name: 'ResetApiKey', sendUpdatesToClient: true } };
    await resetPage.evaluate((value) => window.receiveSettingsCommand(value), { ...command, status: 'started' });
    const pinnedFailure = 'API key is set by an environment variable. Change it in your container configuration.';
    await resetPage.evaluate((value) => window.receiveSettingsCommand(value), { ...command, status: 'failed', message: pinnedFailure });
    await resetPage.getByText(pinnedFailure, { exact: true }).waitFor();
    await resetPage.evaluate(() => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    assert.equal(reloads, 0, 'A failed reset must retain its error instead of reloading');
    await resetPage.evaluate((value) => window.receiveSettingsCommand(value), { ...command, id: 802, status: 'completed' });
    await resetPage.evaluate(() => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    assert.equal(reloads, 0, 'A reset completed by another page must not reload this page');
    await Promise.all([
      resetPage.waitForResponse((response) => new URL(response.url()).pathname.endsWith('/command')),
      resetPage.evaluate(() => window.confirmGeneralReset())
    ]);
    await resetPage.waitForFunction(() => window.getSettingsCommands().some((value) => value.id === 803));
    await resetPage.evaluate((value) => window.receiveSettingsCommand(value), { ...command, id: 804, status: 'completed' });
    await resetPage.evaluate(() => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    assert.equal(reloads, 0, 'An unrelated completion during the local reset must not reload');
    await Promise.all([
      resetPage.waitForEvent('load'),
      resetPage.evaluate((value) => window.receiveSettingsCommand(value), { ...command, id: 803, status: 'completed' })
    ]);
    assert.equal(reloads, 1, 'The page which sent a successful reset reloads without a started notification');
    await reset.context.close();

    for (const early of [false, true]) {
      for (const status of ['completed', 'failed']) {
        const fast = await openFixture(browser, strings, cssPath, { viewport: { width: 1280, height: 900 } });
        try {
          await fast.page.evaluate(() => window.renderSettingsIsolationFixture('general-settings'));
          await fast.page.waitForFunction(() => window.getGeneralSettingsState()?.isPopulated);
          fast.setResetReply(early ? 'queued' : status, early ? status : null);
          const returned = await fast.page.evaluate(() => window.executeSettingsCommand({ name: 'ResetApiKey' }));
          assert.equal(returned.id, 803, 'executeCommand returns the command identity expected by Calendar callers');
          const observed = await fast.page.evaluate(() => ({
            callbacks: window.finishedSettingsCommands,
            command: window.getSettingsCommands().find((value) => value.id === 803)
          }));
          assert.equal(observed.callbacks.length, 1, `${status}, early=${early}: callback settles once`);
          assert.equal(observed.callbacks[0].status, status);
          assert.equal(observed.command.status, status, 'A late queued POST cannot undo terminal state');
          await fast.page.evaluate((value) => window.receiveSettingsCommand(value), { id: 803, name: 'ResetApiKey', status });
          assert.equal(await fast.page.evaluate(() => window.finishedSettingsCommands.length), 1, 'A repeated terminal event cannot invoke the callback twice');
        } finally {
          await fast.context.close();
        }
      }
    }
    console.log('Reset origin, exact command ID, fast terminal POST, early SignalR and returned command identity passed');

    const restart = await openFixture(browser, strings, cssPath, { viewport: { width: 1280, height: 900 } });
    const restartPage = restart.page;
    await restartPage.evaluate(() => window.renderSettingsIsolationFixture('general-settings'));
    await restartPage.waitForFunction(() => window.getGeneralSettingsState()?.isPopulated);
    restart.holdConfigSaves();
    await restartPage.evaluate(() => {
      window.setGeneralDraft('port', 9999);
      window.saveGeneralSetting({}, false);
      window.saveGeneralSetting({ logLevel: 'debug' });
    });
    await restartPage.waitForTimeout(100);
    assert.equal(restart.generalWrites.length, 1);
    const restartNow = restartPage.getByRole('button', { name: strings.RestartNow, exact: true });
    assert.equal(await restartNow.count(), 0, 'The restart prompt must wait for a successful port save');
    await restart.completeConfigSave(0);
    await restartNow.waitFor();
    await restartPage.waitForTimeout(100);
    assert.equal(restart.generalWrites.length, 2);
    assert.equal(restart.generalWrites[1].port, 9999);
    await restart.completeConfigSave(1);
    await restartPage.waitForFunction(() => !window.getGeneralSettingsState().isSaving);
    assert.equal(await restartNow.isVisible(), true, 'A following log-level save must preserve the pending restart prompt');
    await restartPage.evaluate(() => {
      window.saveGeneralSetting({ logLevel: 'trace' });
    });
    await restartPage.waitForTimeout(100);
    await restart.completeConfigSave(2, 500, { message: 'Save failed for this test' });
    await restartPage.waitForFunction(() => !window.getGeneralSettingsState().isSaving);
    assert.equal(await restartNow.isVisible(), true, 'A failed unrelated save must preserve the pending restart prompt');
    await restartPage.getByRole('button', { name: strings.IllRestartLater, exact: true }).click();
    await restartNow.waitFor({ state: 'hidden' });
    await restartPage.evaluate(() => {
      window.saveGeneralSetting({ logLevel: 'info' });
    });
    await restartPage.waitForTimeout(100);
    await restart.completeConfigSave(3);
    await restartPage.waitForFunction(() => !window.getGeneralSettingsState().isSaving);
    assert.equal(await restartNow.count(), 0, 'An unrelated save must not reopen a dismissed restart prompt');
    await restartPage.evaluate(() => {
      window.setGeneralDraft('port', 7777);
      window.saveGeneralSetting({}, false);
    });
    await restartPage.waitForTimeout(100);
    await restart.completeConfigSave(4);
    await restartNow.waitFor();
    assert.equal(await restartNow.isVisible(), true, 'A later successful port save must reopen the restart prompt');
    await restartPage.getByRole('button', { name: strings.IllRestartLater, exact: true }).click();
    await restartNow.waitFor({ state: 'hidden' });
    assert.deepEqual(restart.errors, ['Failed to load resource: the server responded with a status of 500 (Internal Server Error)']);
    await restart.context.close();

    console.log('Media-management cover-scope browser check passed: inline Calculate before Save, no implicit scans or saves, retained estimates, confirmation/cancel, errors/retry, save/unmount cancellation, scope transitions, mobile layout, inline matching Save/revert, Strict round trips on both surfaces, draft cleanup, root/proxy-modal and log-level save isolation, ordered normal/scoped full PUTs, failure continuation, server normalization, later draft preservation, and mounted General Settings restart-prompt lifecycle.');
  } catch (error) {
    console.error(error);
    throw error;
  } finally {
    await browser.close();
    fs.rmSync(output, { recursive: true, force: true });
  }
})().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
