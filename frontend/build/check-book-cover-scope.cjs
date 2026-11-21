/* Count actual browser image requests from the real library list and details components. */
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const webpack = require('webpack');
const darkTheme = require('../src/Styles/Themes/dark');
const { chromium } = require('playwright');

const root = path.resolve(__dirname, '../..');
const themeVariables = Object.entries(darkTheme).map(([key, value]) => `--${key}:${value}`).join(';');
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
    const bookEdits = [];
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
      if (pathname === '/api/v1/book/12' && route.request().method() === 'PUT') {
        const payload = route.request().postDataJSON();
        bookEdits.push(payload);
        return route.fulfill({ json: payload });
      }
      if (pathname.endsWith('/localization')) {
        return route.fulfill({ json: { Strings: strings } });
      }
      if (pathname.startsWith('/MediaCover/') || pathname.startsWith('/MediaCoverProxy/') || url.hostname === 'images.example') {
        bookImages.push({ host: url.hostname, path: pathname });
        if (pathname === '/MediaCoverProxy/book/33/unavailable.jpg') {
          return route.fulfill({ status: 404, body: '' });
        }
        return route.fulfill({ contentType: 'image/svg+xml', body: svg });
      }
      if (pathname !== '/') {
        return route.fulfill({ status: 404, body: '' });
      }
      return route.fulfill({
        contentType: 'text/html',
        body: `<!doctype html><meta name="viewport" content="width=device-width,initial-scale=1"><link rel="stylesheet" href="/style.css"><style>:root{${themeVariables}}body{margin:0;background:var(--pageBackground)}</style><div id="root"></div><div id="portal-root"></div><script>window.Chaptarr={urlBase:"",apiRoot:"/api/v1",isProduction:true};</script><script src="/fixture.js"></script>`
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
    const unavailableCover = {
      coverType: 'cover',
      url: null,
      temporaryUrl: '/MediaCoverProxy/book/33/unavailable.jpg',
      remoteUrl: 'https://images.example/error.jpg'
    };
    for (const width of [1440, 390]) {
      await page.setViewportSize({ width, height: 844 });
      bookImages.length = 0;
      await page.evaluate((image) => window.renderBookCoverFixture('details', image), unavailableCover);
      await page.waitForFunction(() => {
        const cover = document.querySelector('#root img');
        return cover?.src.startsWith('data:image/png;') && cover.naturalWidth > 0;
      });
      assert.deepEqual(bookImages, [{ host: 'book-cover-scope.test', path: '/MediaCoverProxy/book/33/unavailable.jpg' }], 'an exhausted proxy shows the existing placeholder without fetching a remote URL');
      if (width < 768) {
        assert.equal(await page.locator('#root img').isVisible(), false, 'mobile keeps the existing compact details header without a cover column');
      } else {
        const bounds = await page.locator('#root img').boundingBox();
        assert.ok(bounds.width > 0 && bounds.x >= 0 && bounds.x + bounds.width <= width, `details placeholder fits the ${width}px viewport`);
      }
      const scrollWidth = await page.evaluate(() => document.documentElement.scrollWidth);
      assert.ok(scrollWidth <= width, `details header must not overflow the ${width}px viewport`);
      await page.screenshot({ path: path.join(os.tmpdir(), `chaptarr-cover-fallback-${width}.png`) });
    }
    const editCases = [];
    for (const mediaType of ['audiobook', 'ebook']) {
      for (const monitored of [true, false]) {
        const requestCount = bookEdits.length;
        await page.evaluate((input) => window.renderEditBookFixture(input.mediaType, input.monitored), { mediaType, monitored });
        const checkbox = page.locator('input[name="monitored"]');
        assert.equal(await checkbox.isChecked(), monitored);
        await checkbox.setChecked(!monitored, { force: true });
        assert.equal(await checkbox.isChecked(), !monitored);
        const saved = page.waitForResponse((response) => new URL(response.url()).pathname === '/api/v1/book/12' && response.request().method() === 'PUT');
        await page.getByRole('button', { name: strings.Save, exact: true }).click();
        await saved;
        assert.equal(bookEdits.length, requestCount + 1, 'one existing book save request');
        const payload = bookEdits.at(-1);
        const otherMedia = mediaType === 'audiobook' ? 'ebook' : 'audiobook';
        assert.equal(payload.monitored, !monitored);
        assert.equal(payload[`${otherMedia}Monitored`], !monitored, 'opposite media flag remains unchanged');
        assert.equal(payload.anyEditionOk, true);
        assert.deepEqual(payload.editions, []);
        assert.equal(payload.mediaType, mediaType);
        editCases.push({ mediaType, before: monitored, selected: !monitored, explicitSideValue: payload[`${mediaType}Monitored`] });
      }
    }
    console.log(`Edit Book checkbox payloads: ${JSON.stringify(editCases)}`);
    assert.ok(editCases.every((item) => item.explicitSideValue === item.selected), 'the selected checkbox must reach the authoritative per-media field for every direction and media');
    assert.deepEqual(errors, []);
    console.log('Book cover scope browser fixture passed.');
  } finally {
    await browser.close();
  }
})().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
