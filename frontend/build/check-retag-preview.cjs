const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const webpack = require('webpack');
const { chromium } = require('playwright');
const translations = require('../../src/NzbDrone.Core/Localization/Core/en.json');

const schema = require('../../src/Chaptarr.Api.V1/openapi.json');

const component = fs.readFileSync(path.join(__dirname, '../src/Retag/RetagPreviewModalContent.js'), 'utf8');
const mapping = component.match(/const skipMessages = \{([\s\S]*?)\};/)[1];
for (const reason of schema.components.schemas.RetagSkipReason.enum) {
  const entry = mapping.match(new RegExp(`\\b${reason}: '([^']+)'`));
  assert.ok(entry, `Missing preview mapping for ${reason}`);
  assert.ok(Object.hasOwn(translations, entry[1]), `Missing translation ${entry[1]}`);
}

const connector = fs.readFileSync(path.join(__dirname, '../src/Retag/RetagPreviewModalContentConnector.js'), 'utf8');
const retagPayload = connector.match(/this\.props\.executeCommand\(\{([\s\S]*?)\}\)/)?.[1];
assert.ok(retagPayload, 'RetagFiles command payload is missing');
assert.match(retagPayload, /name:\s*commandNames\.RETAG_FILES\b/);
assert.match(retagPayload, /^\s*authorId:\s*this\.props\.authorId,?\s*$/m, 'RetagFiles must send the selected authorId');

const light = require('../src/Styles/Themes/light');
const dark = require('../src/Styles/Themes/dark');

const themes = { light, dark };
const output = fs.mkdtempSync(path.join(os.tmpdir(), 'retag-preview-'));
const config = require('./webpack.config')({ production: true });

config.entry = { fixture: path.join(__dirname, 'retag-preview-fixture.js') };
config.output = { ...config.output, path: output, filename: 'fixture.js', publicPath: '/' };
config.cache = false;
config.optimization = { ...config.optimization, minimize: false, splitChunks: false, runtimeChunk: false };
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
    await new Promise((resolve, reject) => compiler.run((error, stats) => compiler.close((closeError) => {
      if (error || closeError || stats.hasErrors()) {
        reject(error || closeError || new Error(stats.toString({ all: false, errors: true })));
      } else {
        resolve();
      }
    })));
    browser = await chromium.launch({ headless: true, args: ['--no-sandbox'] });
    for (const width of [390, 1280]) {
      for (const [theme, colors] of Object.entries(themes)) {
        for (const scenario of ['unregistered', 'mixed']) {
          const page = await browser.newPage({ viewport: { width, height: 900 }, timezoneId: 'UTC' });
          page.setDefaultTimeout(10000);
          const errors = [];
          page.on('console', (message) => {
            if (message.type() === 'error') {
              errors.push(message.text());
            }
          });
          page.on('pageerror', (error) => errors.push(error.message));
          await page.route('**/*', (route) => {
            const url = new URL(route.request().url());
            if (url.pathname === '/api/v1/localization') {
              return route.fulfill({ json: { Strings: translations } });
            }
            if (url.pathname === '/') {
              const css = fs.readdirSync(path.join(output, 'Content')).find((name) => name.endsWith('.css'));
              return route.fulfill({ contentType: 'text/html', body: `<!doctype html><html><head><link rel="stylesheet" href="/Content/${css}"></head><body><div id="root"></div><div id="portal-root"></div><script>window.Chaptarr={urlBase:"",apiRoot:"/api/v1",isProduction:true};</script><script src="/fixture.js"></script></body></html>` });
            }
            const asset = path.resolve(output, `.${url.pathname}`);
            if (asset.startsWith(`${output}${path.sep}`) && fs.existsSync(asset) && fs.statSync(asset).isFile()) {
              return route.fulfill({ path: asset });
            }
            return route.fulfill({ status: 204, body: '' });
          });
          await page.goto(`http://retag-preview.test/?scenario=${scenario}`);
          await page.waitForFunction(() => window.fixtureReady);
          await page.evaluate((values) => Object.entries(values).forEach(([key, value]) => document.documentElement.style.setProperty(`--${key}`, value)), colors);
          assert.equal(await page.getByText(/Ebooks not registered in Calibre: 1/).count(), 1);
          assert.equal(await page.getByText(translations.SuccessMyWorkIsDoneNoFilesToRetag).count(), 0);
          const button = page.getByRole('button', { name: translations.Retag, exact: true });
          if (scenario === 'unregistered') {
            assert.equal(await button.isDisabled(), true);
          } else {
            await page.waitForFunction(() => window.retag.getSelectedIds().length === 2);
            assert.deepEqual(await page.evaluate(() => window.retag.getSelectedIds()), [1, 3]);
            assert.equal(await page.getByText(/Files not previewed while Calibre library http/).count(), 1);
            assert.equal(await page.getByText(/retrying after 2000-11-11 12:01/).count(), 1);
            assert.equal(await page.getByText(/Files unable to preview: 2/).count(), 1);
            assert.equal(await page.getByText(/Files no longer available in Calibre library missing: 1/).count(), 1);
            assert.equal(await page.getByText(/Files unable to preview in Calibre library broken: 1/).count(), 1);
            assert.equal(await page.getByText(/Files not in any root folder: 1/).count(), 1);
            assert.equal(await page.getByText(/Calibre settings for \/invalid-root are incomplete/).count(), 1);
            assert.equal(await page.getByText(/Ebooks in \/unlinked-root, a root folder not linked to a Calibre library: 1/).count(), 1);
            assert.equal(await page.getByText(/^Files unavailable for preview: 1/).count(), 1);
            assert.equal(await page.getByText(/zero-window.*Retry now/).count(), 1);
            assert.equal(await page.getByText(/They were not retagged|Files not attempted/).count(), 0);
            assert.equal(await page.getByText(/Listed files whose tag writes will be skipped if selected: 1/).count(), 1);
            await page.evaluate(() => window.retag.onSelectAllChange({ value: false }));
            await page.waitForFunction(() => window.retag.getSelectedIds().length === 0);
            await page.evaluate(() => {
              window.retag.onSelectedChange({ id: 1, value: true });
            });
            await page.evaluate(() => {
              window.retag.onSelectedChange({ id: 3, value: true, shiftKey: true });
            });
            assert.deepEqual(await page.evaluate(() => window.retag.getSelectedIds()), [1, 3]);
            await button.focus();
            await page.keyboard.press('Enter');
            assert.deepEqual(await page.evaluate(() => window.calls), [[[1, 3], false, false]]);
          }
          assert.deepEqual(errors, []);
          console.log(`PASS ${scenario} ${width}px ${theme}`);
          await page.close();
        }
      }
    }
  } finally {
    if (browser) {
      await browser.close();
    }
    fs.rmSync(output, { recursive: true, force: true });
    console.log(`Removed owned fixture output: ${output}`);
  }
})().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
