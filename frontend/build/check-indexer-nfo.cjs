/* Exercise shared dialogs and settings navigation in Chromium. */
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const webpack = require('webpack');
const { chromium } = require('playwright');
const translations = require('../../src/NzbDrone.Core/Localization/Core/en.json');

const output = fs.mkdtempSync(path.join(os.tmpdir(), 'chaptarr-indexer-nfo-'));
const config = require('./webpack.config')({ production: true });

config.entry = { fixture: path.join(__dirname, 'indexer-nfo-fixture.js') };
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
    compiler.close((closeError) => {
      if (error || closeError || stats.hasErrors()) {
        reject(error || closeError || new Error(stats.toString({ all: false, errors: true })));
      } else {
        resolve();
      }
    });
  }));
}

const field = (name, value, type = 'textbox') => ({
  name, value, type,
  label: { baseUrl: 'URL', apiKey: 'API Key', enableNarratorMetadata: 'Fetch Narrator Metadata', narratorMetadataBaseUrl: 'Narrator Metadata URL' }[name],
  helpText: name === 'enableNarratorMetadata' ? translations.IndexerNarratorMetadataHelpText : undefined,
  order: 0, advanced: false, privacy: 'normal'
});
const indexer = {
  id: 1, name: 'Existing indexer', implementation: 'Newznab', implementationName: 'Newznab',
  configContract: 'NewznabSettings', enableRss: true, enableAutomaticSearch: true, enableInteractiveSearch: true,
  supportsRss: true, supportsSearch: true, protocol: 'usenet', priority: 25, downloadClientId: 0, proxyId: null, tags: [],
  fields: [field('baseUrl', 'https://indexer.example'), field('apiKey', '********'),
    field('enableNarratorMetadata', true, 'checkbox'), field('narratorMetadataBaseUrl', ''),
    { ...field('categories', [7000, 7020], 'select'), label: 'Categories', selectOptionsProviderAction: 'newznabCategories' }]
};
let browser = null;
(async() => {
  await compileFixture();
  browser = await chromium.launch({ headless: true, args: ['--no-sandbox'] });
  const cssName = fs.readdirSync(path.join(output, 'Content')).find((name) => name.endsWith('.css'));
  for (const theme of ['dark', 'light']) {
    for (const width of [390, 1280]) {
      const page = await browser.newPage({ viewport: { width, height: 1000 } });
      const errors = [];
      page.on('pageerror', (error) => errors.push(error.message));
      const posts = [];
      let supported = false;
      let testGate = null;
      let categoryCalls = 0;
      let categoryName = 'Old ebooks';
      await page.route('**/*', async(route) => {
        const request = route.request();
        const pathname = new URL(request.url()).pathname;
        if (pathname === '/api/v1/localization') {
          return route.fulfill({ json: { Strings: translations } });
        }
        if (pathname.endsWith('/indexer/test')) {
          posts.push(request.postDataJSON());
          if (testGate) {
            await testGate;
          }
          return route.fulfill({ json: { successMessages: [], resource: { supportsNarratorMetadata: supported } } });
        }
        if (pathname.endsWith('/indexer/action/newznabCategories')) {
          categoryCalls++;
          assert.deepEqual(request.postDataJSON().fields.find((entry) => entry.name === 'categories').value, [7000, 7020]);
          return route.fulfill({ json: { options: [
            { value: 7000, name: 'Books' }, { value: 7020, name: categoryName, parentValue: 7000 }
          ] } });
        }
        if (pathname === '/fixture.js') {
          return route.fulfill({ path: path.join(output, 'fixture.js'), contentType: 'text/javascript' });
        }
        if (pathname === '/style.css') {
          return route.fulfill({ path: path.join(output, 'Content', cssName), contentType: 'text/css' });
        }
        if (pathname === '/') {
          return route.fulfill({ contentType: 'text/html', body: '<html><head><link rel="stylesheet" href="/style.css"></head><body><div id="root"></div><div id="portal-root"></div><script>window.Chaptarr={urlBase:"",apiRoot:"/api/v1",isProduction:true,instanceName:"Chaptarr"};</script><script src="/fixture.js"></script></body></html>' });
        }
        const asset = path.resolve(output, `.${decodeURIComponent(pathname)}`);
        if (asset.startsWith(`${output}${path.sep}`) && fs.existsSync(asset) && fs.statSync(asset).isFile()) {
          return route.fulfill({ path: asset });
        }
        if (request.method() !== 'GET') {
          posts.push({ unexpected: pathname });
        }
        return route.fulfill({ json: [] });
      });
      await page.goto('http://indexer-nfo.test/');
      await page.waitForFunction(() => window.fixtureReady === true);
      await page.evaluate(({ indexer: item, theme: t }) => window.renderIndexer(item, t), { indexer, theme });
      const checkbox = page.locator('input[name="enableNarratorMetadata"]');
      await checkbox.waitFor();
      assert.equal(await checkbox.isChecked(), true);
      assert.equal(await checkbox.isEnabled(), true);
      assert.equal(posts.length, 0);
      await page.waitForFunction(() => window.categoryState()?.isPopulated === true);
      const callsBeforeTest = categoryCalls;
      categoryName = 'Current ebooks';
      await page.getByRole('button', { name: 'Test', exact: true }).click();
      await page.waitForFunction(() => window.indexerState().isTesting === false && window.indexerState().pendingChanges.supportsNarratorMetadata === false);
      assert.equal(await checkbox.isDisabled(), true);
      assert.equal(await checkbox.isChecked(), true);
      assert.equal(posts.length, 1);
      await page.waitForFunction(() => window.categoryState().items.some((item) => item.name === 'Current ebooks'));
      assert.equal(categoryCalls, callsBeforeTest + 1, 'Successful Test reloads category options once');
      assert.equal(await page.evaluate(() => window.indexerState().pendingChanges.fields?.categories), undefined,
        'Refreshing options must not edit the selected categories');
      if (process.env.CHAPTARR_NFO_SCREENSHOTS) {
        await page.screenshot({ path: path.join(process.env.CHAPTARR_NFO_SCREENSHOTS, `indexer-${theme}-${width}.png`) });
      }
      await page.getByRole('button', { name: 'Cancel', exact: true }).click();
      await page.evaluate(() => window.reopenIndexer());
      await checkbox.waitFor();
      assert.equal(await checkbox.isDisabled(), true);
      assert.equal(await checkbox.isChecked(), true);
      assert.equal(posts.length, 1);
      await page.locator('input[name="baseUrl"]').fill('https://changed.example');
      assert.equal(await checkbox.isEnabled(), true);
      assert.equal(await checkbox.isChecked(), true);
      supported = true;
      await page.getByRole('button', { name: 'Test', exact: true }).click();
      await page.waitForFunction(() => window.indexerState().isTesting === false && window.indexerState().pendingChanges.supportsNarratorMetadata === true);
      assert.equal(await checkbox.isEnabled(), true);
      await page.getByRole('button', { name: 'Cancel', exact: true }).click();
      await page.evaluate(() => window.reopenIndexer());
      assert.equal(await checkbox.isDisabled(), true);
      assert.equal(await page.locator('input[name="baseUrl"]').inputValue(), 'https://indexer.example');
      let finishTest = null;
      testGate = new Promise((resolve) => {
        finishTest = resolve;
      });
      supported = false;
      const callsBeforeStaleTest = categoryCalls;
      await page.getByRole('button', { name: 'Test', exact: true }).click();
      await page.waitForFunction(() => window.indexerState().isTesting === true);
      await page.locator('input[name="baseUrl"]').fill('https://another.example');
      finishTest();
      await page.waitForFunction(() => window.indexerState().isTesting === false);
      assert.equal(await checkbox.isEnabled(), true, 'A stale result must not disable a different endpoint');
      assert.equal(await checkbox.isChecked(), true);
      assert.equal(categoryCalls, callsBeforeStaleTest + 1, 'Only changing the endpoint reloads categories; stale Test does not');
      testGate = null;
      supported = null;
      await page.getByRole('button', { name: 'Test', exact: true }).focus();
      await page.keyboard.press('Enter');
      await page.waitForFunction(() => window.indexerState().isTesting === false);
      assert.equal(await checkbox.isEnabled(), true, 'Unknown support must not restore an old endpoint rejection');
      await page.getByRole('button', { name: 'Cancel', exact: true }).click();
      const disabledChoice = JSON.parse(JSON.stringify(indexer));
      disabledChoice.fields.find((entry) => entry.name === 'enableNarratorMetadata').value = false;
      await page.evaluate(({ item, t }) => window.renderIndexer(item, t), { item: disabledChoice, t: theme });
      await checkbox.waitFor();
      assert.equal(await checkbox.isChecked(), false);
      assert.equal(await checkbox.isEnabled(), true);
      assert.deepEqual(errors, []);
      console.log(`PASS existing indexer, Test, Cancel/reopen, connection changes and stale replies: ${theme} ${width}px`);
      await page.close();
    }
  }
})().catch((error) => {
  console.error(error);
  process.exitCode = 1;
}).finally(async() => {
  if (browser) {
    await browser.close();
  }
  fs.rmSync(output, { recursive: true, force: true });
});
