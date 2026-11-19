const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const webpack = require('webpack');
const { chromium } = require('playwright');
const createWebpackConfig = require('./webpack.config');

const root = path.resolve(__dirname, '../..');
const output = fs.mkdtempSync(path.join(os.tmpdir(), 'chaptarr-smart-narrator-ui-'));
const artifacts = process.env.CHAPTARR_LAYOUT_ARTIFACTS || fs.mkdtempSync(path.join(os.tmpdir(), 'chaptarr-smart-narrator-evidence-'));
const strings = JSON.parse(fs.readFileSync(path.join(root, 'src/NzbDrone.Core/Localization/Core/en.json'), 'utf8'));

async function compileFixture() {
  const config = createWebpackConfig({ production: true });
  config.entry = { fixture: path.join(__dirname, 'smart-narrator-fixture.js') };
  config.output = { ...config.output, path: output, filename: 'fixture.js', publicPath: '/' };
  config.optimization = { ...config.optimization, minimize: false, splitChunks: false, runtimeChunk: false };
  config.plugins = config.plugins.filter((plugin) => ['DefinePlugin', 'MiniCssExtractPlugin'].includes(plugin.constructor.name));
  for (const rule of config.module.rules) {
    if (Array.isArray(rule.use)) {
      rule.use = rule.use.filter((loader) => !String(typeof loader === 'string' ? loader : loader.loader).includes('css-modules-typescript-loader'));
    }
  }
  const compiler = webpack(config);
  await new Promise((resolve, reject) => compiler.run((error, stats) => {
    compiler.close(() => {});
    if (error || stats.hasErrors()) {
      reject(error || new Error(stats.toString({ all: false, errors: true })));
    } else {
      resolve();
    }
  }));
}

async function verify(browser, theme, width, css) {
  const page = await browser.newPage({ viewport: { width, height: 900 } });
  const errors = [];
  const previews = [];
  let failPreview = false;
  page.on('pageerror', (error) => errors.push(error.message));
  await page.route('**/*', async(route) => {
    const request = route.request();
    const url = new URL(request.url());
    if (url.hostname !== 'smart-narrator.test') {
      return route.abort();
    }
    if (url.pathname.endsWith('/localization')) {
      return route.fulfill({ json: { Strings: strings } });
    }
    if (url.pathname === '/api/v1/config/naming-pattern/preview') {
      const body = request.postDataJSON();
      previews.push(body);
      if (failPreview) {
        return route.fulfill({ status: 500, json: { error: 'Preview failed' } });
      }
      const examples = {
        '{Smart Narrator:AudioProductionType,FullCast,Narrators}': 'GraphicAudio',
        '{Smart Narrator:FullCast,AudioProductionType,Narrators}': 'Full Cast',
        '{Smart Narrator:AudioProductionType,Narrators}': 'GraphicAudio',
        '{Smart Narrator:Narrators}': 'Alice Reader, Bob Reader, Carol Reader',
        '{Smart Narrator:Narrators,GraphicAudio}': 'Alice Reader, Bob Reader, Carol Reader',
        '{Smart Narrator:GraphicAudio,Narrators}': 'GraphicAudio',
        '{Smart Narrator:AudioProductionType}': 'GraphicAudio',
        '{Smart Narrator:AudioProductionType,FirstNarrator}': 'GraphicAudio',
        '{Smart Narrator:FirstNarrator}': 'Alice Reader'
      };
      assert.ok(Object.hasOwn(examples, body.pattern), `Unexpected preview: ${body.pattern}`);
      return route.fulfill({ json: { path: examples[body.pattern] } });
    }
    if (url.pathname.startsWith('/api/')) {
      errors.push(`Unexpected API request ${url.pathname}`);
      return route.fulfill({ status: 404, json: {} });
    }
    if (url.pathname === '/') {
      return route.fulfill({ contentType: 'text/html; charset=utf-8', body: `<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><link rel="stylesheet" href="/Content/${path.basename(css)}"><div id="root"></div><div id="portal-root"></div><script>window.Chaptarr={apiRoot:"/api/v1",urlBase:"",isProduction:true};</script><script src="/fixture.js"></script>` });
    }
    const asset = path.resolve(output, `.${url.pathname}`);
    if (asset.startsWith(`${output}${path.sep}`) && fs.existsSync(asset)) {
      return route.fulfill({ path: asset });
    }
    return route.fulfill({ status: 204, body: '' });
  });

  await page.goto('http://smart-narrator.test/');
  await page.waitForFunction(() => window.fixtureReady);
  await page.evaluate((value) => window.renderNamingFixture(value), theme);
  await page.getByText('{Smart Narrator}', { exact: true }).click();
  await page.getByText('Example: GraphicAudio', { exact: true }).waitFor();
  const production = page.getByRole('button', { name: 'Production Label', exact: true });
  const cast = page.getByRole('button', { name: 'Full Cast (3+)', exact: true });
  await production.scrollIntoViewIfNeeded();
  const productionBox = await production.boundingBox();
  const castBox = await cast.boundingBox();
  await production.focus();
  await page.keyboard.press('Space');
  await page.waitForFunction(() => document.querySelector('[aria-label="Production Label"]').getAttribute('aria-pressed') === 'true');
  await page.waitForFunction(() => document.querySelector('[role="status"]').textContent.includes('over droppable area AudioProductionType'));
  // dnd-kit attaches the keyboard listener after activation and measures on the next frame.
  await page.evaluate(() => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  await page.keyboard.press(castBox.y > productionBox.y + productionBox.height / 2 ? 'ArrowDown' : 'ArrowRight');
  await page.waitForFunction(() => document.querySelector('[role="status"]').textContent.includes('over droppable area FullCast'));
  await page.keyboard.press('Space');
  await page.getByText('Example: Full Cast', { exact: true }).waitFor();
  await page.getByRole('button', { name: 'Apply', exact: true }).click();
  assert.equal(await page.evaluate(() => window.namingPattern), '{Book Title}{ - Smart Narrator:FullCast,AudioProductionType,Narrators}{ (PartNumber:smart)}');

  await page.getByText('{Smart Narrator}', { exact: true }).click();
  await page.getByText('Example: Full Cast', { exact: true }).waitFor();
  await page.getByRole('button', { name: 'Remove Full Cast (3+)', exact: true }).click();
  await page.getByText('Example: GraphicAudio', { exact: true }).waitFor();
  await page.getByRole('button', { name: 'Remove Production Label', exact: true }).click();
  await page.getByText('Example: Alice Reader, Bob Reader, Carol Reader', { exact: true }).waitFor();
  await page.locator('select[name="smartNarratorChoice"]').selectOption('GraphicAudio');
  await page.getByRole('button', { name: 'Add', exact: true }).click();
  await page.getByText('Example: Alice Reader, Bob Reader, Carol Reader', { exact: true }).waitFor();
  await page.getByRole('button', { name: 'Apply', exact: true }).click();
  assert.equal(await page.evaluate(() => window.namingPattern), '{Book Title}{ - Smart Narrator:Narrators,GraphicAudio}{ (PartNumber:smart)}');

  await page.getByText('{Smart Narrator}', { exact: true }).click();
  await page.getByText('Example: Alice Reader, Bob Reader, Carol Reader', { exact: true }).waitFor();
  const graphic = page.getByRole('button', { name: strings.GraphicAudio, exact: true });
  const names = page.getByRole('button', { name: 'All Narrators', exact: true });
  await graphic.scrollIntoViewIfNeeded();
  const graphicBox = await graphic.boundingBox();
  const namesBox = await names.boundingBox();
  await page.mouse.move(graphicBox.x + 10, graphicBox.y + graphicBox.height / 2);
  await page.mouse.down();
  await page.mouse.move(namesBox.x + namesBox.width / 2, namesBox.y + namesBox.height / 2, { steps: 10 });
  await page.waitForFunction(() => document.querySelector('[role="status"]').textContent.includes('over droppable area Narrators'));
  await page.mouse.up();
  await page.getByText('Example: GraphicAudio', { exact: true }).waitFor();
  const screenshot = path.join(artifacts, `smart-narrator-${theme}-${width}.png`);
  await page.screenshot({ path: screenshot, fullPage: true, animations: 'disabled' });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true, 'No horizontal page overflow');
  await page.getByRole('button', { name: `Remove ${strings.GraphicAudio}`, exact: true }).click();
  await page.getByRole('button', { name: 'Remove All Narrators', exact: true }).click();
  assert.equal(await page.getByRole('button', { name: 'Apply', exact: true }).isDisabled(), true);
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  assert.equal(await page.evaluate(() => window.namingPattern), '{Book Title}{ - Smart Narrator:Narrators,GraphicAudio}{ (PartNumber:smart)}');

  failPreview = true;
  await page.getByText('{Smart Narrator}', { exact: true }).click();
  await page.getByText(strings.NamingSmartPreviewFailed, { exact: true }).waitFor();
  assert.equal(await page.getByRole('button', { name: 'Apply', exact: true }).isDisabled(), true);
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  failPreview = false;
  await page.evaluate(() => window.setNamingPattern('{Book Title}{ (PartNumber:smart)} - '));
  await page.getByText('{Smart Narrator}', { exact: true }).click();
  await page.getByText('Example: GraphicAudio', { exact: true }).waitFor();
  await page.getByRole('button', { name: 'Apply', exact: true }).click();
  const insertedPattern = '{Book Title}{ (PartNumber:smart)} - {Smart Narrator:AudioProductionType,FullCast,Narrators}';
  assert.equal(await page.evaluate(() => window.namingPattern), insertedPattern);
  await page.getByRole('button', { name: 'Close', exact: true }).click();
  await page.evaluate(() => window.openNamingFixture());
  assert.equal(await page.locator('input[name="standardBookFormat"]').inputValue(), insertedPattern);
  await page.evaluate(() => window.setNamingPattern('{Book Title}{ - Smart Narrator:Narrators}/{Book Title}{ [SMART_NARRATOR]}'));
  await page.getByText('{Smart Narrator}', { exact: true }).click();
  await page.getByText('Example: Alice Reader, Bob Reader, Carol Reader', { exact: true }).waitFor();
  await page.getByRole('button', { name: 'Apply', exact: true }).click();
  assert.equal(await page.evaluate(() => window.namingPattern), '{Book Title}{ - Smart Narrator:Narrators}/{Book Title}{ [SMART_NARRATOR:Narrators]}');
  await page.evaluate(() => window.setNamingPattern('{Book Title}{ - Smart Narrator:AudioProductionType}'));
  await page.getByText('{Smart Narrator}', { exact: true }).click();
  await page.getByText('Example: GraphicAudio', { exact: true }).waitFor();
  await page.locator('select[name="smartNarratorChoice"]').selectOption('FirstNarrator');
  await page.getByRole('button', { name: 'Add', exact: true }).click();
  await page.getByText('Example: GraphicAudio', { exact: true }).waitFor();
  await page.getByRole('button', { name: 'Apply', exact: true }).click();
  const firstNarratorPattern = '{Book Title}{ - Smart Narrator:AudioProductionType,FirstNarrator}';
  assert.equal(await page.evaluate(() => window.namingPattern), firstNarratorPattern);
  await page.getByText('{Smart Narrator}', { exact: true }).click();
  await page.getByText('Example: GraphicAudio', { exact: true }).waitFor();
  await page.getByRole('button', { name: 'First Narrator', exact: true }).waitFor();
  await page.screenshot({ path: path.join(artifacts, `first-narrator-${theme}-${width}.png`), fullPage: true, animations: 'disabled' });
  await page.getByRole('button', { name: 'Remove Production Label', exact: true }).click();
  await page.getByText('Example: Alice Reader', { exact: true }).waitFor();
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  assert.equal(await page.evaluate(() => window.namingPattern), firstNarratorPattern);
  await page.evaluate(() => window.setNamingPattern('{Book Title}{ - SmartNarrator:Narrators}'));
  await page.getByText('{Smart Narrator}', { exact: true }).click();
  await page.getByText('Example: Alice Reader, Bob Reader, Carol Reader', { exact: true }).waitFor();
  await page.getByRole('button', { name: 'Apply', exact: true }).click();
  assert.equal(await page.evaluate(() => window.namingPattern), '{Book Title}{ - SmartNarrator:Narrators}');
  assert.deepEqual(previews[0].sample.narratorNames, ['Alice Reader', 'Bob Reader', 'Carol Reader']);
  await page.evaluate(() => window.setNamingPattern('{Book Title} - { (PartNumber:smart)}'));
  const patternInput = page.locator('input[name="standardBookFormat"]');
  await patternInput.focus();
  await patternInput.press('Home');
  for (let index = 0; index < '{Book Title}'.length; index++) {
    await patternInput.press('ArrowRight');
  }
  await page.getByText('{Author Name}', { exact: true }).click();
  await page.getByText('{Book Subtitle}', { exact: true }).click();
  assert.equal(await page.evaluate(() => window.namingPattern), '{Book Title}{Author Name}{Book Subtitle} - { (PartNumber:smart)}');
  await page.getByText('{Smart Narrator}', { exact: true }).click();
  await page.getByRole('button', { name: 'Apply', exact: true }).click();
  await page.getByText('{Book CleanTitle}', { exact: true }).click();
  assert.equal(await page.evaluate(() => window.namingPattern), '{Book Title}{Author Name}{Book Subtitle}{Smart Narrator:AudioProductionType,FullCast,Narrators}{Book CleanTitle} - { (PartNumber:smart)}');
  assert.deepEqual(errors, []);
  await page.close();
}

(async() => {
  let browser = null;
  try {
    fs.mkdirSync(artifacts, { recursive: true });
    await compileFixture();
    const css = path.join(output, 'Content', fs.readdirSync(path.join(output, 'Content')).find((name) => name.endsWith('.css')));
    browser = await chromium.launch();
    for (const theme of ['light', 'dark']) {
      for (const width of [1280, 390]) {
        try {
          await verify(browser, theme, width, css);
        } catch (error) {
          for (const page of browser.contexts().flatMap((context) => context.pages())) {
            await page.screenshot({ path: path.join(artifacts, `failure-${theme}-${width}.png`), fullPage: true });
            fs.writeFileSync(path.join(artifacts, `failure-${theme}-${width}.html`), await page.content());
          }
          console.error(`Failed ${theme}/${width}; evidence: ${artifacts}`);
          throw error;
        }
      }
    }
    console.log(`Smart Narrator browser checks passed. Screenshots: ${artifacts}`);
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
