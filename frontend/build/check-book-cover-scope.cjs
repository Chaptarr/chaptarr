/* Count actual browser image requests from the real library list and details components. */
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const webpack = require('webpack');
const { chromium } = require('playwright');

const root = path.resolve(__dirname, '../..');
const output = fs.mkdtempSync(path.join(os.tmpdir(), 'chaptarr-book-cover-scope-'));
const config = require('./webpack.config')({ production: true });

config.entry = { fixture: path.join(__dirname, 'book-cover-scope-fixture.js') };
config.output = { ...config.output, path: output, filename: 'fixture.js' };
config.optimization = { minimize: false };
config.plugins = config.plugins.filter((plugin) => ['DefinePlugin', 'MiniCssExtractPlugin'].includes(plugin.constructor.name));
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
    const page = await browser.newPage();
    const imageRequests = [];
    const forbiddenRequests = [];
    const errors = [];
    const bookImages = [];
    const svg = '<svg xmlns="http://www.w3.org/2000/svg" width="200" height="250"><rect width="200" height="250" fill="#226688"/></svg>';
    const strings = JSON.parse(fs.readFileSync(path.join(root, 'src/NzbDrone.Core/Localization/Core/en.json')));
    page.on('pageerror', (error) => errors.push(error.message));
    page.on('request', (request) => {
      const url = new URL(request.url());
      if (request.resourceType() === 'image') {
        imageRequests.push(url.pathname);
      }
      if (url.pathname.endsWith('/loadImage') || url.pathname.includes('/ensure')) {
        forbiddenRequests.push(url.pathname);
      }
    });
    await page.route('**/*', (route) => {
      const url = new URL(route.request().url());
      const pathname = url.pathname;
      if (pathname === '/fixture.js') {
        return route.fulfill({ path: path.join(output, 'fixture.js'), contentType: 'text/javascript' });
      }
      if (pathname === '/style.css') {
        const css = fs.readdirSync(path.join(output, 'Content')).find((name) => name.endsWith('.css'));
        return route.fulfill({ path: path.join(output, 'Content', css), contentType: 'text/css' });
      }
      if (pathname.endsWith('/localization')) {
        return route.fulfill({ json: { Strings: strings } });
      }
      if (pathname.startsWith('/MediaCover/') || pathname.startsWith('/MediaCoverProxy/') || url.hostname === 'images.example') {
        bookImages.push({ host: url.hostname, path: pathname });
        return route.fulfill({ contentType: 'image/svg+xml', body: svg });
      }
      if (pathname !== '/') {
        return route.fulfill({ status: 404, body: '' });
      }
      return route.fulfill({
        contentType: 'text/html',
        body: '<!doctype html><link rel="stylesheet" href="/style.css"><div id="root"></div><div id="portal-root"></div><script>window.Chaptarr={urlBase:"",apiRoot:"/api/v1",isProduction:true};</script><script src="/fixture.js"></script>'
      });
    });
    await page.goto('http://book-cover-scope.test/');
    await page.evaluate(() => window.prepareBookCoverFixture());

    const unmonitoredFileless = {
      coverType: 'cover',
      url: '/MediaCover/Books/old-cache/cover-250.jpg',
      temporaryUrl: '/MediaCoverProxy/book/31/unmonitored.jpg?v=current',
      remoteUrl: 'https://images.example/unmonitored.jpg'
    };
    await page.evaluate((image) => window.renderBookCoverFixture('list', image, 1000), unmonitoredFileless);
    await page.waitForFunction(() => document.querySelectorAll('#root img').length === 1000);
    await page.waitForTimeout(100);
    assert.deepEqual(bookImages, [], '1,000 ineligible library rows must issue no local, proxy, or remote image request');

    const placeholderWithoutProxy = {
      coverType: 'cover',
      url: null,
      temporaryUrl: '',
      remoteUrl: 'https://images.example/nophoto.jpg'
    };
    await page.evaluate((image) => window.renderBookCoverFixture('list', image, 1000), placeholderWithoutProxy);
    await page.waitForFunction(() => document.querySelectorAll('#root img').length === 1000);
    await page.waitForTimeout(100);
    assert.deepEqual(bookImages, [], 'a missing temporary proxy URL must still suppress provider-placeholder requests');

    const eligible = {
      coverType: 'cover',
      url: '/MediaCover/Books/eligible/cover.jpg',
      remoteUrl: 'https://images.example/eligible.jpg'
    };
    await page.evaluate((image) => window.renderBookCoverFixture('cover', image), eligible);
    await page.waitForFunction(() => document.querySelector('#root img')?.naturalWidth > 0);
    assert.equal(bookImages.length, 1);
    assert.equal(bookImages[0].path, '/MediaCover/Books/eligible/cover-250.jpg', 'eligible covers continue through the local cache');

    bookImages.length = 0;
    await page.evaluate((image) => window.renderBookCoverFixture('details', image), unmonitoredFileless);
    await page.waitForFunction(() => document.querySelector('#root img')?.naturalWidth > 0);
    assert.deepEqual(bookImages, [{ host: 'book-cover-scope.test', path: '/MediaCoverProxy/book/31/unmonitored.jpg' }], 'the real details header may fetch the temporary proxy exactly once');
    assert.deepEqual(forbiddenRequests, [], 'cover display must not call a backend ensure/download endpoint');

    const detailRequestCount = bookImages.length;
    await page.evaluate((image) => window.renderBookCoverFixture('list', image, 1000), unmonitoredFileless);
    await page.waitForFunction(() => document.querySelectorAll('#root img').length === 1000);
    await page.waitForTimeout(100);
    assert.equal(bookImages.length, detailRequestCount, 'returning to library rows must not reuse the detail-only opt-in');
    bookImages.length = 0;
    const uncachedAllBooks = {
      coverType: 'cover',
      url: '/MediaCoverProxy/book/32/uncached.jpg?v=first',
      remoteUrl: 'https://images.example/uncached.jpg'
    };
    await page.evaluate((image) => window.renderBookCoverFixture('cover', image), uncachedAllBooks);
    await page.waitForFunction(() => document.querySelector('#root img')?.naturalWidth > 0);
    assert.deepEqual(bookImages, [{ host: 'book-cover-scope.test', path: '/MediaCoverProxy/book/32/uncached.jpg' }], 'uncached All Books covers use the book route without a direct provider request');
    assert.deepEqual(forbiddenRequests, [], 'switching scope must not start persistent cover downloads from display');
    assert.deepEqual(errors, []);
    console.log('Book cover scope browser fixture passed.');
  } finally {
    await browser.close();
  }
})().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
