/* Exercise the real notification card, form, Redux actions, and API boundary. */
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const webpack = require('webpack');
const { chromium } = require('playwright');
const createWebpackConfig = require('./webpack.config');

const root = path.resolve(__dirname, '../..');
const output = fs.mkdtempSync(path.join(os.tmpdir(), 'chaptarr-notification-upgrade-'));
const artifacts = fs.mkdtempSync(path.join(os.tmpdir(), 'chaptarr-notification-upgrade-artifacts-'));
const strings = JSON.parse(fs.readFileSync(path.join(root, 'src/NzbDrone.Core/Localization/Core/en.json'), 'utf8'));
const eventNames = [
  'Grab', 'ReleaseImport', 'Upgrade', 'Rename', 'AuthorAdded', 'BookAdded', 'AuthorDelete',
  'BookDelete', 'BookFileDelete', 'BookFileDeleteForUpgrade', 'HealthIssue', 'DownloadFailure',
  'ImportFailure', 'BookRetag', 'ApplicationUpdate'
];

function makeNotification(overrides = {}) {
  const notification = {
    id: 1,
    name: 'Discord browser fixture',
    implementation: 'Discord',
    implementationName: 'Discord',
    fields: [{ name: 'username', label: 'Username', type: 'textbox', value: '', advanced: false, errors: [], warnings: [] }],
    tags: []
  };

  eventNames.forEach((event) => {
    notification[`on${event}`] = false;
    notification[`supportsOn${event}`] = false;
  });

  notification.supportsOnReleaseImport = true;
  notification.supportsOnUpgrade = true;
  notification.supportsOnGrab = true;
  notification.supportsOnRename = true;

  return { ...notification, ...overrides };
}

function compileFixture() {
  const config = createWebpackConfig({ production: true });
  config.entry = { fixture: path.join(__dirname, 'notification-upgrade-fixture.js') };
  config.output = { ...config.output, path: output, filename: 'fixture.js', publicPath: '/' };
  config.optimization = { ...config.optimization, minimize: false, splitChunks: false, runtimeChunk: false };
  config.plugins = config.plugins.filter((plugin) => ['DefinePlugin', 'MiniCssExtractPlugin'].includes(plugin.constructor.name));

  for (const rule of config.module.rules) {
    if (Array.isArray(rule.use)) {
      rule.use = rule.use.filter((loader) => !String(typeof loader === 'string' ? loader : loader.loader).includes('css-modules-typescript-loader'));
    }
  }

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

function assertNotificationPayload(payload, onUpgrade, onReleaseImport) {
  assert.equal(payload.onUpgrade, onUpgrade);
  assert.equal(payload.onReleaseImport, onReleaseImport);
  assert.equal(payload.fields.find((field) => field.name === 'username').value, '', 'Blank Discord username must reach the notification API unchanged');
}

async function verify(browser, theme, width, cssFile) {
  const page = await browser.newPage({ viewport: { width, height: 900 } });
  const pageErrors = [];
  const externalRequests = [];
  const unhandledApiRequests = [];
  const testBodies = [];
  const saveBodies = [];
  let notifications = [makeNotification()];
  let failNextTest = true;
  let failNextSave = true;
  page.on('pageerror', (error) => pageErrors.push(error.message));

  await page.route('**/*', async(route) => {
    const request = route.request();
    const url = new URL(request.url());
    const pathname = decodeURIComponent(url.pathname);

    if (url.hostname !== 'notification-upgrade.test') {
      externalRequests.push(request.url());
      return route.abort();
    }

    if (pathname.endsWith('/localization')) {
      return route.fulfill({ json: { Strings: strings } });
    }

    if (pathname === '/api/v1/notification' && request.method() === 'GET') {
      return route.fulfill({ json: JSON.parse(JSON.stringify(notifications)) });
    }

    if (pathname === '/api/v1/notification/test' && request.method() === 'POST') {
      const body = request.postDataJSON();
      testBodies.push(body);
      if (failNextTest) {
        failNextTest = false;
        return route.fulfill({ status: 500, json: { message: 'Mock notification test failed' } });
      }
      return route.fulfill({ json: { successMessages: [] } });
    }

    if (pathname === '/api/v1/notification/1' && request.method() === 'PUT') {
      const body = request.postDataJSON();
      saveBodies.push(body);
      if (failNextSave) {
        failNextSave = false;
        return route.fulfill({ status: 500, json: { message: 'Mock notification save failed' } });
      }

      const existing = notifications.find((notification) => notification.id === body.id) || makeNotification();
      const incomingFields = body.fields || [];
      const fields = existing.fields.map((field) => {
        const incoming = incomingFields.find((candidate) => candidate.name === field.name);
        return incoming ? { ...field, value: incoming.value } : field;
      });
      const saved = { ...existing, ...body, fields };
      notifications = notifications.map((notification) => (
        notification.id === saved.id ? saved : notification
      ));
      return route.fulfill({ json: saved });
    }

    if (pathname.startsWith('/api/v1/')) {
      unhandledApiRequests.push(`${request.method()} ${pathname}`);
      return route.fulfill({ status: 404, json: {} });
    }

    if (pathname === '/') {
      return route.fulfill({
        contentType: 'text/html',
        body: `<!doctype html><meta name="viewport" content="width=device-width,initial-scale=1"><link rel="stylesheet" href="/Content/${path.basename(cssFile)}"><style>body{margin:0}</style><div id="root"></div><div id="portal-root"></div><script>window.Chaptarr={urlBase:"",apiRoot:"/api/v1",isProduction:true,instanceName:"Chaptarr"};</script><script src="/fixture.js"></script>`
      });
    }

    const asset = path.resolve(output, `.${pathname}`);
    if (asset.startsWith(`${output}${path.sep}`) && fs.existsSync(asset) && fs.statSync(asset).isFile()) {
      return route.fulfill({ path: asset });
    }

    return route.fulfill({ status: 404, body: '' });
  });

  async function mount() {
    await page.evaluate((selectedTheme) => {
      window.setNotificationFixtureTheme(selectedTheme);
      window.renderNotificationFixture();
    }, theme);
    await page.waitForFunction(() => window.getNotificationFixtureState().isPopulated);
  }

  async function openConnection(name, implementationName = 'Discord') {
    const card = page.getByText(name, { exact: true }).locator('xpath=../..');
    await card.locator('button').first().click();
    await page.getByText(`Edit Connection - ${implementationName}`, { exact: true }).waitFor();
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, `${theme} ${width}: notification form overflows horizontally`);
  }

  async function pressSpace(name, expectedValue) {
    const input = page.locator(`input[name="${name}"]`);
    assert.equal(await input.isVisible(), true, `${name} should be visible`);
    await input.focus();
    await page.keyboard.press('Space');
    assert.equal(await input.isChecked(), expectedValue, `${name} should toggle with Space`);
    return input;
  }

  async function save() {
    const request = page.waitForRequest((candidate) => candidate.method() === 'PUT' && new URL(candidate.url()).pathname === '/api/v1/notification/1');
    await page.getByRole('button', { name: strings.Save, exact: true }).click();
    return request;
  }

  try {
    await page.goto('http://notification-upgrade.test/');
    await page.waitForFunction(() => window.fixtureReady);
    await mount();

    await page.getByText('Discord browser fixture', { exact: true }).waitFor();
    assert.equal(await page.getByText(strings.Disabled, { exact: true }).count(), 1, 'No selected trigger starts disabled');
    await openConnection('Discord browser fixture');

    const upgrade = page.locator('input[name="onUpgrade"]');
    const releaseImport = page.locator('input[name="onReleaseImport"]');
    const username = page.locator('input[name="username"]');
    assert.equal(await upgrade.isVisible(), true, 'Upgrade must be available independently of Release Import');
    assert.equal(await upgrade.isChecked(), false);
    assert.equal(await releaseImport.isChecked(), false);
    assert.equal(await username.inputValue(), '', 'The Discord username field is empty in this regression case');

    await pressSpace('onUpgrade', true);
    const testRequest = page.waitForRequest((candidate) => candidate.method() === 'POST' && new URL(candidate.url()).pathname === '/api/v1/notification/test');
    await page.getByRole('button', { name: strings.Test, exact: true }).click();
    assertNotificationPayload((await testRequest).postDataJSON(), true, false);
    await page.waitForFunction(() => !window.getNotificationFixtureState().isTesting && !!window.getNotificationFixtureState().saveError);
    assert.equal(await upgrade.isChecked(), true, 'A failed test must retain the selected Upgrade trigger');

    const failedSave = await save();
    assertNotificationPayload(failedSave.postDataJSON(), true, false);
    await page.waitForFunction(() => !window.getNotificationFixtureState().isSaving && !!window.getNotificationFixtureState().saveError);
    assert.equal(await upgrade.isChecked(), true, 'A failed save must retain the selected Upgrade trigger');
    assert.equal(await page.getByText('Edit Connection - Discord', { exact: true }).isVisible(), true, 'A failed save must keep the form open');

    const successfulSave = await save();
    assertNotificationPayload(successfulSave.postDataJSON(), true, false);
    await page.getByText('Edit Connection - Discord', { exact: true }).waitFor({ state: 'hidden' });
    const upgradeBadgeCount = await page.getByText(strings.OnUpgrade, { exact: true }).count();
    if (upgradeBadgeCount !== 1) {
      const debug = await page.evaluate(() => ({
        items: window.getNotificationFixtureState().items,
        text: document.querySelector('#root').innerText,
        html: document.querySelector('#root').innerHTML
      }));
      console.error('Upgrade badge diagnostic:', JSON.stringify(debug));
      await page.screenshot({ path: path.join(artifacts, `${theme}-${width}-badge-failure.png`), fullPage: true });
    }
    assert.equal(upgradeBadgeCount, 1, 'Upgrade-only notification must show its Upgrade card badge');
    assert.equal(await page.getByText(strings.Disabled, { exact: true }).count(), 0, 'Upgrade-only notification must not also show Disabled');

    await mount();
    await openConnection('Discord browser fixture');
    assert.equal(await page.locator('input[name="onUpgrade"]').isChecked(), true, 'Saved Upgrade selection must survive a new Redux store and API fetch');
    assert.equal(await page.locator('input[name="onReleaseImport"]').isChecked(), false);
    await page.screenshot({ path: path.join(artifacts, `${theme}-${width}-upgrade-only.png`), fullPage: true });

    await pressSpace('onReleaseImport', true);
    await page.getByRole('button', { name: strings.Cancel, exact: true }).click();
    await page.getByText('Edit Connection - Discord', { exact: true }).waitFor({ state: 'hidden' });
    await openConnection('Discord browser fixture');
    assert.equal(await page.locator('input[name="onUpgrade"]').isChecked(), true, 'Cancel must preserve the saved Upgrade selection');
    assert.equal(await page.locator('input[name="onReleaseImport"]').isChecked(), false, 'Cancel must discard only the unsaved Release Import selection');

    await pressSpace('onReleaseImport', true);
    const successfulTest = page.waitForRequest((candidate) => candidate.method() === 'POST' && new URL(candidate.url()).pathname === '/api/v1/notification/test');
    await page.getByRole('button', { name: strings.Test, exact: true }).click();
    assertNotificationPayload((await successfulTest).postDataJSON(), true, true);
    await page.waitForFunction(() => !window.getNotificationFixtureState().isTesting);
    await pressSpace('onUpgrade', false);

    const releaseOnlySave = await save();
    assertNotificationPayload(releaseOnlySave.postDataJSON(), false, true);
    await page.getByText('Edit Connection - Discord', { exact: true }).waitFor({ state: 'hidden' });
    assert.equal(await page.getByText(strings.OnReleaseImport, { exact: true }).count(), 1);
    assert.equal(await page.getByText(strings.OnUpgrade, { exact: true }).count(), 0);
    assert.equal(await page.getByText(strings.Disabled, { exact: true }).count(), 0);

    await mount();
    await openConnection('Discord browser fixture');
    assert.equal(await page.locator('input[name="onUpgrade"]').isChecked(), false);
    assert.equal(await page.locator('input[name="onReleaseImport"]').isChecked(), true, 'Release Import must remain independently enabled after reload');
    await pressSpace('onReleaseImport', false);
    await page.getByRole('button', { name: strings.Cancel, exact: true }).click();
    await page.getByText('Edit Connection - Discord', { exact: true }).waitFor({ state: 'hidden' });
    await openConnection('Discord browser fixture');
    assert.equal(await page.locator('input[name="onReleaseImport"]').isChecked(), true, 'Cancel must leave the saved Release Import selection intact');
    await page.getByRole('button', { name: strings.Cancel, exact: true }).click();

    notifications = [makeNotification({
      id: 2,
      name: 'Unsupported Upgrade fixture',
      implementationName: 'Unsupported fixture',
      supportsOnUpgrade: false
    })];
    await mount();
    await openConnection('Unsupported Upgrade fixture', 'Unsupported fixture');
    assert.equal(await page.locator('input[name="onUpgrade"]').count(), 0, 'Providers that do not support Upgrade must not show its control');
    assert.deepEqual(testBodies.map((body) => [body.onUpgrade, body.onReleaseImport]), [[true, false], [true, true]]);
    assert.deepEqual(saveBodies.map((body) => [body.onUpgrade, body.onReleaseImport]), [[true, false], [true, false], [false, true]]);
    assert.deepEqual(unhandledApiRequests, []);
    assert.deepEqual(externalRequests, [], 'Every request is intercepted; no webhook or external request may escape the fixture');
    assert.deepEqual(pageErrors, []);
    await page.close();
  } catch (error) {
    await page.close();
    throw new Error(`${theme} ${width}: ${error.message}`, { cause: error });
  }
}

(async() => {
  let browser = null;
  try {
    await compileFixture();
    const cssFile = path.join(output, 'Content', fs.readdirSync(path.join(output, 'Content')).find((file) => file.endsWith('.css')));
    browser = await chromium.launch();
    for (const theme of ['light', 'dark']) {
      for (const width of [320, 1440]) {
        await verify(browser, theme, width, cssFile);
      }
    }
    console.log(`Notification Upgrade browser checks passed at 320px and 1440px in light and dark themes. Screenshots: ${artifacts}`);
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
