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
        let refuseDelete = false;
        page.on('pageerror', (e) => errors.push(e.message));
        await page.route('**/*', (route) => {
          const request = route.request();
          const pathname = new URL(request.url()).pathname;
          if (pathname.endsWith('/localization')) {
            return route.fulfill({ json: { Strings: strings } });
          }
          if (request.method() === 'DELETE' && (/\/author\/(editor|1)$/).test(pathname)) {
            return refuseDelete ?
              route.fulfill({ status: 409, json: { message: 'Frank Herbert: choose a format or explicitly select both. Nothing was changed.' } }) :
              route.fulfill({ json: {} });
          }
          if (pathname.endsWith('/author') && request.method() === 'GET') {
            return route.fulfill({ json: [{ id: 1, authorName: 'Frank Herbert', sortName: 'herbert frank', path: '/library/Dune/Frank Herbert' }] });
          }
          if (pathname.endsWith('/book') && request.method() === 'GET') {
            return route.fulfill({ json: [] });
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
        const deleteButton = page.getByRole('button', { name: 'Delete', exact: true });
        const both = page.getByRole('button', { name: strings.AuthorDeleteBothFormats, exact: true });
        const files = page.locator('input[name="deleteFiles"]');
        const exclusion = page.locator('input[name="addImportListExclusion"]');
        const media = page.getByRole('button', { name: strings.Ebooks, exact: true });
        assert.equal(await deleteButton.isDisabled(), true, 'All-format view must require an explicit choice');
        assert.equal(await both.count(), 0);
        await page.getByRole('button', { name: strings.AuthorDeleteSelectFormat, exact: true }).click();
        await page.getByText(strings.Ebooks, { exact: true }).click();
        await exclusion.focus();
        await page.keyboard.press('Space');
        assert.equal(await exclusion.isChecked(), true);
        const scopedRequest = page.waitForRequest((request) => request.url().endsWith('/author/editor') && request.method() === 'DELETE');
        await deleteButton.click();
        assert.deepEqual((await scopedRequest).postDataJSON(), { authorIds: [1, 2], mediaType: 'ebook', applyToBothFormats: false, deleteFiles: false, addImportListExclusion: true });

        await deleteButton.waitFor({ state: 'hidden' });
        await page.evaluate(() => window.renderDeleteFixture(false, 'ebook'));
        assert.equal(await exclusion.isChecked(), false);
        await files.focus();
        await page.keyboard.press('Space');
        assert.equal(await files.isChecked(), true);
        assert.equal(await page.getByText(strings.AuthorDeleteFormatFilesHelpText, { exact: true }).isVisible(), true);
        refuseDelete = true;
        await deleteButton.click();
        await page.getByText('Frank Herbert: choose a format or explicitly select both. Nothing was changed.').waitFor();
        assert.equal(await media.isVisible(), true, 'Refusal must preserve the requested scope');
        assert.equal(await files.isChecked(), true, 'Refusal must preserve the checkbox');
        assert.equal(await deleteButton.isEnabled(), true, 'Refusal must leave a working retry');
        await media.click();
        await page.getByText(strings.AuthorDeleteBothFormats, { exact: true }).click();
        const selectedPaths = ['/library/Dune/Frank Herbert', '/audiobooks/Dune/Frank Herbert', '/ebooks/Dune/Frank Herbert', '/library/Dune/Brian Herbert'];
        for (const selectedPath of selectedPaths) {
          assert.equal(await page.getByText(selectedPath, { exact: true }).count(), 1, `${selectedPath} must be disclosed once`);
        }
        assert.equal(await page.getByText(strings.AuthorDeleteFoldersHelpText, { exact: true }).isVisible(), true);
        assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `${theme} ${width}: bulk overflow`);
        await page.screenshot({ path: path.join(output, `${theme}-${width}-paths.png`) });
        refuseDelete = false;
        const wholeRequest = page.waitForRequest((request) => request.url().endsWith('/author/editor') && request.method() === 'DELETE');
        await deleteButton.click();
        assert.deepEqual((await wholeRequest).postDataJSON(), { authorIds: [1, 2], applyToBothFormats: true, deleteFiles: true, addImportListExclusion: false });

        await deleteButton.waitFor({ state: 'hidden' });
        await page.evaluate(() => window.renderDeleteFixture(true, 'ebook'));
        assert.equal(await media.isVisible(), true);
        assert.equal(await both.count(), 0);
        await files.focus();
        await page.keyboard.press('Space');
        assert.equal(await page.getByText(strings.AuthorDeleteFormatFilesHelpText, { exact: true }).isVisible(), true);
        assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `${theme} ${width}: single overflow`);
        await page.screenshot({ path: path.join(output, `${theme}-${width}-single.png`) });
        const singleRequest = page.waitForRequest((request) => new URL(request.url()).pathname.endsWith('/author/1') && request.method() === 'DELETE');
        await deleteButton.click();
        const singleQuery = new URL((await singleRequest).url()).searchParams;
        assert.equal(singleQuery.get('mediaType'), 'ebook');
        assert.equal(singleQuery.get('applyToBothFormats'), 'false');
        assert.equal(singleQuery.get('deleteFiles'), 'true');
        await page.waitForFunction(() => !window.getDeleteFixtureState().authors.isDeleting);
        assert.equal(await page.evaluate(() => window.getDeleteFixtureState().authors.items.some((author) => author.id === 1)), true, 'Scoped success must not remove the surviving author from state');

        await deleteButton.waitFor({ state: 'hidden' });
        await page.evaluate(() => window.renderDeleteFixture(true, 'audiobook'));
        await page.locator('input[name="readdAuthor"]').focus();
        await page.keyboard.press('Space');
        assert.equal(await both.isVisible(), true, 'Purge and re-add must visibly apply to the whole author');
        assert.equal(await files.isDisabled(), true);
        assert.equal(await exclusion.isDisabled(), true);
        const readdRequest = page.waitForRequest((request) => new URL(request.url()).pathname.endsWith('/author/1') && request.method() === 'DELETE');
        await page.getByRole('button', { name: strings.PurgeAndReaddAuthor, exact: true }).click();
        const readdQuery = new URL((await readdRequest).url()).searchParams;
        assert.equal(readdQuery.get('readdAuthor'), 'true');
        assert.equal(readdQuery.get('applyToBothFormats'), 'true');
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
