/* Run with an existing Playwright installation; no backend or library data is used. */
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const webpack = require('webpack');
const darkTheme = require('../src/Styles/Themes/dark');
const lightTheme = require('../src/Styles/Themes/light');

const { chromium } = require(process.env.CHAPTARR_PLAYWRIGHT_MODULE || 'playwright');
const root = path.resolve(__dirname, '../..');
const output = fs.mkdtempSync(path.join(os.tmpdir(), 'chaptarr-toolbar-'));
const config = require('./webpack.config')({ production: true });

config.entry = { fixture: path.join(__dirname, 'mobile-toolbar-fixture.js') };
config.output = { ...config.output, path: output, filename: 'fixture.js' };
config.optimization = { minimize: false };
config.plugins = config.plugins.filter((p) => ['DefinePlugin', 'MiniCssExtractPlugin'].includes(p.constructor.name));
// Fixture builds must not generate source declarations or overwrite application output.
for (const rule of config.module.rules) {
  if (Array.isArray(rule.use)) {
    rule.use = rule.use.filter((loader) => !String(typeof loader === 'string' ? loader : loader.loader).includes('css-modules-typescript-loader'));
  }
}

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
    const css = fs.readdirSync(path.join(output, 'Content')).find((file) => file.endsWith('.css'));
    const strings = JSON.parse(fs.readFileSync(path.join(root, 'src/NzbDrone.Core/Localization/Core/en.json')));
    let cases = 0;
    for (const theme of ['dark', 'light']) {
      for (const width of [320, 390, 600, 768, 769, 820, 900, 992, 1000, 1024, 1025, 1180, 1440]) {
        const context = await browser.newContext({ viewport: { width, height: 800 }, isMobile: width <= 1024, hasTouch: width <= 1024 });
        const page = await context.newPage();
        const errors = [];
        page.on('pageerror', (error) => errors.push(error.message));
        await page.route('**/*', (route) => {
          const pathname = new URL(route.request().url()).pathname;
          if (pathname.endsWith('/localization')) {
            return route.fulfill({ json: { Strings: strings } });
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
          const variables = Object.entries((theme === 'dark' ? darkTheme : lightTheme)).map(([key, value]) => `--${key}:${value}`).join(';');
          return route.fulfill({ contentType: 'text/html', body: `<!doctype html><meta name="viewport" content="width=device-width,initial-scale=1"><link rel="stylesheet" href="/style.css"><style>:root{${variables}}body{margin:0}#root{margin-left:${width > 768 ? 210 : 0}px}</style><div id="root"></div><div id="portal-root"></div><script>window.Chaptarr={isProduction:true,apiRoot:'/api/v1'};</script><script src="/fixture.js"></script>` });
        });
        await page.goto('http://toolbar.test/');
        await page.evaluate(() => window.renderToolbarFixture());
        await page.getByText('Filter', { exact: true }).waitFor();
        // Focusing must reveal the actual control, not merely keep the document width fixed.
        const filter = page.getByText('Filter', { exact: true }).locator('xpath=ancestor::*[self::button or self::a][1]');
        await filter.focus();
        const visible = await filter.evaluate((element) => {
          const r = element.getBoundingClientRect();
          for (let parent = element.parentElement; parent; parent = parent.parentElement) {
            const style = getComputedStyle(parent);
            const p = parent.getBoundingClientRect();
            if (['hidden', 'auto', 'scroll'].includes(style.overflowX) && (r.left < p.left - 1 || r.right > p.right + 1)) {
              return false;
            }
          }
          return r.left >= 0 && r.right <= innerWidth;
        });
        assert(visible, `${theme} ${width}px: Filter is clipped after focus`);
        await filter.click();
        await page.getByText('Filter choice', { exact: true }).waitFor();
        await page.mouse.click(5, 300);
        await page.getByTitle('Options', { exact: true }).click();
        await page.getByText('Columns', { exact: true }).waitFor();
        await page.keyboard.press('Escape');
        if (process.env.CHAPTARR_LAYOUT_ARTIFACTS && [390, 900, 1025].includes(width)) {
          fs.mkdirSync(process.env.CHAPTARR_LAYOUT_ARTIFACTS, { recursive: true });
          await page.screenshot({ path: path.join(process.env.CHAPTARR_LAYOUT_ARTIFACTS, `${theme}-${width}.png`) });
        }
        if (width === 900) {
          await page.locator('button[aria-pressed]').filter({ hasText: 'eBooks' }).click();
          await page.setViewportSize({ width: 1440, height: 800 });
          await page.setViewportSize({ width: 900, height: 800 });
          assert.equal(await page.locator('[aria-pressed=true]').textContent(), '📖eBooks');
          await filter.click();
          await page.getByText('Filter choice', { exact: true }).waitFor();
        }
        await page.evaluate(() => window.renderHeaderFixture());
        const bookmark = page.locator('[class*=AuthorDetailsHeader-toggleMonitoredContainer] svg').first();
        await bookmark.waitFor();
        const clippedIcons = await page.evaluate(() => [...document.querySelectorAll('svg')].filter((icon) => {
          const r = icon.getBoundingClientRect();
          if (!r.width || r.left < 0 || r.right > innerWidth) {
            return false;
          }
          for (let parent = icon.parentElement; parent; parent = parent.parentElement) {
            const style = getComputedStyle(parent);
            const p = parent.getBoundingClientRect();
            if (['hidden', 'auto', 'scroll'].includes(style.overflowY) && (r.top < p.top - 0.5 || r.bottom > p.bottom + 0.5)) {
              return true;
            }
          }
          return false;
        }).map((icon) => icon.getAttribute('data-icon')));
        assert.deepEqual(clippedIcons, [], `${theme} ${width}px: header icons clipped vertically`);
        assert.deepEqual(errors, [], `${theme} ${width}px: browser errors`);
        cases++;
        await context.close();
      }
    }
    console.log(`PASS: ${cases} toolbar cases, including sidebar widths, Filter/Options interactions, and header icons`);
  } finally {
    await browser.close();
  }
})().catch((error) => {
  console.error(error.message);
  process.exitCode = 1;
}).finally(() => fs.rmSync(output, { recursive: true, force: true }));
