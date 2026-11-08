const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const webpack = require('webpack');
const { chromium } = require('playwright');
const createWebpackConfig = require('./webpack.config');

const root = path.resolve(__dirname, '../..');
const output = fs.mkdtempSync(path.join(os.tmpdir(), 'chaptarr-update-popup-'));
const strings = JSON.parse(fs.readFileSync(path.join(root, 'src/NzbDrone.Core/Localization/Core/en.json')));
const config = createWebpackConfig({ production: true });
config.entry = { fixture: path.join(__dirname, 'update-popup-fixture.js') };
config.output = { ...config.output, path: output, filename: 'fixture.js', publicPath: '/' };
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
    await new Promise((resolve, reject) => compiler.run((error, stats) => {
      compiler.close(() => {});
      if (error || stats.hasErrors()) {
        reject(error || new Error(stats.toString({ all: false, errors: true })));
      } else {
        resolve();
      }
    }));
    browser = await chromium.launch({ headless: true });
    for (const [theme, width] of [['dark', 1280], ['light', 1280], ['dark', 390], ['light', 390]]) {
      const page = await browser.newPage({ viewport: { width, height: 800 } });
      const errors = [];
      page.on('pageerror', (e) => errors.push(e.message));
      await page.route('**/*', async(route) => {
        const url = new URL(route.request().url());
        assert.equal(url.hostname, 'updates.test');
        if (url.pathname.endsWith('/localization')) {
          return route.fulfill({ json: { Strings: strings } });
        }
        if (url.pathname.endsWith('/system/updates')) {
          return route.fulfill({ contentType: 'text/html', body: 'Full update history route' });
        }
        if (url.pathname === '/fixture.js') {
          return route.fulfill({ contentType: 'application/javascript', body: fs.readFileSync(path.join(output, 'fixture.js')) });
        }
        if (url.pathname.endsWith('.css')) {
          const css = fs.readdirSync(path.join(output, 'Content')).find((file) => file.endsWith('.css'));
          return route.fulfill({ contentType: 'text/css', body: fs.readFileSync(path.join(output, 'Content', css)) });
        }
        if (url.pathname === '/') {
          return route.fulfill({ contentType: 'text/html', body: '<html><head><link rel="stylesheet" href="/fixture.css"></head><body><div id="root"></div><div id="portal-root"></div><script>window.Chaptarr={apiRoot:"/api/v1",apiKey:"test",urlBase:"/chaptarr"};</script><script src="/fixture.js"></script></body></html>' });
        }
        return route.fulfill({ status: 404, body: '' });
      });
      try {
        await page.goto('http://updates.test/');
        await page.waitForFunction(() => window.fixtureReady);
        const row = (version, note) => ({ version, changes: { new: [note], fixed: [] } });
        const items = [row('0.9.999', 'Future note'), row('0.9.991', 'Installed release note'), row('0.9.980', 'Skipped release note'), row('0.9.958', 'Old release note')];
        for (const previous of ['0.9.958', 'unknown', '0.9.991']) {
          await page.evaluate(([rows, prev, t]) => window.renderPopup(rows, prev, t), [items, previous, theme]);
          await page.getByText('Installed release note', { exact: true }).waitFor();
          for (const note of ['Future note', 'Skipped release note', 'Old release note']) {
            assert.equal(await page.getByText(note, { exact: true }).count(), 0);
          }
        }
        await page.evaluate(([rows, t]) => window.renderPopup(rows, 'unknown', t), [[row('0.9.980', 'Wrong release note')], theme]);
        assert.equal(await page.getByText('Wrong release note', { exact: true }).count(), 0);
        // The API's existing in-binary supplement uses the installed version, even when appended last.
        await page.evaluate(([rows, t]) => window.renderPopup(rows, 'unknown', t), [[row('0.9.980', 'Old feed row'), row('0.9.991', 'Installed fallback note')], theme]);
        await page.getByText('Installed fallback note', { exact: true }).waitFor();
        assert.equal(await page.getByText('Old feed row', { exact: true }).count(), 0);
        // The API sorts the installed four-part supplement first when the feed lags.
        await page.evaluate(([rows, t]) => window.renderPopup(rows, 'unknown', t, '0.9.991.0'), [[row('0.9.991.0', 'Four-part installed note'), row('0.9.980', 'Old feed row')], theme]);
        await page.getByText('Four-part installed note', { exact: true }).waitFor();
        assert.equal(await page.getByText('Old feed row', { exact: true }).count(), 0);
        // A three-part feed entry is the same release as assembly version x.y.z.0.
        await page.evaluate(([rows, t]) => window.renderPopup(rows, 'unknown', t, '0.9.991.0'), [[row('0.9.991.1', 'Different revision'), row('0.9.991', 'Same release from manifest')], theme]);
        await page.getByText('Same release from manifest', { exact: true }).waitFor();
        assert.equal(await page.getByText('Different revision', { exact: true }).count(), 0);
        await page.evaluate((t) => window.renderPopup([], 'unknown', t, '0.9.991.0', { status: 503 }), theme);
        assert.equal(await page.getByText(strings.FailedToFetchUpdates, { exact: true }).count(), 1);
        assert.equal(await page.getByText(strings.MaintenanceRelease, { exact: true }).count(), 0);
        if (process.env.CHAPTARR_LAYOUT_ARTIFACTS) {
          fs.mkdirSync(process.env.CHAPTARR_LAYOUT_ARTIFACTS, { recursive: true });
          await page.screenshot({ path: path.join(process.env.CHAPTARR_LAYOUT_ARTIFACTS, `popup-${theme}-${width}.png`) });
        }
        await page.getByRole('button', { name: strings.RecentChanges, exact: true }).click();
        await page.waitForURL('**/chaptarr/system/updates');
        assert.deepEqual(errors, []);
        console.log(`${theme} ${width}: installed only, unknown prior, same version, absent row, four-part supplement, three-part manifest, feed error, full-history navigation passed`);
      } finally {
        await page.close();
      }
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
