const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const webpack = require('webpack');
const { chromium } = require('playwright');
const darkTheme = require('../src/Styles/Themes/dark');
const lightTheme = require('../src/Styles/Themes/light');

const root = path.resolve(__dirname, '../..');
const output = fs.mkdtempSync(path.join(os.tmpdir(), 'chaptarr-book-route-guards-'));
const config = require('./webpack.config')({ production: true });

config.entry = { fixture: path.join(__dirname, 'book-route-guard-fixture.js') };
config.output = { ...config.output, path: output, filename: 'fixture.js' };
config.optimization = { minimize: false };
config.plugins = config.plugins.filter((plugin) => ['DefinePlugin', 'MiniCssExtractPlugin'].includes(plugin.constructor.name));
config.plugins.push(new webpack.NormalModuleReplacementPlugin(/^\.\/BookDetailsConnector$/, path.join(__dirname, 'BookRouteDetailWitness.js')));
for (const rule of config.module.rules) {
  if (Array.isArray(rule.use)) {
    rule.use = rule.use.filter((loader) => !String(typeof loader === 'string' ? loader : loader.loader).includes('css-modules-typescript-loader'));
  }
}

(async() => {
  let compiler = null;
  let browser = null;
  try {
    compiler = webpack(config);
    await new Promise((resolve, reject) => compiler.run((error, stats) => (error || stats.hasErrors() ?
      reject(error || new Error(stats.toString({ all: false, errors: true }))) : resolve())));
    await new Promise((resolve) => compiler.close(resolve));
    compiler = null;
    browser = await chromium.launch();
    const page = await browser.newPage();
    const requests = [];
    const errors = [];
    const held = new Map();
    const strings = JSON.parse(fs.readFileSync(path.join(root, 'src/NzbDrone.Core/Localization/Core/en.json')));
    const book = (id, authorId = 15) => ({ id, authorId, mediaType: 'audiobook', title: `Book ${id}`, titleSlug: `book-${id}` });
    const authors = [{ id: 15, authorName: 'Fixture Author' }];
    let mode = 'empty';
    page.on('pageerror', (error) => errors.push(error.message));
    await page.route('**/*', async(route) => {
      const url = new URL(route.request().url());
      if (url.pathname === '/fixture.js') {
        return route.fulfill({ path: path.join(output, 'fixture.js'), contentType: 'text/javascript' });
      }
      if (url.pathname === '/style.css') {
        const css = fs.readdirSync(path.join(output, 'Content')).find((name) => name.endsWith('.css'));
        return route.fulfill({ path: path.join(output, 'Content', css), contentType: 'text/css' });
      }
      if (url.pathname.endsWith('/localization')) {
        return route.fulfill({ json: { Strings: strings } });
      }
      if (url.pathname.startsWith('/api/v1/')) {
        requests.push(url.pathname + url.search);
        assert.equal(url.searchParams.has('onComplete'), false, 'completion callbacks must never enter API queries');
        if (mode === 'held') {
          held.set(url.searchParams.get('bookId') || url.searchParams.get('authorId') || 'all', route);
          return;
        }
        if (mode === 'error' || (mode === 'author-error' && url.pathname.startsWith('/api/v1/author/'))) {
          return route.fulfill({ status: 500, json: { message: 'Fixture service unavailable' } });
        }
        if (mode === '404' || (mode === 'author-404' && url.pathname.startsWith('/api/v1/author/'))) {
          return route.fulfill({ status: 404, json: {} });
        }
        if (mode === 'network') {
          return route.abort('failed');
        }
        if (url.pathname.startsWith('/api/v1/author/')) {
          return route.fulfill({ json: authors[0] });
        }
        if (url.searchParams.has('authorId')) {
          return route.fulfill({ json: [book(12), book(13)] });
        }
        if (mode === 'sibling') {
          return route.fulfill({ json: [{ ...book(44), mediaType: 'ebook', titleSlug: 'different-ebook-slug' }] });
        }
        if (mode === 'found') {
          return route.fulfill({ json: [book(12)] });
        }
        return route.fulfill({ json: [] });
      }
      if (url.pathname !== '/') {
        return route.fulfill({ status: 404, body: '' });
      }
      const variables = Object.entries(darkTheme).map(([key, value]) => `--${key}:${value}`).join(';');
      return route.fulfill({ contentType: 'text/html', body: `<!doctype html><meta name="viewport" content="width=device-width,initial-scale=1"><link rel="stylesheet" href="/style.css"><style>:root{${variables}}body{margin:0;background:var(--pageBackground)}</style><div id="root"></div><div id="portal-root"></div><script>window.Chaptarr={urlBase:"",apiRoot:"/api/v1",isProduction:true,instanceName:"Fixture"};</script><script src="/fixture.js"></script>` });
    });
    await page.goto('http://book-route.test/');
    await page.evaluate(() => window.prepareBookRouteFixture());
    const mount = (input) => page.evaluate((value) => window.mountBookRouteFixture(value), input);
    const settled = () => page.waitForFunction(() => !window.bookRouteFixture.state().books.isFetching && !window.bookRouteFixture.state().authors.isFetching);
    const text = (value) => page.getByText(value, { exact: true }).waitFor();
    const stable = async(count) => {
      await page.waitForTimeout(150);
      assert.equal(requests.length, count);
    };
    const waitForHeld = async(key) => {
      const deadline = Date.now() + 5000;
      while (!held.has(key) && Date.now() < deadline) {
        await new Promise((resolve) => setTimeout(resolve, 10));
      }
      assert.ok(held.has(key), `request ${key} must be held`);
    };
    const waitForRequestCount = async(predicate, count, message) => {
      const deadline = Date.now() + 5000;
      while (requests.filter(predicate).length < count && Date.now() < deadline) {
        await new Promise((resolve) => setTimeout(resolve, 10));
      }
      assert.equal(requests.filter(predicate).length, count, message);
    };
    const release = async(key, payload = {}) => {
      await waitForHeld(key);
      await held.get(key).fulfill({ json: [], ...payload });
      held.delete(key);
    };

    for (mode of ['empty', '404', 'error', 'network']) {
      requests.length = 0;
      await mount({ route: '/book/999', authors });
      const expected = { error: 'Fixture service unavailable', network: strings.NarratorDetailsUnableToLoadBooks };
      await text(expected[mode] || strings.SorryThatBookCannotBeFound);
      await stable(1);
      await page.evaluate(() => window.bookRouteFixture.navigate('/book/998'));
      await settled();
      await stable(2);
      await mount({ route: '/book/999', authors });
      await settled();
      await stable(3);
    }
    console.log('Missing, 404, server and network failures settle; navigation and remount retry once.');

    mode = 'held';
    requests.length = 0;
    await mount({ route: '/book/12', authors, initialFetch: {} });
    await waitForHeld('all');
    await stable(1);
    await release('all');
    await waitForHeld('12');
    await page.evaluate(() => window.bookRouteFixture.fetch({ authorId: 99 }));
    await release('99');
    await waitForRequestCount((url) => url.includes('bookId=12'), 2, 'cancellation cannot establish absence');
    mode = 'found';
    await release('12', { json: [book(12)] });
    await page.locator('[data-loaded-book="12"]').waitFor();
    await stable(5);
    console.log('Startup overlap and superseded lookup recover without false Not Found or loops.');

    for (mode of ['author-error', 'author-404']) {
      requests.length = 0;
      await mount({ route: '/book/12', books: [book(12), book(13)] });
      await text(mode === 'author-error' ? 'Fixture service unavailable' : strings.SorryThatBookCannotBeFound);
      await stable(1);
    }
    mode = 'found';
    requests.length = 0;
    await mount({ route: '/audiobook/book/book-12', authors });
    await page.locator('[data-loaded-book="12"]').waitFor();
    assert.equal(await page.evaluate(() => window.bookRouteFixture.state().path), '/book/12');
    await stable(2);
    assert.ok(requests[0].includes('mediaType=audiobook'));
    console.log('Missing author settles; successful scoped links retain redirect and sibling navigation.');
    mode = 'sibling';
    requests.length = 0;
    await mount({ route: '/ebook/book/original-audiobook-slug', authors });
    await page.locator('[data-loaded-book="44"]').waitFor();
    assert.equal(await page.evaluate(() => window.bookRouteFixture.state().path), '/book/44');
    assert.ok(requests[0].includes('mediaType=ebook'));
    assert.equal(requests.filter((url) => url.includes('bookId=original-audiobook-slug')).length, 1);
    console.log('Scoped slug uses the server-resolved book even when its slug differs.');

    mode = 'held';
    requests.length = 0;
    await mount({ route: '/book/21', authors });
    await page.waitForTimeout(50);
    await page.evaluate(() => window.bookRouteFixture.navigate('/book/22'));
    await release('21', { status: 500, json: { message: 'Old route failure' } });
    await page.waitForTimeout(50);
    await release('22');
    await text(strings.SorryThatBookCannotBeFound);
    await stable(2);
    await page.evaluate(() => window.bookRouteFixture.navigate('/book/21'));
    await page.waitForTimeout(50);
    mode = 'found';
    await release('21', { json: [book(21), book(22)] });
    await page.locator('[data-loaded-book="21"]').waitFor();
    await stable(3);
    mode = 'held';
    await page.evaluate(() => window.bookRouteFixture.navigate('/book/23'));
    await page.waitForTimeout(50);
    await page.evaluate(() => window.bookRouteFixture.unmount());
    await release('23', { status: 500, json: { message: 'After page exit' } });
    await stable(4);
    assert.equal(await page.locator('#root').textContent(), '');
    console.log('Route changes, return navigation and page exit ignore obsolete completions.');

    mode = 'error';
    for (const [themeName, theme] of [['dark', darkTheme], ['light', lightTheme]]) {
      for (const width of [1440, 390]) {
        await page.setViewportSize({ width, height: 844 });
        await page.evaluate((variables) => Object.entries(variables).forEach(([key, value]) => document.documentElement.style.setProperty(`--${key}`, value)), theme);
        await mount({ route: '/book/999', authors });
        await text('Fixture service unavailable');
        assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `${themeName}/${width}: error must fit`);
        if (process.env.CHAPTARR_BROWSER_EVIDENCE) {
          await page.screenshot({ path: path.join(process.env.CHAPTARR_BROWSER_EVIDENCE, `book-route-${themeName}-${width}.png`) });
        }
      }
    }
    assert.deepEqual(errors, []);
    console.log('Book route guard browser checks passed, including dark/light at 390px and 1440px.');
  } finally {
    if (browser) {
      await browser.close();
    }
    if (compiler) {
      await new Promise((resolve) => compiler.close(resolve));
    }
    fs.rmSync(output, { recursive: true, force: true });
    console.log(`Temporary browser output removed: ${!fs.existsSync(output)}`);
  }
})().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
