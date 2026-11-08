const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const webpack = require('webpack');
const { chromium } = require('playwright');
const createWebpackConfig = require('./webpack.config');

const root = path.resolve(__dirname, '../..');
const output = fs.mkdtempSync(path.join(os.tmpdir(), 'chaptarr-book-file-delete-'));
const strings = JSON.parse(fs.readFileSync(path.join(root, 'src/NzbDrone.Core/Localization/Core/en.json')));
const config = createWebpackConfig({ production: true });
config.entry = { fixture: path.join(__dirname, 'book-file-delete-fixture.js') };
config.output = { ...config.output, path: output, filename: 'fixture.js', publicPath: '/' };
config.optimization = { ...config.optimization, minimize: false, splitChunks: false, runtimeChunk: false };
config.plugins = config.plugins.filter((plugin) => ['DefinePlugin', 'MiniCssExtractPlugin'].includes(plugin.constructor.name));
for (const rule of config.module.rules) {
  if (Array.isArray(rule.use)) {
    rule.use = rule.use.filter((loader) => !String(typeof loader === 'string' ? loader : loader.loader).includes('css-modules-typescript-loader'));
  }
}

const artifacts = process.env.CHAPTARR_LAYOUT_ARTIFACTS;

async function verify(browser, theme, width) {
  const page = await browser.newPage({ viewport: { width, height: 800 } });
  const errors = [];
  const requests = [];
  let mode = 'hold';
  const held = [];
  const result = (ids, calibre = false) => ({ hasCalibreFiles: calibre, files: ids.map((id) => ({ id,
    path: `/library/Dune ${id}.epub`, isPermanentDelete: !calibre || mode === 'calibrePermanent',
    associatedPaths: [`/audio/Dune ${id}.epub`, '/library/cover.jpg']
  })) });
  page.on('pageerror', (e) => errors.push(e.message));
  await page.route('**/*', async(route) => {
    const req = route.request();
    const url = new URL(req.url());
    assert.equal(url.hostname, 'deletion.test');
    if (url.pathname.endsWith('/localization')) {
      return route.fulfill({ json: { Strings: strings } });
    }
    if (url.pathname.endsWith('/bookfile/deletepreview')) {
      assert.equal(req.method(), 'POST');
      const ids = req.postDataJSON().bookFileIds ?? req.postDataJSON().bookIds;
      requests.push(ids);
      if (mode === 'hold') {
        held.push({ route, ids });
        return;
      }
      return mode === 'fail' ? route.fulfill({ status: 500, json: { message: 'test failure' } }) : route.fulfill({ json: result(ids, mode.startsWith('calibre')) });
    }
    if (url.pathname === '/fixture.js') {
      return route.fulfill({ contentType: 'application/javascript', body: fs.readFileSync(path.join(output, 'fixture.js')) });
    }
    if (url.pathname.endsWith('.css')) {
      const css = fs.readdirSync(path.join(output, 'Content')).find((f) => f.endsWith('.css'));
      return route.fulfill({ contentType: 'text/css', body: fs.readFileSync(path.join(output, 'Content', css)) });
    }
    if (url.pathname === '/') {
      return route.fulfill({ contentType: 'text/html', body: '<html><head><link rel="stylesheet" href="/fixture.css"></head><body><div id="root"></div><div id="portal-root"></div><script>window.Chaptarr={apiRoot:\'/api/v1\',apiKey:\'test\'};</script><script src="/fixture.js"></script></body></html>' });
    }
    return route.fulfill({ status: 404, body: '' });
  });
  try {
    await page.goto('http://deletion.test/');
    await page.waitForFunction(() => window.fixtureReady);
    await page.evaluate(([t]) => window.renderDeletion([1, 1], t), [theme]);
    await page.waitForFunction(() => document.querySelector('[class*="LoadingIndicator"]'));
    const button = page.getByRole('button', { name: strings.Delete, exact: true });
    assert.equal(await button.isDisabled(), true);
    await page.keyboard.press('Enter');
    assert.equal(await page.evaluate(() => window.calls.confirm), 0);
    assert.deepEqual(requests, [[1]]);
    const first = held.shift();
    await first.route.fulfill({ json: result(first.ids) });
    await page.getByText('/audio/Dune 1.epub', { exact: true }).waitFor();
    assert.equal(await page.getByText('/library/cover.jpg', { exact: true }).count(), 1);
    await page.getByText(strings.ConfirmImportPermanentDeletion, { exact: true }).waitFor();
    if (artifacts) {
      fs.mkdirSync(artifacts, { recursive: true });
      await page.screenshot({ path: path.join(artifacts, `delete-${theme}-${width}.png`) });
    }
    await page.getByRole('button', { name: strings.Cancel, exact: true }).click();
    assert.equal(await page.evaluate(() => window.calls.confirm), 0);
    mode = 'fail';
    await page.evaluate((t) => window.renderDeletion([2], t), theme);
    await page.getByText(strings.UnableToLoadDeletionFiles, { exact: true }).waitFor();
    assert.equal(await button.isDisabled(), true);
    await page.keyboard.press('Enter');
    assert.equal(await page.evaluate(() => window.calls.confirm), 0);
    await page.getByRole('button', { name: strings.Cancel, exact: true }).click();
    mode = 'calibre';
    await page.evaluate((t) => window.renderDeletion([1, 2], t), theme);
    await page.getByText(strings.DeleteBookFilesCalibreRecyclingWarning, { exact: true }).waitFor();
    assert.equal(await page.getByText('/library/cover.jpg', { exact: true }).count(), 1);
    await button.click();
    assert.equal(await page.evaluate(() => window.calls.confirm), 1);
    await page.getByRole('button', { name: strings.Cancel, exact: true }).click();
    mode = 'calibrePermanent';
    await page.evaluate((t) => window.renderDeletion([1, 2], t), theme);
    await page.getByText(strings.DeleteBookFilesCalibrePermanentWarning, { exact: true }).waitFor();
    await page.getByRole('button', { name: strings.Cancel, exact: true }).click();
    mode = 'hold';
    await page.evaluate((t) => window.renderDeletion([3], t), theme);
    await page.waitForFunction(() => document.querySelector('[class*="LoadingIndicator"]'));
    await page.getByRole('button', { name: strings.Cancel, exact: true }).click();
    const late = held.shift();
    await late.route.fulfill({ json: result(late.ids) }).catch(() => {});
    assert.equal(await page.getByText('/library/Dune 3.epub', { exact: true }).count(), 0);
    assert.equal(await page.evaluate(() => window.calls.confirm), 1);
    for (const bulk of [false, true]) {
      mode = 'hold';
      await page.evaluate((b) => window.renderBookDeletion(b), bulk);
      await page.locator('input[name="deleteFiles"]').locator('..').click();
      await page.waitForFunction(() => document.querySelector('[class*="LoadingIndicator"]'));
      assert.equal(await button.isDisabled(), true);
      const pending = held.shift();
      assert.deepEqual(pending.ids, bulk ? [7, 8] : [7]);
      await pending.route.fulfill({ json: result(pending.ids) });
      await page.getByText('/library/cover.jpg', { exact: true }).waitFor();
      assert.equal(await button.isDisabled(), false);
      if (!bulk) {
        await page.locator('input[name="applyToBothFormats"]').locator('..').click();
        assert.equal(await button.isDisabled(), true);
        await page.waitForFunction(() => document.querySelector('[class*="LoadingIndicator"]'));
        const expanded = held.shift();
        assert.deepEqual(expanded.ids, [7, 8]);
        await expanded.route.fulfill({ json: result(expanded.ids) });
        await page.getByText('/library/Dune 8.epub', { exact: true }).waitFor();
      }
      await button.click();
      await page.getByRole('button', { name: bulk ? strings.Cancel : strings.Close, exact: true }).click();
    }
    assert.equal(await page.evaluate(() => window.calls.confirm), 3);
    for (const bulk of [false, true]) {
      mode = 'hold';
      await page.evaluate((value) => window.renderUnmappedDeletion(value), bulk);
      if (bulk) {
        await page.getByRole('button', { name: strings.DeleteSelected, exact: true }).click();
      } else {
        await page.locator('#unmapped-row button').last().click();
      }
      await page.waitForFunction(() => document.querySelector('[class*="LoadingIndicator"]'));
      assert.equal(await button.isDisabled(), true);
      assert.deepEqual(await page.evaluate(() => window.unmappedDeleted), []);
      const pending = held.shift();
      assert.deepEqual(pending.ids, [51]);
      await pending.route.fulfill({ json: result(pending.ids) });
      await page.getByText('/audio/Dune 51.epub', { exact: true }).waitFor();
      await button.click();
      assert.deepEqual(await page.evaluate(() => window.unmappedDeleted), [51]);
    }
    const skippedMessage = 'Skipped included file \'extra.epub\': custom format score is below the profile minimum';
    const importedData = { droppedPath: '/downloads/requested.m4b', importedPath: '/library/requested.m4b', message: skippedMessage };
    await page.evaluate((data) => window.renderImportedHistory(data), importedData);
    await page.getByText(skippedMessage, { exact: true }).waitFor();
    assert.equal(await page.getByText(strings.Message, { exact: true }).count(), 1, 'Successful import details display nonblocking included-file skips');
    assert.equal(await page.getByText(importedData.importedPath, { exact: true }).count(), 1);
    await page.evaluate((data) => window.renderImportedHistory(data), { ...importedData, message: undefined });
    assert.equal(await page.getByText(strings.Message, { exact: true }).count(), 0, 'An import with no message gains no empty detail row');
    assert.deepEqual(errors, []);
    console.log(`${theme} ${width}: single/bulk inventory, dedupe, permanent/Calibre, pending/error disable, Cancel/late reply passed`);
  } finally {
    await page.close();
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
    browser = await chromium.launch({ headless: true, args: ['--no-sandbox'] });
    for (const [theme, width] of [['dark', 1280], ['light', 390], ['dark', 390], ['light', 1280]]) {
      await verify(browser, theme, width);
    }
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
