const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const webpack = require('webpack');
const { chromium } = require('playwright');
const createWebpackConfig = require('./webpack.config');

const root = path.resolve(__dirname, '../..');
const output = fs.mkdtempSync(path.join(os.tmpdir(), 'chaptarr-header-search-'));
const strings = JSON.parse(fs.readFileSync(path.join(root, 'src/NzbDrone.Core/Localization/Core/en.json')));
const config = createWebpackConfig({ production: true });
config.entry = { fixture: path.join(__dirname, 'header-search-fixture.js') };
config.output = { ...config.output, path: output, filename: 'fixture.js', publicPath: '/' };
config.optimization = { ...config.optimization, minimize: false, splitChunks: false, runtimeChunk: false };
config.plugins = config.plugins.filter((plugin) => ['DefinePlugin', 'MiniCssExtractPlugin'].includes(plugin.constructor.name));
for (const rule of config.module.rules) {
  if (Array.isArray(rule.use)) {
    rule.use = rule.use.filter((loader) => !String(typeof loader === 'string' ? loader : loader.loader).includes('css-modules-typescript-loader'));
  }
}

async function verify(browser, theme, width) {
  const page = await browser.newPage({ viewport: { width, height: 800 } });
  const errors = [];
  const requests = [];
  const pending = [];
  const addNewRequests = [];
  const heldSearches = [];
  let hold = false;
  page.on('pageerror', (error) => errors.push(error.message));
  const book = (id, title, exactMatch) => ({ id, title, authorName: 'Writer', exactMatch, monitored: true });
  const responses = {
    Hermione: { books: [{ ...book(22, 'Pocket Potters: Harry', false), matchedEditionTitle: 'Pocket Potters: Hermione' }] },
    It: { authors: [{ id: 10, name: 'It Author', exactMatch: false }], books: [book(1, 'It', true), book(2, 'It Starts With Us', false)] },
    'It St': { books: [book(2, 'It Starts With Us', false)] },
    Ann: { authors: [{ id: 12, name: 'Ann', exactMatch: true }, { id: 13, name: 'Ann Pat', exactMatch: false }], books: [book(3, 'Ann of Somewhere', false)] }
  };
  const waitForPending = async(term) => {
    const deadline = Date.now() + 5000;
    while (!pending.some((entry) => entry.term === term)) {
      assert.ok(Date.now() < deadline, `No intercepted request for ${term}`);
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
  };
  const releasePending = async() => {
    for (const { route, term } of pending.splice(0)) {
      await route.fulfill({ json: responses[term] || {} });
    }
  };
  await page.route('**/*', async(route) => {
    const url = new URL(route.request().url());
    assert.equal(url.hostname, 'header-search.test');
    if (url.pathname.endsWith('/localization')) {
      return route.fulfill({ json: { Strings: strings } });
    }
    if (url.pathname === '/api/v1/search') {
      addNewRequests.push(Object.fromEntries(url.searchParams));
      const author = (id, authorName) => ({ id, author: { authorName, foreignAuthorId: `gr:${id}` } });
      const data = [author(1, 'Rachel Louise Adams'), { id: 2, book: { title: 'John Adams' } },
        author(3, 'Rachel Adams'), { id: 4, series: { title: 'Related series' } }, author(5, 'Adam Smith'),
        author(6, 'Rachel Lyn Adams'), { id: 7, book: { title: 'Adams biography' } }];
      if (url.searchParams.get('term') === 'old search') {
        heldSearches.push({ route, data });
        return;
      }
      return route.fulfill({ json: data });
    }
    if (url.pathname === '/api/v1/library/search') {
      const term = url.searchParams.get('term');
      requests.push(term);
      if (hold) {
        pending.push({ route, term });
        return;
      }
      return route.fulfill({ json: responses[term] || {} });
    }
    if (url.pathname === '/') {
      const css = fs.readdirSync(path.join(output, 'Content')).find((name) => name.endsWith('.css'));
      return route.fulfill({ contentType: 'text/html', body: `<!doctype html><meta name="viewport" content="width=device-width,initial-scale=1"><link rel="stylesheet" href="/Content/${css}"><div id="root"></div><script>window.Chaptarr={urlBase:'',apiRoot:'/api/v1',isProduction:true};</script><script src="/fixture.js"></script>` });
    }
    const asset = path.resolve(output, `.${url.pathname}`);
    if (asset.startsWith(`${output}${path.sep}`) && fs.existsSync(asset) && fs.statSync(asset).isFile()) {
      return route.fulfill({ path: asset });
    }
    return route.fulfill({ status: 404, body: '' });
  });
  try {
    await page.goto('http://header-search.test/');
    await page.waitForFunction(() => window.fixtureReady);
    const checks = await page.evaluate(() => {
      const { createTextSearch, createBookSearchFilter, createExistingAuthorSelector, orderAuthorSearchResults } = window.searchSurface;
      const cases = [
        ['Bronte', 'Brontë', true], ['Soren', 'Søren', true], ['İstanbul', 'Istanbul', true], ['ı', 'I', true],
        ['Spider man', 'Spider-Man', true], ['Spider-man', 'Spider Man', true],
        ['Catch 22', 'Catch-22', true], ['Adams', 'Rachel Adams', true], ['Adams', 'Adam Smith', false],
        ['Adams', 'Adam Scott Huerta', false], ['It', 'Smith', false], ['Dune Messiah', 'Dune', false],
        ['Spider missing', 'Spider-Man', false], ['Renée', 'Rene\u0301e', true],
        ['東京', '東京物語', true], ['ポッター', 'ハリーポッターと賢者の石', true], ['王子', 'カスピアン王子のつのぶえ', true],
        ['カスピアン 王子', 'カスピアン王子のつのぶえ', true], ['แมว', 'นิทานแมว', true],
        ['otter', 'Potter', false], ['2', '12', false], ['Dune 王子', 'Dune カスピアン王子のつのぶえ', true],
        ['Dunex 王子', 'Dune カスピアン王子のつのぶえ', false], ['الحياة', 'كتاب الحياة', true], ['5.7', '5 7', false], ['---', 'Spider-Man', false]
      ];
      const failures = cases.filter(([term, value, expected]) => createTextSearch(term).matches(value) !== expected);
      const fields = [{ title: 'Spider-Man' }, { authorTitle: 'Spider-Man' }, { seriesTitle: 'Spider-Man' },
        { narrator: 'Spider-Man' }, { availableNarrators: ['Spider-Man'] }, { seriesLabels: ['Spider-Man'] },
        { editions: [{ title: 'Spider-Man' }] }, { editions: [{ subtitle: 'Spider-Man' }] }, { editions: [{ narrator: 'Spider-Man' }] },
        { seriesTitle: 'Visible header', seriesLabels: ['Visible header #1', 'Spider-Man #8'] }];
      const source = [{ id: 1, author: { authorName: 'Rachel Adamscott' } }, { id: 2, book: {} },
        { id: 3, author: { authorName: 'Rachel Louise Adams' } }, { id: 4, series: {} },
        { id: 5, author: { authorName: 'Rachel Adams' } }, { id: 6, author: { authorName: 'Rachel Lyn Adams' } }];
      const ordered = orderAuthorSearchResults(source, 'Rachel Adams');
      const findAuthor = createExistingAuthorSelector();
      const library = { authors: { items: [{ id: 20, foreignAuthorId: 'gr:100' }, { id: 21, foreignAuthorId: 'hc:200' }] } };
      const fallback = findAuthor(library, { id: 10, foreignAuthorId: 'gr:100' });
      const direct = findAuthor(library, { id: 21, foreignAuthorId: 'gr:100' });
      const absent = findAuthor(library, { id: 10, foreignAuthorId: 'gr:404' });
      return { failures, fields: fields.filter(createBookSearchFilter('spider man')).length,
        crossed: createBookSearchFilter('spider man')({ title: 'Spider', editions: [{ title: 'Man' }] }),
        group: createBookSearchFilter('spider man', 'Spider-Man')({ title: 'Another title' }),
        unemittedLinks: createBookSearchFilter('spider man')({ seriesLinks: [{ series: { title: 'Spider-Man' } }] }),
        malformed: createBookSearchFilter('spider man')({ availableNarrators: 'Spider-Man', editions: {}, seriesLinks: 'Spider-Man', seriesLabels: 'Spider-Man' }),
        ordered: ordered.map((item) => item.id), original: source.map((item) => item.id),
        references: ordered.every((item) => source.includes(item)),
        fallback: fallback?.id, direct: direct?.id, absent,
        exact: ['It Starts With Us', 'It', 'Iteration'].sort((a, b) => createTextSearch('It').score(b) - createTextSearch('It').score(a)) };
    });
    assert.deepEqual(checks, { failures: [], fields: 10, crossed: false, group: true, unemittedLinks: false, malformed: false,
      ordered: [5, 2, 3, 4, 6, 1], original: [1, 2, 3, 4, 5, 6], references: true,
      fallback: 20, direct: 21, absent: null,
      exact: ['It', 'It Starts With Us', 'Iteration'] });
    for (const provider of ['goodreads', 'hardcover', 'audible']) {
      await page.evaluate((selected) => window.runAddNewSearch({ term: 'Rachel Adams', provider: selected }), provider);
      await page.waitForFunction(() => window.readAddNewResults()?.[0]?.id === 3);
      assert.deepEqual(await page.evaluate(() => window.readAddNewResults().map((item) => item.id)), [3, 2, 1, 4, 6, 5, 7]);
      await page.evaluate(() => {
        window.searchActions = [];
      });
    }
    assert.deepEqual(addNewRequests, ['goodreads', 'hardcover', 'audible'].map((provider) => ({ term: 'Rachel Adams', provider })));
    const oldSearch = page.waitForRequest((request) => new URL(request.url()).searchParams.get('term') === 'old search');
    await page.evaluate(() => window.runAddNewSearch({ term: 'old search', provider: 'audible' }));
    await oldSearch;
    await page.evaluate(() => window.runAddNewSearch({ term: 'Rachel Adams', provider: 'goodreads' }));
    await page.waitForFunction(() => window.readAddNewResults()?.[0]?.id === 3);
    for (const { route, data } of heldSearches) {
      await route.fulfill({ json: data });
    }
    assert.deepEqual(await page.evaluate(() => window.readAddNewResults().map((item) => item.id)), [3, 2, 1, 4, 6, 5, 7]);
    console.log(`${theme}: shared word filters and unchanged Add New queries/book slots passed`);
    await page.evaluate((selected) => window.renderHeaderSearch(selected), theme);
    const input = page.locator('input[name="authorSearch"]');
    const options = page.locator('[role="option"]');
    const navigations = () => page.evaluate(() => window.headerNavigations);

    await input.fill('Hermione');
    await page.getByText('Pocket Potters: Harry', { exact: true }).waitFor();
    await page.getByText('Matched edition: Pocket Potters: Hermione', { exact: true }).waitFor();

    await input.fill('It');
    await page.getByText('It', { exact: true }).waitFor();
    assert.match(await options.first().innerText(), /^It\n/);
    assert.equal(await page.getByText(/^Matched edition:/).count(), 0);
    await input.press('Enter');
    assert.deepEqual(await navigations(), [['book', 1]]);

    await input.fill('Ann');
    await page.getByText('Ann', { exact: true }).waitFor();
    await input.press('Enter');
    assert.deepEqual((await navigations()).at(-1), ['author', 12]);

    await input.fill('It');
    await page.getByText('It', { exact: true }).waitFor();
    hold = true;
    const before = requests.length;
    await input.fill('It St');
    await input.press('Enter');
    assert.equal((await navigations()).length, 2, 'Enter during debounce must not navigate to an old result or Add New');
    assert.equal(await page.getByText('It', { exact: true }).count(), 0);
    await waitForPending('It St');
    assert.equal(requests.length, before + 1);
    await input.press('Enter');
    assert.equal((await navigations()).length, 2, 'Enter while the current request is pending must wait');
    await releasePending();
    await page.getByText('It Starts With Us', { exact: true }).waitFor();
    await page.getByText('It Starts With Us', { exact: true }).click();
    assert.deepEqual((await navigations()).at(-1), ['book', 2]);

    for (const action of ['Escape', 'blur', 'unmount']) {
      const requested = page.waitForRequest((request) => new URL(request.url()).searchParams.get('term') === 'It');
      await input.fill('It');
      await requested;
      await waitForPending('It');
      if (action === 'blur') {
        await input.evaluate((element) => element.blur());
      } else if (action === 'unmount') {
        await page.evaluate(() => window.unmountHeaderSearch());
      } else {
        await input.press('Escape');
      }
      await releasePending();
      assert.equal(await options.count(), 0, `${action} must discard the outstanding response`);
    }

    await page.evaluate((selected) => window.renderHeaderSearch(selected), theme);
    const first = page.waitForRequest((request) => new URL(request.url()).searchParams.get('term') === 'It');
    await input.fill('It');
    await first;
    await waitForPending('It');
    await input.fill('Ann');
    await releasePending();
    assert.equal(await page.getByText('It', { exact: true }).count(), 0, 'An older response during the new debounce must stay discarded');
    await waitForPending('Ann');
    await releasePending();
    await page.getByText('Ann', { exact: true }).waitFor();
    await input.press('Escape');
    hold = false;
    await input.fill('No existing item');
    await page.waitForResponse((response) => new URL(response.url()).searchParams.get('term') === 'No existing item');
    await page.waitForFunction(() => !document.querySelector('[class*="LoadingIndicator"]'));
    await input.press('Enter');
    assert.deepEqual((await navigations()).at(-1), ['add', 'No existing item', 'hardcover']);
    assert.deepEqual(errors, []);
    console.log(`${theme} ${width}px: exact order, narrowing, Enter/click, debounce, late response, Escape/blur/unmount and Add New passed`);
  } finally {
    await page.close();
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
    browser = await chromium.launch({ headless: true, args: ['--no-sandbox'] });
    for (const [theme, width] of [['dark', 1280], ['light', 390]]) {
      await verify(browser, theme, width);
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
