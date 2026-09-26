/* Render the real media-management settings and confirmation flow in Chromium. */
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const webpack = require('webpack');
const { chromium } = require('playwright');

const root = path.resolve(__dirname, '../..');
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

function routeFixture(page, strings, cssPath) {
  const estimates = [];
  page.route('**/*', async(route) => {
    const pathname = new URL(route.request().url()).pathname;
    if (pathname.endsWith('/config/mediamanagement/covercleanup')) {
      assert.equal(route.request().method(), 'GET', 'Estimating savings must be read-only');
      estimates.push(route);
      return;
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
        body: '<!doctype html><html><head><meta name="viewport" content="width=device-width, initial-scale=1"><link rel="stylesheet" href="/style.css"></head><body><div id="root"></div><div id="portal-root"></div><script>window.Chaptarr={isProduction:true,apiRoot:"/api/v1",instanceName:"Chaptarr"};</script><script src="/fixture.js"></script></body></html>'
      });
    }

    const assetPath = path.resolve(output, `.${decodeURIComponent(pathname)}`);
    if (assetPath.startsWith(`${output}${path.sep}`) && fs.existsSync(assetPath) && fs.statSync(assetPath).isFile()) {
      return route.fulfill({ path: assetPath });
    }

    return route.fulfill({ status: 204, body: '' });
  });
  return estimates;
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
  const estimates = routeFixture(page, strings, cssPath);
  await page.goto('http://media-management.test/');
  await page.waitForFunction(() => window.fixtureReady && window.renderMediaManagementFixture);
  await page.waitForTimeout(100);
  if (!(await page.getByText(strings.CoverDownloadSettings, { exact: true }).count())) {
    console.error('Fixture did not render settings content:', await page.locator('body').innerText(), errors);
  }
  return { context, page, errors, estimates };
}

(async() => {
  await compileFixture();
  const cssName = fs.readdirSync(path.join(output, 'Content')).find((name) => name.endsWith('.css'));
  const cssPath = path.join(output, 'Content', cssName);
  const strings = JSON.parse(fs.readFileSync(path.join(root, 'src/NzbDrone.Core/Localization/Core/en.json')));
  const browser = await chromium.launch();
  try {
    const desktop = await openFixture(browser, strings, cssPath, { viewport: { width: 1280, height: 900 } });
    const page = desktop.page;
    await page.getByText(strings.CoverDownloadSettings, { exact: true }).scrollIntoViewIfNeeded();
    assert.equal(await page.getByRole('button', { name: strings.CoverDownloadScopeAllBooks, exact: true }).count(), 1, 'All Books should be the default');
    assert.equal(await page.getByText(strings.CoverDownloadScopeAllBooksHelpText, { exact: true }).count(), 1, 'Help should describe the selected All books option');
    await page.screenshot({ path: path.join(os.tmpdir(), 'chaptarr-cover-scope-desktop.png'), fullPage: true });

    const mobile = await openFixture(browser, strings, cssPath, {
      viewport: { width: 390, height: 844 },
      isMobile: true,
      hasTouch: true,
      userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1'
    });
    await mobile.page.getByText(strings.CoverDownloadSettings, { exact: true }).scrollIntoViewIfNeeded();
    assert.equal(await mobile.page.evaluate(() => window.innerWidth), 390, 'The mobile fixture should render at the requested 390px viewport width');
    assert.equal(await mobile.page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true, 'The setting should fit a mobile viewport without horizontal overflow');
    assert.deepEqual(mobile.errors, [], 'The mobile page should render without browser exceptions');
    await mobile.page.screenshot({ path: path.join(os.tmpdir(), 'chaptarr-cover-scope-mobile.png'), fullPage: true });
    await mobile.page.evaluate(() => window.renderMediaManagementFixture({ selectedScope: 'libraryBooks' }));
    await mobile.page.getByRole('button', { name: 'Save Changes', exact: true }).click();
    await mobile.page.getByText(strings.CoverCleanupEstimateHelpText, { exact: true }).waitFor();
    assert.equal(mobile.estimates.length, 0, 'Opening confirmation must not start a disk scan');
    await mobile.page.screenshot({ path: path.join(os.tmpdir(), 'chaptarr-cover-scope-calculate-mobile.png') });
    const mobileEstimate = mobile.page.waitForRequest('**/config/mediamanagement/covercleanup');
    await mobile.page.getByRole('button', { name: strings.CalculateCoverSavings, exact: true }).click();
    await mobileEstimate;
    await mobile.estimates[0].fulfill({ json: { bytes: 0, bookCount: 0 } });
    await mobile.page.getByText(strings.CoverCleanupEstimate.replace('{size}', '0 B'), { exact: true }).waitFor();
    const mobileSave = await mobile.page.getByRole('button', { name: 'Save', exact: true }).boundingBox();
    assert(mobileSave && mobileSave.y + mobileSave.height <= 844 && mobileSave.x + mobileSave.width <= 390, 'The confirmation Save button must fit the mobile screen');
    await mobile.page.screenshot({ path: path.join(os.tmpdir(), 'chaptarr-cover-scope-confirmation-mobile.png') });
    await mobile.context.close();

    await page.getByRole('button', { name: strings.CoverDownloadScopeAllBooks, exact: true }).click();
    await page.getByText('Library covers only', { exact: true }).click();
    assert.equal(await page.getByText(strings.CoverDownloadScopeLibraryBooksHelpText, { exact: true }).count(), 1, 'Help should follow the selected Library covers only option');
    assert.equal(desktop.estimates.length, 0, 'Browsing options must not scan the cover cache');
    await page.getByRole('button', { name: 'Save Changes', exact: true }).click();
    await page.getByText(strings.CoverDownloadScopeConfirmTitle, { exact: true }).waitFor();
    assert.equal(await page.getByText(strings.CoverDownloadScopeConfirmMessage, { exact: true }).count(), 1, 'The confirmation must explain which cached covers are deleted and what remains unaffected');
    await page.getByText(strings.CoverCleanupEstimateHelpText, { exact: true }).waitFor();
    assert.equal(desktop.estimates.length, 0, 'Opening confirmation must not start a disk scan');
    await page.screenshot({ path: path.join(os.tmpdir(), 'chaptarr-cover-scope-calculate-desktop.png') });
    const firstEstimate = page.waitForRequest('**/config/mediamanagement/covercleanup');
    firstEstimate.catch(() => {});
    await page.getByRole('button', { name: strings.CalculateCoverSavings, exact: true }).press('Enter');
    assert.deepEqual(await page.evaluate(() => window.savedCalls), [], 'Enter on Calculate must not confirm destructive cleanup');
    await page.getByText(strings.CoverCleanupEstimateLoading, { exact: true }).waitFor();
    assert.equal(await page.getByRole('button', { name: strings.CalculateCoverSavings, exact: true }).isEnabled(), false, 'Calculate must not start duplicate concurrent scans');
    assert.equal(await page.getByRole('button', { name: 'Save', exact: true }).isEnabled(), true, 'A slow estimate must not block Save');
    await firstEstimate;
    assert.equal(desktop.estimates.length, 1, 'Calculate should request one estimate');
    await desktop.estimates[0].fulfill({ json: { bytes: 2397483648, bookCount: 100 } });
    await page.getByText(strings.CoverCleanupEstimate.replace('{size}', '2 GiB'), { exact: true }).waitFor();
    await page.screenshot({ path: path.join(os.tmpdir(), 'chaptarr-cover-scope-confirmation.png') });
    assert.deepEqual(await page.evaluate(() => window.savedCalls), [], 'Opening the confirmation must not save either settings section');

    await page.getByRole('button', { name: 'Cancel', exact: true }).press('Enter');
    assert.deepEqual(await page.evaluate(() => window.savedCalls), [], 'Cancel must leave both settings sections unsaved');
    await page.getByRole('button', { name: 'Save Changes', exact: true }).click();
    await page.getByText(strings.CoverDownloadScopeConfirmTitle, { exact: true }).waitFor();
    await page.getByText(strings.CoverCleanupEstimateHelpText, { exact: true }).waitFor();
    assert.equal(desktop.estimates.length, 1, 'Reopening must clear the old result without recalculating');
    await page.getByText(strings.CoverDownloadScopeConfirmMessage, { exact: true }).click();
    await page.keyboard.press('Enter');
    assert.equal(desktop.estimates.length, 1, 'Saving without Calculate must never scan for an estimate');
    await page.waitForFunction(() => window.savedCalls.length === 2);
    assert.deepEqual(await page.evaluate(() => window.savedCalls), ['mediaManagement', 'naming'], 'Confirm must preserve the existing media-management and naming saves');
    assert.deepEqual(await page.evaluate(() => window.invalidations), [], 'Cached book resources must remain untouched until the settings save succeeds');
    await page.evaluate(() => window.resolveMediaSaveSuccess());
    assert.deepEqual(await page.evaluate(() => window.invalidations), ['coverScope'], 'A successful scope save must invalidate cached book resources');

    await page.evaluate(() => window.renderMediaManagementFixture({ selectedScope: 'libraryBooks' }));
    await page.getByRole('button', { name: 'Save Changes', exact: true }).click();
    const failedEstimate = page.waitForRequest('**/config/mediamanagement/covercleanup');
    await page.getByRole('button', { name: strings.CalculateCoverSavings, exact: true }).click();
    await failedEstimate;
    await desktop.estimates.at(-1).fulfill({ contentType: 'application/json', body: '{' });
    await page.getByText(strings.CoverCleanupEstimateError, { exact: true }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Save', exact: true }).isEnabled(), true, 'An unavailable estimate must not block Save or be shown as zero');
    assert.deepEqual(await page.evaluate(() => window.savedCalls), [], 'Estimating space must not save settings');
    const retryEstimate = page.waitForRequest('**/config/mediamanagement/covercleanup');
    await page.getByRole('button', { name: strings.CalculateCoverSavings, exact: true }).click();
    await retryEstimate;
    const cancelledEstimate = page.waitForEvent('requestfailed', (request) => request.url().endsWith('/config/mediamanagement/covercleanup'));
    await page.getByRole('button', { name: 'Save', exact: true }).press('Enter');
    await cancelledEstimate;
    await page.waitForFunction(() => window.savedCalls.length === 2);

    await page.evaluate(() => window.renderMediaManagementFixture({ savedScope: 'libraryBooks', selectedScope: 'libraryBooks', otherSettingPending: true }));
    await page.getByRole('button', { name: 'Save Changes', exact: true }).click();
    await page.waitForFunction(() => window.savedCalls.length === 2);
    assert.equal(await page.getByText(strings.CoverDownloadScopeConfirmTitle, { exact: true }).count(), 0, 'Unrelated saves under Library Books must not ask for this confirmation');
    assert.deepEqual(await page.evaluate(() => window.savedCalls), ['mediaManagement', 'naming']);
    assert.deepEqual(await page.evaluate(() => window.invalidations), [], 'An unrelated settings save must not invalidate book resources');

    await page.evaluate(() => window.renderMediaManagementFixture({ savedScope: 'libraryBooks', selectedScope: 'allBooks' }));
    await page.getByRole('button', { name: 'Save Changes', exact: true }).click();
    await page.waitForFunction(() => window.savedCalls.length === 2);
    assert.equal(await page.getByText(strings.CoverDownloadScopeConfirmTitle, { exact: true }).count(), 0, 'Expanding from Library Books should not require the narrowing confirmation');
    await page.evaluate(() => window.resolveMediaSaveSuccess());
    assert.deepEqual(await page.evaluate(() => window.invalidations), ['coverScope'], 'Either persisted scope transition must refresh cached book resources');
    assert.deepEqual(desktop.errors, [], 'The desktop page should render without browser exceptions');
    console.log('Media-management cover-scope browser check passed: default, All Books to Library Books save confirmation/cancel, explicit Calculate only, optional read-only estimates and errors, paired saves, unrelated Library Books save, and mobile layout.');
  } finally {
    await browser.close();
    fs.rmSync(output, { recursive: true, force: true });
  }
})().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
