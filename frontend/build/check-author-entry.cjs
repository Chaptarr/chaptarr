/* Real Author Details CTA, connected shared modal, Redux and HTTP; no live server. */
const assert = require('node:assert/strict');
const { EventEmitter, once } = require('node:events');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const webpack = require('webpack');
const { chromium } = require('playwright');

const root = path.resolve(__dirname, '../..');
const artifacts = process.env.CHAPTARR_LAYOUT_ARTIFACTS;
const output = fs.mkdtempSync(path.join(os.tmpdir(), 'chaptarr-author-entry-'));
const config = require('./webpack.config')({ production: true });

config.entry = { fixture: path.join(__dirname, 'author-search-fixture.js') };
config.output = { ...config.output, path: output, filename: 'fixture.js' };
config.optimization = { minimize: false };
config.plugins = config.plugins.filter((plugin) => ['DefinePlugin', 'MiniCssExtractPlugin'].includes(plugin.constructor.name));
for (const rule of config.module.rules) {
  if (Array.isArray(rule.use)) {
    rule.use = rule.use.filter((loader) => !String(typeof loader === 'string' ? loader : loader.loader).includes('css-modules-typescript-loader'));
  }
}

(async() => {
  let browser = null;
  try {
    const compiler = webpack(config);
    await new Promise((resolve, reject) => compiler.run((error, stats) => {
      compiler.close(() => {});
      if (error || stats.hasErrors()) {
        reject(error || new Error(stats.toString({ all: false, errors: true })));
      } else {
        resolve();
      }
    }));
    browser = await chromium.launch();
    const page = await browser.newPage();
    const strings = JSON.parse(fs.readFileSync(path.join(root, 'src/NzbDrone.Core/Localization/Core/en.json')));
    const css = fs.readdirSync(path.join(output, 'Content')).find((name) => name.endsWith('.css'));
    const errors = [];
    const expectedErrors = [];
    const expectedObservedErrors = [];
    const requests = [];
    const receipts = [];
    let author = null;
    let nextImport = 'success';
    let heldRoute = null;
    let heldAuthorRoute = null;
    let holdAuthor = false;
    let heldRootRoute = null;
    let holdRoots = false;
    const heldResponses = new EventEmitter();
    page.on('pageerror', (error) => errors.push({ type: 'pageerror', message: error.message, stack: error.stack }));
    page.on('console', (message) => {
      if (message.type() !== 'error') {
        return;
      }
      const expectedIndex = expectedErrors.findIndex((error) => error.url === message.location().url && message.text().includes(`status of ${error.status}`));
      if (expectedIndex >= 0) {
        expectedObservedErrors.push({ ...expectedErrors.splice(expectedIndex, 1)[0], message: message.text() });
      } else {
        errors.push({ type: 'console', message: message.text() });
      }
    });
    page.on('requestfailed', (request) => errors.push({ type: 'requestfailed', url: request.url(), failure: request.failure()?.errorText }));
    const completeImport = async(route) => {
      const body = route.request().postDataJSON();
      const side = body.mediaType;
      author = {
        ...author,
        [`${side}RootFolderPath`]: body.rootFolder,
        [`${side}Path`]: `${body.rootFolder}/${author.authorName}`,
        [`${side}QualityProfileId`]: body.qualityProfileId,
        [`${side}MetadataProfileId`]: body.metadataProfileId,
        [`${side}Monitored`]: body[`${side}Monitored`],
        [`${side}Tags`]: body.tags,
        ...(body.syncMonitoredAcrossFormats == null ? {} : { syncMonitoredAcrossFormats: body.syncMonitoredAcrossFormats }),
        ...Object.fromEntries(['audiobookMonitorNewItems', 'ebookMonitorNewItems'].filter((field) => body[field] != null).map((field) => [field, body[field]])),
        hydrationWarning: 'Fixture hydration warning'
      };
      await route.fulfill({ json: author });
    };
    await page.route('**/*', async(route) => {
      const request = route.request();
      const url = new URL(request.url());
      const pathname = url.pathname.toLowerCase();
      if (pathname.startsWith('/api/')) {
        requests.push({ method: request.method(), path: url.pathname, query: Object.fromEntries(url.searchParams), body: request.postData() ? request.postDataJSON() : null });
      }
      if (pathname === '/api/v1/book/monitor' && request.method() === 'PUT') {
        const payload = request.postDataJSON();
        const current = await page.evaluate(() => window.readSeriesMembershipBooks());
        return route.fulfill({ json: current.filter((book) => payload.bookIds.includes(book.id)).map((book) => ({
          id: book.id, monitored: payload.monitored,
          audiobookMonitored: book.mediaType === 'audiobook' ? payload.monitored : book.audiobookMonitored,
          ebookMonitored: book.mediaType === 'ebook' ? payload.monitored : book.ebookMonitored
        })) });
      }
      if ((/^\/api\/v1\/author\/\d+$/).test(pathname) && request.method() === 'PUT') {
        return route.fulfill({ json: { ...author, ...request.postDataJSON() } });
      }
      if (pathname === '/api/v1/author/10' && request.method() === 'GET') {
        if (holdAuthor) {
          heldAuthorRoute = route;
          heldResponses.emit('author');
          return;
        }
        return route.fulfill({ json: author });
      }
      if (pathname === '/api/v1/author/import') {
        if (nextImport === 'hold') {
          heldRoute = route;
          return;
        }
        return completeImport(route);
      }
      if (pathname === '/api/v1/author/10/selectedmediatype/audiobook' || pathname === '/api/v1/author/10/selectedmediatype/ebook') {
        return route.fulfill({ json: {} });
      }
      if (pathname === '/api/v1/rootfolder') {
        if (holdRoots) {
          heldRootRoute = route;
          heldResponses.emit('roots');
          return;
        }
        return route.fulfill({ json: await page.evaluate(() => window.addAuthorFixture.roots) });
      }
      if (pathname === '/api/v1/qualityprofile' || pathname === '/api/v1/metadataprofile') {
        const profiles = await page.evaluate((kind) => window.addAuthorFixture[kind], pathname.endsWith('/qualityprofile') ? 'qualityProfiles' : 'metadataProfiles');
        const mediaType = url.searchParams.get('mediaType');
        return route.fulfill({ json: profiles.filter((profile) => !mediaType || profile.profileType === (mediaType === 'audiobook' ? 1 : 2)) });
      }
      if (pathname === '/api/v1/book' && request.method() === 'POST') {
        return route.fulfill({ status: 202, json: { pendingId: 1, message: 'Fixture queued' } });
      }
      if (pathname === '/api/v1/release' && url.searchParams.get('existing') === 'true') {
        return route.fulfill({ contentType: 'application/json', body: 'null' });
      }
      if (pathname === '/api/v1/filesystem') {
        return route.fulfill({ json: { parent: null, directories: [], files: [] } });
      }
      if (pathname === '/api/v1/book' || pathname === '/api/v1/series' || pathname === '/api/v1/bookfile' || pathname === '/api/v1/queue/details') {
        return route.fulfill({ json: [] });
      }
      if (pathname === '/api/v1/author/10/size/audiobook' || pathname === '/api/v1/author/10/size/ebook') {
        return route.fulfill({ json: { sizeOnDisk: 0 } });
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
      if (pathname === '/content/images/author-placeholder-muted.png') {
        return route.fulfill({ path: path.join(root, 'frontend/src/Content/Images/author-placeholder-muted.png'), contentType: 'image/png' });
      }
      if (pathname === '/content/images/chaptarr-logo.svg') {
        return route.fulfill({ path: path.join(root, 'frontend/src/Content/Images/chaptarr-logo.svg'), contentType: 'image/svg+xml' });
      }
      if (pathname === '/') {
        return route.fulfill({ contentType: 'text/html', body: '<!doctype html><meta charset="utf-8"><style>html,body,#root{height:100%;margin:0}</style><link rel="stylesheet" href="/style.css"><div id="root"></div><div id="portal-root"></div><script>window.Chaptarr={isProduction:true,instanceName:"Fixture",apiRoot:"/api/v1",apiKey:"fixture",urlBase:""};</script><script src="/fixture.js"></script>' });
      }
      errors.push({ type: 'unmatched', method: request.method(), url: request.url() });
      return route.fulfill({ status: 404, body: '' });
    });
    await page.goto('http://author-entry.test/');
    const modal = () => page.locator('#portal-root [class*="AddNewAuthorModalContent-container"]');
    const add = () => page.locator('[class*="AddNewAuthorModalContent-addButton"]');
    const checkbox = (name) => page.locator(`input[name="${name}"]`);
    const setCheckbox = async(name, value) => {
      const input = checkbox(name);
      if (await input.isChecked() !== value) {
        await input.locator('..').click();
      }
      assert.equal(await input.isChecked(), value);
    };
    const choose = async(label, value) => {
      const group = page.locator('#portal-root [class*="FormGroup-group"]').filter({ has: page.locator('[class*="FormLabel-label"]').filter({ hasText: label }) }).first();
      await group.getByRole('button').first().click();
      await page.locator('[class*="EnhancedSelectInput-optionsContainer"]:visible').getByText(value, { exact: true }).click();
    };
    const rememberedDefaults = () => page.evaluate(() => ({
      memory: window.readAddAuthorFixtureState().authorDefaults,
      persisted: JSON.parse(localStorage.getItem('chaptarr')).search.authorDefaults
    }));
    const bookGets = () => requests.filter((request) => request.method === 'GET' && request.path.toLowerCase() === '/api/v1/book');
    const mutations = () => requests.filter((request) => request.method !== 'GET');
    const render = async(missing, theme, width) => {
      const configured = missing === 'ebook' ? 'audiobook' : 'ebook';
      author = {
        id: 10, foreignAuthorId: 'gr:existing-entry-author', authorName: 'Existing Entry Writer', sortNameLastFirst: 'Writer, Existing Entry',
        path: `/fixture/${configured}/Existing Entry Writer`, rootFolderPath: `/fixture/${configured}`,
        audiobookRootFolderPath: null, ebookRootFolderPath: null, audiobookPath: null, ebookPath: null,
        audiobookQualityProfileId: null, ebookQualityProfileId: null, audiobookMetadataProfileId: null, ebookMetadataProfileId: null,
        [`${configured}RootFolderPath`]: `/fixture/${configured === 'audiobook' ? 'audio' : 'ebook'}-alt`,
        [`${configured}Path`]: `/fixture/${configured === 'audiobook' ? 'audio' : 'ebook'}-alt/Existing Entry Writer`,
        [`${configured}QualityProfileId`]: configured === 'audiobook' ? 12 : 14,
        [`${configured}MetadataProfileId`]: configured === 'audiobook' ? 22 : 24,
        [`${configured}Tags`]: [43], [`${configured}Monitored`]: false, [`${configured}MonitorNewItems`]: 'all',
        syncMonitoredAcrossFormats: true, monitored: true, tags: [43], status: 'continuing', images: [], links: [],
        alternateTitles: [], pseudonyms: [], ratings: { votes: 0, value: 0 }, overview: '',
        statistics: { totalBookCount: 0, bookCount: 0, bookFileCount: 0, sizeOnDisk: 0 },
        audiobookStatistics: { totalBookCount: 0, bookCount: 0 }, ebookStatistics: { totalBookCount: 0, bookCount: 0 }
      };
      await page.setViewportSize({ width, height: 1000 });
      await page.evaluate(({ configuredSide, existingAuthor, selectedTheme }) => window.renderAddAuthorFixture(configuredSide, 'author-entry', null, {
        existingAuthor, selectedMediaType: configuredSide, defaultMediaType: configuredSide, theme: selectedTheme,
        authorDefaults: { audiobookRootFolderPath: '/fixture/audio', ebookRootFolderPath: '/fixture/ebook', audiobookTags: [41], ebookTags: [42] }
      }), { configuredSide: configured, existingAuthor: author, selectedTheme: theme });
      try {
        await page.locator('[class*="AuthorDetailsControls-syncAndToggle"]').getByRole('button', { name: missing === 'ebook' ? /eBooks/ : /Audiobooks/ }).click();
      } catch (error) {
        console.error(JSON.stringify({ errors, requests, text: await page.locator('body').innerText() }, null, 2));
        throw error;
      }
      await page.getByRole('button', { name: 'Select root folder', exact: true }).waitFor();
      return configured;
    };
    const open = async(missing) => {
      await page.getByRole('button', { name: 'Select root folder', exact: true }).click();
      await modal().waitFor();
      await page.locator(`select[name="${missing}MetadataProfileId"] option[value="${missing === 'ebook' ? 24 : 22}"]`).waitFor({ state: 'attached' });
      assert.equal(await add().innerText(), missing === 'ebook' ? 'Add Ebooks' : 'Add Audiobooks');
      assert.equal(await page.locator('[class*="ModalHeader-modalHeader"]').innerText(), strings.AddAsFormat.replace('{format}', strings[missing === 'ebook' ? 'Ebooks' : 'Audiobooks']));
    };
    for (const [missing, theme, width] of [['ebook', 'light', 1440], ['audiobook', 'dark', 390]]) {
      const configured = await render(missing, theme, width);
      const remembered = await rememberedDefaults();
      const original = await page.evaluate(() => window.readAuthorEntryState().authors.items[0]);
      await open(missing);
      assert.deepEqual(await rememberedDefaults(), remembered, 'Opening an existing-author dialog never changes remembered Add New defaults');
      await page.evaluate(() => window.updateAuthorEntryDefault('both'));
      assert.equal(await add().innerText(), missing === 'ebook' ? 'Add Ebooks' : 'Add Audiobooks', 'An asynchronous global default must not retarget the missing-side entry');
      assert.equal(await checkbox('syncMonitoredAcrossFormats').isChecked(), true, 'Saved sync overrides root defaults');
      const beforeCancel = { mutations: mutations().length, books: bookGets().length };
      await page.locator('#portal-root [class*="ModalContent-closeButton"]').click();
      await modal().waitFor({ state: 'detached' });
      assert.equal(mutations().length, beforeCancel.mutations, 'Cancel sends no mutation');
      assert.equal(bookGets().length, beforeCancel.books, 'Cancel does not reload the author books');
      assert.deepEqual(await page.evaluate(() => window.readAuthorEntryState().authors.items[0]), original);
      assert.deepEqual(await rememberedDefaults(), remembered, 'Closing without adding preserves remembered defaults');
      await open(missing);
      await page.locator(`select[name="${missing}QualityProfileId"]`).selectOption(missing === 'ebook' ? '14' : '12');
      await page.keyboard.press('Escape');
      await modal().waitFor({ state: 'detached' });
      assert.deepEqual(await rememberedDefaults(), remembered, 'Escape discards the local choice');
      assert.equal(mutations().length, beforeCancel.mutations, 'Escape sends no mutation');
      await open(missing);
      assert.equal(await page.locator(`select[name="${missing}QualityProfileId"]`).inputValue(), missing === 'ebook' ? '13' : '11', 'Reopening restores the root default instead of the abandoned choice');
      const rootPrefix = missing === 'ebook' ? 'ebook' : 'audio';
      await page.locator('[class*="RootFolderSelectInputSelectedValue-path"]').filter({ hasText: `/fixture/${rootPrefix}` }).first().click();
      await page.locator('[class*="RootFolderSelectInputOption-value"]').filter({ hasText: `/fixture/${rootPrefix}-alt` }).click();
      await page.locator(`select[name="${missing}QualityProfileId"]`).selectOption(missing === 'ebook' ? '14' : '12');
      await page.locator(`select[name="${missing}MetadataProfileId"]`).selectOption(missing === 'ebook' ? '24' : '22');
      const selectedProfile = await page.locator(`select[name="${missing}QualityProfileId"]`).evaluate((input) => ({ value: input.value, text: input.selectedOptions[0].textContent }));
      assert.deepEqual(selectedProfile, { value: missing === 'ebook' ? '14' : '12', text: 'None' }, 'A real positive-ID quality profile named None remains a valid manual choice, distinct from an empty/sentinel profile');
      await setCheckbox(`${missing}Monitored`, false);
      const tags = page.locator(`input[name="${missing}Tags"]`);
      await tags.focus();
      await tags.press('Backspace');
      await choose(strings.InitialBookMonitoring, strings.BooksWithFiles);
      await choose(strings.MonitorNewBooks, strings.FutureReleases);
      await setCheckbox('searchForMissingBooks', true);
      assert.equal(await add().isDisabled(), false);
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
      if (artifacts) {
        fs.mkdirSync(artifacts, { recursive: true });
        await page.screenshot({ path: path.join(artifacts, `missing-${missing}-${theme}-${width}.png`), fullPage: true });
        await page.locator('#portal-root [class*="ModalBody-modalScroller"]').evaluate((element) => {
          element.scrollTop = 0;
        });
        await page.screenshot({ path: path.join(artifacts, `missing-${missing}-${theme}-${width}-top.png`), fullPage: true });
      }
      assert.deepEqual(await rememberedDefaults(), remembered, 'All edits to an existing-author dialog stay local');
      const beforeSubmit = requests.length;
      const beforeBookGets = bookGets().length;
      await add().click();
      await modal().waitFor({ state: 'detached' });
      await page.waitForFunction(() => !window.readAuthorEntryState().books.isFetching);
      const posted = requests.slice(beforeSubmit).filter((request) => request.path.toLowerCase() === '/api/v1/author/import');
      assert.equal(posted.length, 1);
      assert.deepEqual(posted[0].body, {
        foreignAuthorId: 'gr:existing-entry-author', authorName: 'Existing Entry Writer', mediaType: missing,
        syncMonitoredAcrossFormats: true, manualFlag: true, searchForMissingBooks: true,
        audiobookMonitorNewItems: 'new', ebookMonitorNewItems: 'new', [`${missing}MonitorExistingMode`]: 'existing',
        [`${missing}Monitored`]: false, rootFolder: `/fixture/${rootPrefix}-alt`,
        qualityProfileId: missing === 'ebook' ? 14 : 12, metadataProfileId: missing === 'ebook' ? 24 : 22,
        monitor: 'existing', tags: []
      });
      const refreshed = bookGets().slice(beforeBookGets);
      assert.equal(refreshed.length, 1, 'Successful close reloads through the existing page populate exactly once');
      assert.deepEqual(refreshed[0].query, { authorId: '10', mediaType: missing });
      const saved = await page.evaluate(() => window.readAuthorEntryState().authors.items[0]);
      for (const suffix of ['RootFolderPath', 'QualityProfileId', 'MetadataProfileId', 'Tags', 'Monitored']) {
        assert.deepEqual(saved[`${configured}${suffix}`], original[`${configured}${suffix}`], `Untouched ${configured}${suffix} must be preserved`);
      }
      assert.equal(saved[`${missing}RootFolderPath`], `/fixture/${rootPrefix}-alt`);
      assert.equal(saved[`${configured}MonitorNewItems`], 'new');
      assert.deepEqual(await rememberedDefaults(), remembered, 'Successful hydration also preserves Add New defaults');
      assert.equal(await page.getByRole('button', { name: 'Select root folder', exact: true }).count(), 0);
      assert.equal(await page.evaluate(() => window.readAuthorEntryState().selectedMediaType), missing);
      receipts.push({ case: 'real-details-missing-side', missing, theme, width, selectedProfile, original, body: posted[0].body, refreshed, saved });
      await page.evaluate(() => window.openNewAuthorEntryFixture());
      await modal().waitFor();
      const newRoots = await page.locator('[class*="RootFolderSelectInputSelectedValue-path"]').allTextContents();
      assert(newRoots.some((value) => value.includes(`/fixture/${configured === 'audiobook' ? 'audio' : 'ebook'}`)), `Add New retains its own root: ${JSON.stringify(newRoots)}`);
      assert(newRoots.every((value) => !value.includes('-alt')), 'No existing-author root leaks into the next Add New dialog');
      await page.locator('#portal-root [class*="ModalContent-closeButton"]').click();
      await modal().waitFor({ state: 'detached' });
      receipts.push({ case: 'existing-author-draft-isolation', missing, remembered, newRoots });
    }
    assert.deepEqual(errors, [], 'Normal entry, Cancel and success have zero console/page/network errors');
    await render('ebook', 'light', 1440);
    await open('ebook');
    await choose(strings.MonitorNewBooks, strings.FutureReleases);
    const beforeFailure = { remembered: await rememberedDefaults(), mutations: mutations().length, books: bookGets().length, author: await page.evaluate(() => window.readAuthorEntryState().authors.items[0]) };
    nextImport = 'hold';
    await add().click();
    await page.waitForFunction(() => window.readAuthorEntryState().search.isAdding);
    assert.equal(await add().isDisabled(), true, 'Held submission blocks a duplicate Add');
    assert.ok(heldRoute);
    expectedErrors.push({ url: heldRoute.request().url(), status: 400 });
    await heldRoute.fulfill({ status: 400, json: [{ propertyName: 'ebookMonitorNewItems', errorMessage: 'Fixture import validation failure' }] });
    await page.waitForFunction(() => !window.readAuthorEntryState().search.isAdding);
    assert.equal(await add().isDisabled(), false, 'Failure leaves the chosen settings usable');
    assert.equal(await modal().count(), 1);
    assert.equal(bookGets().length, beforeFailure.books, 'Failure does not reload books');
    assert.deepEqual(await rememberedDefaults(), beforeFailure.remembered, 'Failure retains the draft without persisting its choices');
    assert.deepEqual(await page.evaluate(() => window.readAuthorEntryState().authors.items[0]), beforeFailure.author);
    nextImport = 'success';
    await add().click();
    await modal().waitFor({ state: 'detached' });
    await page.waitForFunction(() => !window.readAuthorEntryState().books.isFetching);
    assert.equal(mutations().length - beforeFailure.mutations, 2, 'One failed request and one explicit retry only');
    assert.equal(bookGets().length - beforeFailure.books, 1);
    assert.deepEqual(await rememberedDefaults(), beforeFailure.remembered, 'Retry uses the draft without persisting existing-author choices');
    assert.deepEqual(expectedErrors, []);
    for (const [mediaType, theme, width] of [['audiobook', 'light', 1440], ['ebook', 'dark', 390]]) {
      await page.setViewportSize({ width, height: 1000 });
      await page.evaluate(({ side, selectedTheme }) => window.renderSeriesMembershipFixture(side, selectedTheme), { side: mediaType, selectedTheme: theme });
      const firstGroup = page.locator('[data-series-fixture="first"]');
      const secondGroup = page.locator('[data-series-fixture="second"]');
      const rowTitles = () => firstGroup.locator('tbody tr').evaluateAll((rows) => rows.map((row) => row.querySelector('a').textContent));
      const ascending = ['Position 0.25', 'Position 1', 'Shared membership title', 'Position 2.02', 'Position 2.2', 'Position 10', 'Position 序章'];
      assert.deepEqual(await rowTitles(), ascending, 'Series ordering uses the numeric API value followed by the original provider text');
      await firstGroup.getByRole('columnheader', { name: 'Number', exact: true }).click();
      assert.deepEqual(await rowTitles(), [...ascending].reverse(), 'Descending reverses the same backend order and original-text tie break');
      const before = await page.evaluate(() => window.readSeriesMembershipBooks());
      const targetId = mediaType === 'audiobook' ? 501 : 601;
      const otherId = mediaType === 'audiobook' ? 601 : 501;
      const firstRow = firstGroup.locator('tbody tr').filter({ hasText: 'Shared membership title' });
      const secondRow = secondGroup.locator('tbody tr').filter({ hasText: 'Shared membership title' });
      const monitorWrites = () => requests.filter((request) => request.path === '/api/v1/book/monitor');
      const count = monitorWrites().length;
      await firstRow.getByTitle('Not monitored - Click to monitor', { exact: true }).click();
      await secondRow.getByTitle('Monitored - Click to stop monitoring', { exact: true }).waitFor();
      assert.equal(await firstRow.getByTitle('Monitored - Click to stop monitoring', { exact: true }).count(), 1);
      assert.deepEqual(monitorWrites().slice(count).map(({ body }) => body), [{ bookIds: [targetId], monitored: true }]);
      const after = await page.evaluate(() => window.readSeriesMembershipBooks());
      assert.deepEqual(after.find(({ id }) => id === otherId), before.find(({ id }) => id === otherId), 'Monitoring a membership never changes the other media record');
      await secondRow.getByTitle('Monitored - Click to stop monitoring', { exact: true }).click();
      await firstRow.getByTitle('Not monitored - Click to monitor', { exact: true }).waitFor();
      assert.deepEqual(monitorWrites().slice(count).map(({ body }) => body), [
        { bookIds: [targetId], monitored: true }, { bookIds: [targetId], monitored: false }
      ], 'Either series controls the same selected-format Book row');
      const restored = await page.evaluate(() => window.readSeriesMembershipBooks());
      assert.deepEqual(restored.find(({ id }) => id === otherId), before.find(({ id }) => id === otherId));
      receipts.push({ case: 'Shared series membership monitoring and canonical position sort', mediaType, targetId, otherId, writes: monitorWrites().slice(count) });
    }
    assert.deepEqual(errors, []);
    receipts.push({ case: 'held-failure-explicit-retry', beforeFailure, requests: requests.slice(-10) });
    for (const close of ['Cancel', 'X', 'Escape', 'Delete']) {
      const first = { ...author, syncMonitoredAcrossFormats: false };
      const second = { ...first, id: 11, authorName: 'Second Fixture Writer', foreignAuthorId: 'gr:second-fixture' };
      await page.evaluate((a) => window.openEditAuthorEntryFixture(a), first);
      await page.locator('select[name="audiobookQualityProfileId"] option[value="11"]').waitFor({ state: 'attached' });
      await page.locator('select[name="audiobookQualityProfileId"]').selectOption('11');
      const tags = page.locator('input[name="audiobookTags"]');
      await tags.focus();
      await tags.press('Backspace');
      await setCheckbox('audiobookMonitored', true);
      const changed = await page.evaluate(() => window.readAuthorSyncState().pendingChanges);
      assert.equal(changed.audiobookQualityProfileId, 11);
      assert.deepEqual(changed.audiobookTags, []);
      assert.equal(changed.audiobookMonitored, true);
      const beforeClose = mutations().length;
      if (close === 'Escape') {
        await page.keyboard.press('Escape');
      } else if (close === 'X') {
        await page.locator('#portal-root [class*="ModalContent-closeButton"]').click();
      } else {
        await page.getByRole('button', { name: close, exact: true }).click();
      }
      await page.locator('select[name="audiobookQualityProfileId"]').waitFor({ state: 'detached' });
      assert.deepEqual(await page.evaluate(() => window.readAuthorSyncState().pendingChanges), {}, `${close} clears the author draft`);
      assert.equal(mutations().length, beforeClose, `${close} sends no PUT`);
      await page.evaluate((a) => window.openEditAuthorEntryFixture(a), second);
      await page.locator('select[name="audiobookQualityProfileId"] option[value="12"]').waitFor({ state: 'attached' });
      assert.equal(await page.locator('select[name="audiobookQualityProfileId"]').inputValue(), '12');
      assert.equal(await checkbox('audiobookMonitored').isChecked(), false);
      assert.deepEqual(await page.evaluate(() => window.readAuthorSyncState().pendingChanges), {});
      await page.getByRole('button', { name: 'Save', exact: true }).click();
      await page.waitForFunction(() => !window.readAuthorSyncState().isSaving);
      const sent = mutations().slice(beforeClose);
      assert.equal(sent.length, 1);
      assert.equal(sent[0].method, 'PUT');
      assert.equal(sent[0].path, '/api/v1/author/11');
      assert.equal(sent[0].body.audiobookQualityProfileId, 12);
      assert.equal(sent[0].body.audiobookMonitored, false);
      assert.deepEqual(sent[0].body.audiobookTags, [43]);
      receipts.push({ case: 'edit-author-draft-discarded', close, changed, sent });
    }
    const selectedBook = {
      foreignBookId: 'hc:8100', foreignAuthorId: 'hc:8000', title: 'Selected Search Book', authorName: 'Selected Search Writer',
      images: [], links: [], ratings: { votes: 0, value: 0 }, searchProvider: 'hardcover',
      author: { foreignAuthorId: 'hc:8000', authorName: 'Selected Search Writer' },
      editions: [{ foreignEditionId: 'hc:edition:8200', title: 'Selected Search Book', monitored: true, manualAdd: false }],
      localAudiobookBooks: [], localEbookBooks: []
    };
    for (const side of ['audiobook', 'ebook', 'both']) {
      for (const rootPolicy of ['all', 'none']) {
        await page.evaluate(({ side: selectedSide, selectedBook: requestedBook, rootPolicy: policy }) => window.renderAddAuthorFixture(selectedSide, 'captured-book', requestedBook, {
          roots: [
            { audiobookMonitored: false, audiobookMonitorExistingMode: policy, audiobookMonitorNewItems: policy },
            { ebookMonitored: false, ebookMonitorExistingMode: policy, ebookMonitorNewItems: policy }
          ]
        }), { side, selectedBook, rootPolicy });
        await page.locator('[class*="AddNewBookSearchResult-underlay"]').click();
        const addBookButton = page.locator('[class*="AddNewBookModalContent-addButton"]');
        await addBookButton.waitFor();
        const before = requests.length;
        await addBookButton.click();
        await page.waitForFunction(() => window.readAddAuthorFixtureState().isQueued);
        const sent = requests.slice(before).filter((request) => request.method === 'POST' && request.path === '/api/v1/book');
        const expectedSides = side === 'both' ? ['audiobook', 'ebook'] : [side];
        assert.equal(sent.length, expectedSides.length);
        for (const [index, media] of expectedSides.entries()) {
          const body = sent[index].body;
          assert.equal(body.author.addOptions.monitor, 'specificBook');
          assert.deepEqual(body.author.addOptions.booksToMonitor, ['hc:8100']);
          assert.equal(body.monitored, true);
          assert.equal(body.author[`${media}Monitored`], true);
          assert.equal(body.author[`${media}MonitorNewItems`], 'none');
          assert.deepEqual(body.author[`${media}Tags`], [media === 'audiobook' ? 41 : 42]);
          assert.equal(body.author[`${media}QualityProfileId`], media === 'audiobook' ? 11 : 13);
          assert.equal(body.author[`${media}MetadataProfileId`], media === 'audiobook' ? 21 : 23);
        }
        receipts.push({ case: 'Add Book defaults to only the requested row', side, rootPolicy, sent });
      }
    }
    for (const side of ['audiobook', 'ebook', 'both']) {
      for (const monitored of [true, false]) {
        const savedAuthor = {
          id: 10, foreignAuthorId: 'gr:7000', authorName: 'Selected Search Writer',
          audiobookRootFolderPath: '/fixture/audio-alt', ebookRootFolderPath: '/fixture/ebook-alt',
          audiobookQualityProfileId: 12, ebookQualityProfileId: 14,
          audiobookMetadataProfileId: 22, ebookMetadataProfileId: 24,
          audiobookMonitored: false, ebookMonitored: true,
          audiobookMonitorNewItems: 'new', ebookMonitorNewItems: 'all',
          audiobookTags: [43], ebookTags: [], tags: [43]
        };
        const book = { ...selectedBook, author: { ...selectedBook.author, id: 10 } };
        await page.setViewportSize({ width: side === 'both' ? 390 : 1280, height: 900 });
        await page.evaluate(({ selectedSide, requestedBook, saved }) => window.renderAddAuthorFixture(selectedSide, 'captured-book', requestedBook, {
          existingAuthor: saved, theme: selectedSide === 'ebook' ? 'dark' : 'light'
        }), { selectedSide: side, requestedBook: book, saved: savedAuthor });
        await page.locator('[class*="AddNewBookSearchResult-underlay"]').click();
        const sides = side === 'both' ? ['audiobook', 'ebook'] : [side];
        for (const media of sides) {
          assert.equal(await page.locator(`select[name="${media}QualityProfileId"]`).count(), 0, 'Configured-side settings are a summary, not ignored edits');
          await setCheckbox(`${media}MonitorThisBook`, monitored);
          assert((await page.locator('#portal-root').innerText()).includes(savedAuthor[`${media}RootFolderPath`]));
        }
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth), false);
        if (artifacts && monitored) {
          await page.screenshot({ path: path.join(artifacts, `configured-${side}.png`), fullPage: true });
        }
        const before = requests.length;
        await page.locator('[class*="AddNewBookModalContent-addButton"]').click();
        await page.waitForFunction(() => window.readAddAuthorFixtureState().isQueued);
        const sent = requests.slice(before).filter((request) => request.method === 'POST' && request.path === '/api/v1/book');
        assert.equal(sent.length, sides.length);
        for (const [index, media] of sides.entries()) {
          const body = sent[index].body;
          assert.equal(body.monitored, monitored);
          assert.equal(body[`${media}Monitored`], monitored);
          assert.equal(body.author.addOptions.monitor, monitored ? 'specificBook' : 'none');
          for (const suffix of ['RootFolderPath', 'QualityProfileId', 'MetadataProfileId', 'Monitored', 'MonitorNewItems', 'Tags']) {
            assert.deepEqual(body.author[`${media}${suffix}`], savedAuthor[`${media}${suffix}`], `${side}: actual saved ${media}${suffix}`);
          }
          assert.equal(typeof body.author[`${media}QualityProfileId`], 'number');
          assert.equal(typeof body.author[`${media}MetadataProfileId`], 'number');
          assert.equal(typeof body.author[`${media}Monitored`], 'boolean');
          assert(body.author[`${media}Tags`].every((id) => typeof id === 'number'));
        }
        assert.equal(requests.slice(before).filter((request) => request.method === 'PUT').length, 0);
        receipts.push({ case: 'configured-side-alias-and-monitor-choice', side, monitored, sent });
      }
    }
    for (const fail of [false, true]) {
      author = {
        id: 10, foreignAuthorId: 'gr:7000', authorName: 'Selected Search Writer',
        audiobookRootFolderPath: '/fixture/audio-alt', audiobookQualityProfileId: 12,
        audiobookMetadataProfileId: 22, audiobookMonitored: false, audiobookMonitorNewItems: 'none', audiobookTags: []
      };
      holdAuthor = true;
      heldAuthorRoute = null;
      await page.evaluate((requestedBook) => window.renderAddAuthorFixture('audiobook', 'captured-book', requestedBook),
        { ...selectedBook, author: { ...selectedBook.author, id: 10 } });
      const before = requests.length;
      const authorRequested = once(heldResponses, 'author', { signal: AbortSignal.timeout(30000) });
      await page.locator('[class*="AddNewBookSearchResult-underlay"]').click();
      await authorRequested;
      await page.waitForFunction(() => window.readAuthorSyncState().isFetching);
      const button = page.locator('[class*="AddNewBookModalContent-addButton"]');
      assert(await button.isDisabled(), 'Wait for the identified local author; never substitute defaults');
      assert.equal(await page.locator('select[name="audiobookQualityProfileId"]').count(), 0);
      assert(heldAuthorRoute, 'The author request must reach the held response handler');
      holdAuthor = false;
      if (fail) {
        expectedErrors.push({ url: heldAuthorRoute.request().url(), status: 503 });
        await heldAuthorRoute.fulfill({ status: 503, json: { message: 'Fixture unavailable' } });
        await page.waitForFunction(() => !!window.readAuthorSyncState().error);
        assert(await button.isDisabled());
        assert((await page.locator('#portal-root').innerText()).includes(strings.UnableToLoadAuthor));
        await page.getByRole('button', { name: 'Close', exact: true }).click();
        assert.equal(requests.slice(before).filter((request) => request.method === 'POST' || request.method === 'PUT').length, 0);
        await page.locator('[class*="AddNewBookSearchResult-underlay"]').click();
      } else {
        await heldAuthorRoute.fulfill({ json: author });
      }
      await page.waitForFunction(() => window.readAuthorSyncState().items.some((item) => item.id === 10));
      assert.equal(await button.isDisabled(), false);
      assert((await page.locator('#portal-root').innerText()).includes('/fixture/audio-alt'));
      await page.getByRole('button', { name: 'Close', exact: true }).click();
      assert.equal(requests.slice(before).filter((request) => request.method === 'POST' || request.method === 'PUT').length, 0);
      receipts.push({ case: 'identified author loading, failure and reopen', fail });
    }
    for (const fail of [false, true]) {
      holdRoots = true;
      heldRootRoute = null;
      await page.evaluate((requestedBook) => window.renderAddAuthorFixture('audiobook', 'captured-book', requestedBook, { rootsPopulated: false }), selectedBook);
      const rootsRequested = once(heldResponses, 'roots', { signal: AbortSignal.timeout(30000) });
      await page.locator('[class*="AddNewBookSearchResult-underlay"]').click();
      await rootsRequested;
      assert(await page.locator('[class*="AddNewBookModalContent-addButton"]').isDisabled(), 'Wait for root defaults');
      assert(heldRootRoute, 'The root folder request must reach the held response handler');
      holdRoots = false;
      if (fail) {
        expectedErrors.push({ url: heldRootRoute.request().url(), status: 503 });
        await heldRootRoute.fulfill({ status: 503, json: { message: 'Fixture unavailable' } });
        await page.getByText(strings.UnableToLoadRootFolders, { exact: true }).waitFor();
        assert(await page.locator('[class*="AddNewBookModalContent-addButton"]').isDisabled());
        await page.getByRole('button', { name: 'Close', exact: true }).click();
        await page.locator('[class*="AddNewBookSearchResult-underlay"]').click();
      } else {
        await heldRootRoute.fulfill({ json: await page.evaluate(() => window.addAuthorFixture.roots) });
      }
      await page.locator('select[name="audiobookQualityProfileId"] option[value="11"]').waitFor({ state: 'attached' });
      assert.equal(await page.locator('select[name="audiobookQualityProfileId"]').inputValue(), '11');
      await page.getByRole('button', { name: 'Close', exact: true }).click();
      receipts.push({ case: 'Add Book waits for root defaults and reports failures', fail });
    }
    for (const configured of ['audiobook', 'ebook']) {
      const missing = configured === 'audiobook' ? 'ebook' : 'audiobook';
      const savedAuthor = {
        id: 10, foreignAuthorId: 'gr:7000', authorName: 'Selected Search Writer',
        [`${configured}RootFolderPath`]: `/fixture/${configured === 'ebook' ? 'ebook' : 'audio'}-alt`,
        [`${configured}QualityProfileId`]: configured === 'ebook' ? 14 : 12,
        [`${configured}MetadataProfileId`]: configured === 'ebook' ? 24 : 22,
        [`${configured}Monitored`]: false, [`${configured}MonitorNewItems`]: 'new', [`${configured}Tags`]: [43]
      };
      await page.setViewportSize({ width: 1280, height: 900 });
      await page.evaluate(({ requestedBook, saved }) => window.renderAddAuthorFixture('both', 'captured-book', requestedBook, {
        existingAuthor: saved
      }), { requestedBook: { ...selectedBook, author: { ...selectedBook.author, id: 10 } }, saved: savedAuthor });
      await page.locator('[class*="AddNewBookSearchResult-underlay"]').click();
      await page.locator(`select[name="${missing}QualityProfileId"]`).selectOption(missing === 'ebook' ? '14' : '12');
      assert.equal(await page.locator(`input[name="${missing}RootFolderPath_display"]`).count(), 0);
      assert.equal(await page.locator(`input[name="${configured}MonitorThisBook"]`).count(), 1);
      const before = requests.length;
      await page.locator('[class*="AddNewBookModalContent-addButton"]').click();
      await page.waitForFunction(() => window.readAddAuthorFixtureState().isQueued);
      const sent = requests.slice(before).filter((request) => request.method === 'POST' && request.path === '/api/v1/book');
      assert.equal(sent.length, 2);
      const addedMissing = sent.find((request) => request.body.mediaType === missing).body;
      assert.equal(addedMissing.author[`${missing}RootFolderPath`], missing === 'ebook' ? '/fixture/ebook' : '/fixture/audio');
      assert.equal(addedMissing.author[`${missing}QualityProfileId`], missing === 'ebook' ? 14 : 12);
      assert.deepEqual(addedMissing.author[`${missing}Tags`], [missing === 'ebook' ? 42 : 41]);
      receipts.push({ case: 'Both preserves configured side and permits new-side overrides', configured, sent });
    }
    assert.deepEqual(errors, []);
    for (const media of ['audiobook', 'ebook']) {
      for (const shared of [false, true]) {
        for (const move of [false, true]) {
          await page.setViewportSize({ width: shared ? 390 : 1280, height: 900 });
          author = { ...author, id: 10, authorName: 'Walter Isaacson', syncMonitoredAcrossFormats: false,
            path: '/fixture/audio/library/Walter Isaacson',
            audiobookFolder: '/fixture/audio/library/Walter Isaacson',
            ebookFolder: shared ? '/fixture/audio/library/Walter Isaacson' : '/fixture/ebook/library/Walter Isaacson',
            audiobookRootFolderPath: '/fixture/audio', ebookRootFolderPath: shared ? '/fixture/audio' : '/fixture/ebook',
            audiobookQualityProfileId: 11, ebookQualityProfileId: 13,
            audiobookMetadataProfileId: 21, ebookMetadataProfileId: 23,
            audiobookMonitorNewItems: 'none', ebookMonitorNewItems: 'none' };
          const original = author[`${media}Folder`];
          const target = original.replace('/library/', '/');
          const before = mutations().length;
          await page.evaluate((a) => window.openEditAuthorEntryFixture(a), author);
          if (media === 'ebook') {
            await page.getByRole('button', { name: /eBooks/ }).click();
          }
          const field = page.locator(`input[name="${media}Folder"]`);
          assert.equal(await field.inputValue(), original, 'Edit displays the stored nested folder');
          await field.fill(target);
          await page.getByRole('button', { name: 'Save', exact: true }).click();
          await page.getByText(strings.MoveAuthorFilesConfirm.replace('{originalPath}', original).replace('{destinationPath}', target), { exact: false }).waitFor();
          assert.equal(mutations().length, before, 'Opening Move Files must not save');
          assert.equal(await page.getByText(strings.MoveSharedAuthorFolderConfirm, { exact: true }).count(), shared ? 1 : 0);
          if (artifacts && move) {
            await page.screenshot({ path: path.join(artifacts, `edit-${media}-${shared ? 'shared-mobile' : 'separate-desktop'}.png`) });
          }
          await page.getByRole('button', { name: move ? strings.MoveAuthorYesMove : strings.MoveAuthorNoIllMoveMyself, exact: true }).click();
          await page.waitForFunction(() => !window.readAuthorSyncState().isSaving);
          const sent = mutations().slice(before);
          assert.equal(sent.length, 1);
          assert.equal(sent[0].body[`${media}Folder`], target);
          assert.equal(Object.hasOwn(sent[0].body, `${media === 'ebook' ? 'audiobook' : 'ebook'}Folder`), false, 'Untouched path is omitted');
          assert.equal(sent[0].query.moveFiles === 'true', move);
          assert.equal(Object.hasOwn(sent[0].body, `${media}Path`), false);
          receipts.push({ case: 'Stored author folder and explicit move choice', media, shared, move, sent });
        }
      }
    }
    author = { ...author, tags: [41], audiobookTags: [], ebookTags: [41] };
    const beforeUnrelatedSave = mutations().length;
    await page.evaluate((a) => window.openEditAuthorEntryFixture(a), author);
    await page.locator('select[name="audiobookQualityProfileId"] option[value="12"]').waitFor({ state: 'attached' });
    await page.locator('select[name="audiobookQualityProfileId"]').selectOption('12');
    // Another client moved the author's folders after this dialog loaded its resource.
    author = { ...author, path: '/fixture/audio/new/Walter Isaacson', audiobookFolder: '/fixture/audio/new/Walter Isaacson', ebookFolder: '/fixture/ebook/new/Walter Isaacson' };
    await page.getByRole('button', { name: strings.Save, exact: true }).click();
    await page.waitForFunction(() => !window.readAuthorSyncState().isSaving);
    const unrelated = mutations().slice(beforeUnrelatedSave);
    assert.equal(unrelated.length, 1);
    assert.equal(Object.hasOwn(unrelated[0].body, 'tags'), false, 'A combined tag echo must not undo a cleared format');
    assert.deepEqual(unrelated[0].body.audiobookTags, []);
    assert.deepEqual(unrelated[0].body.ebookTags, [41]);
    assert.equal(Object.hasOwn(unrelated[0].body, 'path'), true);
    assert.equal(Object.hasOwn(unrelated[0].body, 'audiobookFolder'), false);
    assert.equal(Object.hasOwn(unrelated[0].body, 'ebookFolder'), false);
    const refreshed = await page.evaluate(() => window.readAuthorSyncState().items[0]);
    assert.equal(refreshed.audiobookFolder, author.audiobookFolder);
    assert.equal(refreshed.ebookFolder, author.ebookFolder);
    receipts.push({ case: 'Unrelated full-resource save omits stale folders', sent: unrelated });
    for (const shared of [false, true]) {
      await page.evaluate((value) => window.openBulkMoveFixture(value), shared);
      await page.locator('[class*="AuthorEditorFooter-inputContainer"]').filter({ hasText: strings.RootFolder }).getByRole('button').first().click();
      await page.getByText('/fixture/audio-alt', { exact: false }).last().click();
      await page.getByRole('button', { name: strings.MoveAuthorYesMove, exact: true }).waitFor();
      assert.equal(await page.getByText(strings.MoveSharedAuthorFolderConfirm, { exact: true }).count(), shared ? 1 : 0);
      await page.getByRole('button', { name: strings.MoveAuthorNoIllMoveMyself, exact: true }).click();
    }
    assert.deepEqual(errors, []);
    if (artifacts) {
      fs.writeFileSync(path.join(artifacts, 'receipts.json'), JSON.stringify(receipts, null, 2));
      fs.writeFileSync(path.join(artifacts, 'requests.json'), JSON.stringify(requests, null, 2));
      fs.writeFileSync(path.join(artifacts, 'browser-errors.json'), JSON.stringify({ unexpectedErrors: errors, expectedInjectedHttpErrors: expectedObservedErrors }, null, 2));
    }
    console.log('Author dialog draft isolation and Edit Cancel/X/Escape browser checks passed; missing-format entry in both directions: real CTA, explicit selected side, Cancel, complete payload, independent configured-side data, successful close/page GET, held failure and retry.');
  } finally {
    await browser?.close();
    fs.rmSync(output, { recursive: true, force: true });
  }
})().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
