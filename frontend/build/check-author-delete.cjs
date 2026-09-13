/* Uses the repo-pinned browser runner; requests never reach a real library. */
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const webpack = require('webpack');

const { chromium } = require('playwright');

const root = path.resolve(__dirname, '../..');
const output = fs.mkdtempSync(path.join(os.tmpdir(), 'chaptarr-delete-ui-'));
const darkTheme = require('../src/Styles/Themes/dark');
const lightTheme = require('../src/Styles/Themes/light');

const themeVariables = { dark: darkTheme, light: lightTheme };
const config = require('./webpack.config')({ production: true });

config.entry = { fixture: path.join(__dirname, 'author-delete-fixture.js') };
config.output = { ...config.output, path: output, filename: 'fixture.js' };
config.optimization = { minimize: false };
config.plugins = config.plugins.filter((p) => ['DefinePlugin', 'MiniCssExtractPlugin'].includes(p.constructor.name));
for (const rule of config.module.rules) {
  if (Array.isArray(rule.use)) {
    rule.use = rule.use.filter((loader) => !String(typeof loader === 'string' ? loader : loader.loader).includes('css-modules-typescript-loader'));
  }
}
const baseline = process.argv.includes('--baseline');
(async() => {
  const compiler = webpack(config);
  await new Promise((resolve, reject) => compiler.run((error, stats) => {
    compiler.close(() => {});
    if (error || stats.hasErrors()) {
      reject(error || new Error(stats.toString({ all: false, errors: true })));
    } else {
      resolve();
    }
  }));
  const browser = await chromium.launch();
  try {
    const css = fs.readdirSync(path.join(output, 'Content')).find((f) => f.endsWith('.css'));
    const strings = JSON.parse(fs.readFileSync(path.join(root, 'src/NzbDrone.Core/Localization/Core/en.json')));
    let cases = 0;
    for (const theme of ['light', 'dark']) {
      for (const width of baseline ? [390, 1440] : [320, 412, 1440]) {
        const page = await browser.newPage({ viewport: { width, height: 850 } });
        const errors = [];
        page.on('pageerror', (e) => errors.push(e.message));
        await page.route('**/*', (route) => {
          const request = route.request();
          const pathname = new URL(request.url()).pathname;
          if (pathname.endsWith('/localization')) {
            return route.fulfill({ json: { Strings: strings } });
          }
          if (pathname.endsWith('/author/editor') && request.method() === 'DELETE') {
            return route.fulfill({ json: {} });
          }
          if (pathname === '/fixture.js') {
            return route.fulfill({ path: path.join(output, 'fixture.js'), contentType: 'text/javascript' });
          }
          if (pathname === '/style.css') {
            return route.fulfill({ path: path.join(output, 'Content', css), contentType: 'text/css' });
          }
          if (pathname !== '/') {
            return route.fulfill({ status: 404, body: '' });
          }
          const variables = Object.entries(themeVariables[theme]).map(([k, v]) => `--${k}:${v}`).join(';');
          return route.fulfill({ contentType: 'text/html', body: `<!doctype html><meta name="viewport" content="width=device-width,initial-scale=1"><link rel="stylesheet" href="/style.css"><style>:root{${variables}}body{margin:0}</style><div id="root"></div><div id="portal-root"></div><script>window.Chaptarr={isProduction:true,apiRoot:'/api/v1'};</script><script src="/fixture.js"></script>` });
        });
        await page.goto('http://delete-fixture.test/');
        await page.evaluate(() => window.renderDeleteFixture());
        await page.getByText('Delete Selected Author', { exact: true }).waitFor();
        if (!baseline) {
          assert.equal(await page.getByText(strings.AddListExclusionAuthorHelpText, { exact: true }).isVisible(), true);
          const exclusion = page.locator('input[name="addImportListExclusion"]');
          assert.equal(await exclusion.isChecked(), false);
          assert.equal(await page.locator('input[name="deleteFiles"]').isChecked(), false);
          await exclusion.focus();
          await page.keyboard.press('Space');
          assert.equal(await exclusion.isChecked(), true);
          const excludedRequest = page.waitForRequest((request) => request.url().endsWith('/author/editor') && request.method() === 'DELETE');
          await page.getByRole('button', { name: 'Delete', exact: true }).click();
          assert.deepEqual((await excludedRequest).postDataJSON(), { authorIds: [1, 2], deleteFiles: false, addImportListExclusion: true });
          await page.evaluate(() => window.renderDeleteFixture());
          assert.equal(await exclusion.isChecked(), false);
          const deleteFiles = page.locator('input[name="deleteFiles"]');
          await deleteFiles.focus();
          await page.keyboard.press('Space');
          assert.equal(await deleteFiles.isChecked(), true);
          await deleteFiles.locator('..').click();
          assert.equal(await deleteFiles.isChecked(), false);
          const uncheckedRequest = page.waitForRequest((request) => request.url().endsWith('/author/editor') && request.method() === 'DELETE');
          await page.getByRole('button', { name: 'Delete', exact: true }).click();
          assert.deepEqual((await uncheckedRequest).postDataJSON(), { authorIds: [1, 2], deleteFiles: false, addImportListExclusion: false });
          await page.evaluate(() => window.renderDeleteFixture());
          await deleteFiles.focus();
          await page.keyboard.press('Space');
          const checkedRequest = page.waitForRequest((request) => request.url().endsWith('/author/editor') && request.method() === 'DELETE');
          await page.getByRole('button', { name: 'Delete', exact: true }).click();
          assert.deepEqual((await checkedRequest).postDataJSON(), { authorIds: [1, 2], deleteFiles: true, addImportListExclusion: false });
          await page.evaluate(() => window.renderDeleteFixture());
        }
        assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `${theme} ${width}: horizontal overflow`);
        await page.screenshot({ path: path.join(output, `${theme}-${width}.png`) });
        if (!baseline) {
          await page.evaluate(() => window.renderDeleteFixture(true));
          assert.equal(await page.getByText(strings.AddListExclusionAuthorHelpText, { exact: true }).isVisible(), true, 'Single and bulk deletion must describe author exclusion consistently');
          assert.equal(await page.getByText(strings.DeleteAuthorHeader.replace('{authorName}', 'Example Author'), { exact: true }).isVisible(), true);
          assert.equal(await page.getByText('/books/Example Author', { exact: true }).isVisible(), true);
          assert.equal(await page.locator('input[name="addImportListExclusion"]').isChecked(), false);
          assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `${theme} ${width}: single-modal horizontal overflow`);
          await page.screenshot({ path: path.join(output, `${theme}-${width}-single.png`) });
        }
        assert.deepEqual(errors, []);
        await page.close();
        cases++;
      }
    }
    console.log(JSON.stringify({ cases, baseline, output }));
  } finally {
    await browser.close();
  }
})().catch((e) => {
  console.error(e);
  process.exitCode = 1;
});
