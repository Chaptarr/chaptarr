/* Verify backup status, cancellation and server-owned restore reconnect with the real Redux store. */
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const webpack = require('webpack');
const { chromium } = require('playwright');

const output = fs.mkdtempSync(path.join(os.tmpdir(), 'chaptarr-backup-ui-'));
const config = require('./webpack.config')({ production: true });

config.entry = { fixture: path.join(__dirname, 'backup-fixture.js') };
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

async function check(browser, width, lostReply) {
  const page = await browser.newPage({ viewport: { width, height: 1000 } });
  const errors = [];
  page.on('pageerror', (error) => errors.push(error.message));
  page.on('console', (message) => {
    if (message.type() === 'error' && !message.text().startsWith('Failed to load resource:')) {
      errors.push(message.text());
    }
  });
  let pending = true;
  let cancels = 0;
  let restores = 0;
  let restarts = 0;
  let settingsRestores = 0;
  let failedRestore = true;
  let dismissals = 0;
  let navigations = 0;
  const css = path.join(output, 'Content', fs.readdirSync(path.join(output, 'Content')).find((name) => name.endsWith('.css')));
  await page.route('**/*', async(route) => {
    const request = route.request();
    const pathname = new URL(request.url()).pathname;
    if (request.isNavigationRequest()) {
      navigations++;
    }
    if (pathname.includes('/localization')) {
      return route.fulfill({ json: { Strings: strings } });
    }
    if (pathname.endsWith('/system/settingsbackup/restore')) {
      settingsRestores++;
      if (settingsRestores > 2) {
        return route.fulfill({ json: { applied: ['Indexers'], warnings: ['Skipped unavailable provider'] } });
      }
      if (settingsRestores === 2) {
        return route.fulfill({ status: 500, json: {
          message: 'Settings restore stopped at Profiles: delay profiles (IOException). Changes were not rolled back.\nCompleted categories: Interface Preferences.\nPartly applied categories: Profiles: custom formats, metadata profiles, quality profiles completed; failed at delay profiles (may be partially changed); release profiles, quality sizes not attempted.\nNot attempted categories: Connections.'
        } });
      }
      return route.fulfill({ status: 400, json: [
        { propertyName: 'LocalPath', errorMessage: 'Destination folder is missing' },
        { propertyName: 'RemotePath', errorMessage: 'Remote folder is required' }
      ] });
    }
    if (pathname.endsWith('/system/backup/status')) {
      return route.fulfill({ json: { pendingRestore: pending, canCancelRestore: pending, scheduleDisabled: true, restoreFailed: failedRestore } });
    }
    if (pathname.endsWith('/system/backup/restore/failed') && request.method() === 'DELETE') {
      dismissals++;
      if (dismissals === 1) {
        return route.fulfill({ status: 500, json: { message: 'Recovery folder is locked' } });
      }
      failedRestore = false;
      return route.fulfill({ json: {} });
    }
    if (pathname.endsWith('/system/backup/restore') && request.method() === 'DELETE') {
      pending = false;
      cancels++;
      return route.fulfill({ json: {} });
    }
    if (pathname.endsWith('/system/backup/restore/7')) {
      restores++;
      if (lostReply) {
        return route.abort('failed');
      }
      return route.fulfill({ json: { restartRequired: true } });
    }
    if (pathname.endsWith('/system/backup')) {
      return route.fulfill({ json: [{ id: 7, name: 'chaptarr_backup_v1.0_2026.10.04_00.00.00.zip', path: '/backup/test.zip', type: 'manual', size: 5000000001, time: '2026-10-04T00:00:00Z', includedComponents: ['chaptarr.db', 'config.xml'], omittedComponents: ['logs.db'] }] });
    }
    if (pathname.endsWith('/system/status')) {
      return route.fulfill({ status: 503, body: '' });
    }
    if (pathname.endsWith('/system/restart') || pathname.endsWith('/command')) {
      restarts++;
      return route.fulfill({ json: {} });
    }
    if (pathname === '/fixture.js') {
      return route.fulfill({ path: path.join(output, 'fixture.js') });
    }
    if (pathname === '/style.css') {
      return route.fulfill({ path: css });
    }
    if (pathname === '/') {
      return route.fulfill({ contentType: 'text/html', body: '<!doctype html><html><head><link rel="stylesheet" href="/style.css"></head><body><div id="root"></div><div id="portal-root"></div><script>window.Chaptarr={urlBase:"",apiRoot:"/api/v1",isProduction:true,instanceName:"Chaptarr"};</script><script src="/fixture.js"></script></body></html>' });
    }
    const asset = path.resolve(output, `.${decodeURIComponent(pathname)}`);
    if (asset.startsWith(`${output}${path.sep}`) && fs.existsSync(asset) && fs.statSync(asset).isFile()) {
      return route.fulfill({ path: asset });
    }
    return route.fulfill({ status: 204, body: '' });
  });
  try {
    await page.goto('http://backup.test/');
    await page.waitForFunction(() => window.fixtureReady && window.fixtureState().system.backupStatus.isPopulated);
    await page.getByText(strings.BackupsContainUnencryptedCredentials, { exact: true }).waitFor();
    await page.getByText(strings.ScheduledBackupsDisabled, { exact: true }).waitFor();
    await page.getByText(strings.BackupUploadLimitHelp, { exact: true }).waitFor();
    await page.evaluate(() => window.showSettingsRestoreError());
    await page.getByText('Destination folder is missing', { exact: false }).waitFor();
    await page.getByText('Remote folder is required', { exact: false }).waitFor();
    await page.getByRole('button', { name: strings.Close, exact: true }).click();
    await page.evaluate(() => window.showSettingsRestoreError());
    await page.getByText('Completed categories: Interface Preferences.', { exact: false }).waitFor();
    await page.getByText('failed at delay profiles (may be partially changed)', { exact: false }).waitFor();
    await page.getByText('Not attempted categories: Connections.', { exact: false }).waitFor();
    await page.getByText('Completed categories: Interface Preferences.', { exact: false }).scrollIntoViewIfNeeded();
    await page.screenshot({ path: path.join(output, `partial-settings-${width}-${lostReply}.png`), fullPage: true });
    await page.getByRole('button', { name: strings.Close, exact: true }).click();
    await page.evaluate(() => window.showSettingsRestoreError());
    await page.getByText('Skipped unavailable provider', { exact: true }).waitFor();
    assert.equal(navigations, 1, 'Successful settings restore must keep results visible');
    await page.getByText('Skipped unavailable provider', { exact: true }).scrollIntoViewIfNeeded();
    await page.screenshot({ path: path.join(output, `settings-result-${width}-${lostReply}.png`), fullPage: true });
    assert.equal(await page.getByRole('button', { name: strings.Restore, exact: true }).isDisabled(), true);
    const reload = page.waitForRequest((request) => request.isNavigationRequest());
    await page.getByRole('button', { name: strings.Close, exact: true }).click();
    await reload;
    await page.waitForFunction(() => window.fixtureReady && window.fixtureState().system.backupStatus.isPopulated);
    assert.equal(navigations, 2, 'Close refreshes the app after settings changed');
    assert.equal(await page.getByRole('button', { name: strings.DeleteFailedRestoreFiles, exact: true }).isDisabled(), true);
    const cancel = page.getByRole('button', { name: strings.CancelRestore, exact: true });
    await cancel.focus();
    await page.keyboard.press('Enter');
    await page.waitForFunction(() => window.fixtureState().system.backupStatus.item.pendingRestore === false);
    assert.equal(cancels, 1);
    await page.getByRole('button', { name: strings.DeleteFailedRestoreFiles, exact: true }).click();
    await page.getByText(strings.DeleteFailedRestoreFilesMessage, { exact: true }).waitFor();
    await page.screenshot({ path: path.join(output, `delete-recovery-${width}-${lostReply}.png`), fullPage: true });
    await page.getByRole('button', { name: strings.Cancel, exact: true }).click();
    assert.equal(dismissals, 0, 'Cancel must not remove any recovery files');
    for (let attempt = 0; attempt < 2; attempt++) {
      await page.getByRole('button', { name: strings.DeleteFailedRestoreFiles, exact: true }).click();
      await page.getByRole('button', { name: strings.Delete, exact: true }).focus();
      await page.keyboard.press('Enter');
      if (attempt === 0) {
        await page.getByText('Recovery folder is locked', { exact: true }).waitFor();
      }
    }
    await page.waitForFunction(() => window.fixtureState().system.backupStatus.item.restoreFailed === false);
    assert.equal(dismissals, 2);
    assert.equal(await page.getByText('Recovery folder is locked', { exact: true }).count(), 0);

    const themeColors = new Set();
    for (const theme of ['light', 'dark']) {
      await page.evaluate((name) => window.setFixtureTheme(name), theme);
      themeColors.add(await page.locator('table').evaluate((element) => getComputedStyle(element).color));
      await page.screenshot({ path: path.join(output, `list-${width}-${theme}-${lostReply}.png`), fullPage: true });
    }
    assert.equal(themeColors.size, 2, 'Both themes must change the rendered table colors');
    await page.locator('table').getByRole('button', { name: strings.RestoreBackup, exact: true }).click();
    await page.getByText(strings.RestoreBackupQueueHelp, { exact: true }).waitFor();
    const reconnect = page.waitForRequest((request) => new URL(request.url()).pathname.endsWith('/system/status'), { timeout: 15000 });
    await page.getByRole('button', { name: strings.Restore, exact: true }).click();
    await page.waitForFunction(() => window.fixtureState().app.isRestarting === true);
    assert.equal(await page.getByRole('button', { name: strings.Restore, exact: true }).isDisabled(), true);
    assert.equal(restores, 1);
    assert.equal(restarts, 0, 'The browser must not own the restore restart');
    await reconnect;
    assert.deepEqual(errors, []);
    await page.screenshot({ path: path.join(output, `backup-${width}-${lostReply}.png`), fullPage: true });
  } catch (error) {
    console.error(await page.locator('body').innerText(), errors);
    throw error;
  } finally {
    await page.close();
  }
}

(async() => {
  let browser = null;
  try {
    await compileFixture();
    browser = await chromium.launch();
    for (const width of [1440, 375]) {
      for (const lostReply of [false, true]) {
        await check(browser, width, lostReply);
      }
    }
    console.log('PASS: backup status/cancel, warning and contents, desktop/mobile, light/dark, keyboard, normal/lost response reconnect. Screenshots:', output);
  } finally {
    if (browser) {
      await browser.close();
    }
  }
})().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
