/* Verify Enter dispatch from real ConfirmModal controls in Chromium. */
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const webpack = require('webpack');
const { chromium } = require('playwright');

const output = fs.mkdtempSync(path.join(os.tmpdir(), 'chaptarr-confirm-modal-'));
const config = require('./webpack.config')({ production: true });

config.entry = { fixture: path.join(__dirname, 'confirm-modal-fixture.js') };
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

async function openFixture(browser) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (error) => errors.push(error.message));
  page.on('console', (message) => {
    if (message.type() === 'error') {
      errors.push(message.text());
    }
  });

  const cssName = fs.readdirSync(path.join(output, 'Content')).find((name) => name.endsWith('.css'));
  const cssPath = path.join(output, 'Content', cssName);
  await page.route('**/*', (route) => {
    const pathname = new URL(route.request().url()).pathname;
    if (pathname === '/fixture.js') {
      return route.fulfill({ path: path.join(output, 'fixture.js'), contentType: 'text/javascript' });
    }
    if (pathname === '/style.css') {
      return route.fulfill({ path: cssPath, contentType: 'text/css' });
    }
    if (pathname === '/') {
      return route.fulfill({
        contentType: 'text/html',
        body: '<!doctype html><html><head><link rel="stylesheet" href="/style.css"></head><body><div id="root"></div><div id="portal-root"></div><script>window.Chaptarr={urlBase:"",apiRoot:"/api/v1",isProduction:true,instanceName:"Chaptarr"};</script><script src="/fixture.js"></script></body></html>'
      });
    }

    const assetPath = path.resolve(output, `.${decodeURIComponent(pathname)}`);
    if (assetPath.startsWith(`${output}${path.sep}`) && fs.existsSync(assetPath) && fs.statSync(assetPath).isFile()) {
      return route.fulfill({ path: assetPath });
    }

    return route.fulfill({ status: 204, body: '' });
  });

  await page.goto('http://confirm-modal.test/');
  await page.waitForFunction(() => window.fixtureReady === true);
  await page.getByTestId('modal-content').waitFor();
  return { page, errors };
}

async function assertEnterDispatch(browser, focusTarget, expectedCounts) {
  const { page, errors } = await openFixture(browser);
  try {
    const target = focusTarget === 'content' ?
      page.getByTestId('modal-content') :
      page.getByRole('button', { name: focusTarget, exact: true });
    await target.focus();
    assert.equal(await target.evaluate((element) => document.activeElement === element), true, `${focusTarget} should have focus before Enter`);

    await page.keyboard.press('Enter');
    assert.equal(await page.evaluate(() => window.confirmCalls), expectedCounts.confirm, `${focusTarget}: Confirm should run exactly ${expectedCounts.confirm} time(s)`);
    assert.equal(await page.evaluate(() => window.cancelCalls), expectedCounts.cancel, `${focusTarget}: Cancel should run exactly ${expectedCounts.cancel} time(s)`);
    assert.deepEqual(errors, [], `${focusTarget}: the modal should not produce browser errors`);
  } finally {
    await page.close();
  }
}

(async() => {
  let browser = null;
  try {
    await compileFixture();
    browser = await chromium.launch();

    await assertEnterDispatch(browser, 'Cancel', { confirm: 0, cancel: 1 });
    await assertEnterDispatch(browser, 'Confirm', { confirm: 1, cancel: 0 });
    await assertEnterDispatch(browser, 'content', { confirm: 1, cancel: 0 });

    console.log('ConfirmModal Enter browser checks passed.');
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
