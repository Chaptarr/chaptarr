/* Exercise shared dialogs and settings navigation in Chromium. */
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const webpack = require('webpack');
const { chromium } = require('playwright');
const lightTheme = require('../src/Styles/Themes/light');
const darkTheme = require('../src/Styles/Themes/dark');
const translations = require('../../src/NzbDrone.Core/Localization/Core/en.json');

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
    compiler.close((closeError) => {
      if (error || closeError || stats.hasErrors()) {
        reject(error || closeError || new Error(stats.toString({ all: false, errors: true })));
      } else {
        resolve();
      }
    });
  }));
}

async function openFixture(browser, scenario, width, theme) {
  const page = await browser.newPage({ viewport: { width, height: 900 } });
  page.setDefaultTimeout(5000);
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
    if (pathname === '/api/v1/localization') {
      return route.fulfill({ json: { Strings: translations } });
    }
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

  await page.goto(`http://confirm-modal.test/?scenario=${scenario}`);
  await page.evaluate((colors) => {
    for (const [key, value] of Object.entries(colors)) {
      document.documentElement.style.setProperty(`--${key}`, value);
    }
  }, theme === 'dark' ? darkTheme : lightTheme);
  try {
    await page.waitForFunction(() => window.fixtureReady === true);
  } catch (error) {
    await page.close();
    throw new Error(`${scenario}: ${errors.join('; ')}; ${error.message}`);
  }
  return { page, errors };
}

async function pressFocused(page, target, key = 'Enter') {
  await target.focus();
  assert.equal(await target.evaluate((element) => document.activeElement === element), true);
  await page.keyboard.press(key);
}

async function assertCalls(page, expected) {
  assert.deepEqual(await page.evaluate(() => window.calls), {
    confirm: 0, cancel: 0, link: 0, nested: 0, unmount: 0, ...expected
  });
}

async function assertFooterFits(page) {
  const problems = await page.locator('#portal-root [class*="ModalFooter-modalFooter"]').evaluate((footer) => {
    const errors = [];
    const bounds = footer.getBoundingClientRect();
    const buttons = Array.from(footer.querySelectorAll('a, button'));
    const boxes = buttons.map((button) => button.getBoundingClientRect());
    buttons.forEach((button, index) => {
      const box = boxes[index];
      const label = button.textContent.trim();
      if (box.left < bounds.left || box.right > bounds.right || box.bottom > innerHeight || box.top < 0) {
        errors.push(`Outside footer or viewport: ${label}`);
      }
      const text = document.createTreeWalker(button, NodeFilter.SHOW_TEXT);
      while (text.nextNode()) {
        if (text.currentNode.textContent.trim() && getComputedStyle(text.currentNode.parentElement).visibility !== 'hidden') {
          const range = document.createRange();
          range.selectNodeContents(text.currentNode);
          if (Array.from(range.getClientRects()).some((rect) => rect.left < box.left || rect.right > box.right || rect.top < box.top || rect.bottom > box.bottom)) {
            errors.push(`Clipped caption: ${label}`);
          }
        }
      }
      if (boxes.slice(index + 1).some((other) => box.left < other.right && box.right > other.left && box.top < other.bottom && box.bottom > other.top)) {
        errors.push(`Overlapping actions: ${label}`);
      }
    });
    return errors;
  });
  assert.deepEqual(problems, [], 'Footer captions must remain readable and actions must not overlap');
}

(async() => {
  let browser = null;
  const receipts = [];
  try {
    await compileFixture();
    browser = await chromium.launch();

    for (const theme of ['light', 'dark']) {
      for (const width of [320, 390, 1440]) {
        const check = async(name, scenario, run) => {
          if (process.env.CHAPTARR_DIALOG_SCENARIO && !scenario.startsWith(process.env.CHAPTARR_DIALOG_SCENARIO)) {
            return;
          }
          const { page, errors } = await openFixture(browser, scenario, width, theme);
          try {
            await run(page);
            assert.deepEqual(errors, [], 'No browser errors');
            receipts.push({ name, width, theme, passed: true });
          } catch (error) {
            receipts.push({ name, width, theme, passed: false, error: error.message });
          } finally {
            await page.close();
          }
        };

        await check('Send Logs stays disabled with its translated explanation', 'send-logs', async(page) => {
          const button = page.getByRole('button', { name: translations.UnmappedFilesSendLogsButton, exact: true });
          if (!await button.isVisible()) {
            await page.getByRole('button', { name: /^More\b/ }).click();
          }
          await button.waitFor();
          assert.equal(await button.isDisabled(), true);
          assert.equal(await button.getAttribute('title'), translations.UnmappedFilesSendLogsDisabled);
          await button.hover({ force: true });
          await button.evaluate((element) => element.click());
          const nativeButton = page.getByTestId('disabled-button');
          const disabledColor = await nativeButton.evaluate((element) => getComputedStyle(element).color);
          assert.equal(await nativeButton.evaluate((element) => getComputedStyle(element).pointerEvents), 'none');
          assert.equal(await nativeButton.evaluate((element) => getComputedStyle(element).color), disabledColor);
          await nativeButton.evaluate((element) => element.click());
          assert.equal(await page.getByTestId('disabled-link').evaluate((element) => getComputedStyle(element).pointerEvents), 'none');
          assert.deepEqual(await page.evaluate(() => window.logUploadCalls), []);
        });

        const retryToolbar = (page) => page.locator(`[aria-label^="${translations.UnmappedFilesRetryMatch}"]`).first();
        const chooseAudio = (page) => page.getByRole('button', { name: /Audiobooks/ }).click();
        const calls = (page) => page.evaluate(() => window.retryCalls);
        await check('Retry six books confirms; cancel and Escape do nothing', 'unmapped-retry', async(page) => {
          await retryToolbar(page).click();
          await page.getByText('This will retry matching for 7 books. Continue?').waitFor();
          await page.keyboard.press('Escape');
          await chooseAudio(page);
          await retryToolbar(page).click();
          assert.equal(await page.getByText('This will retry matching for 6 books. Continue?').isVisible(), true);
          assert.deepEqual(await calls(page), []);
          await assertFooterFits(page);
          if (process.env.CHAPTARR_DIALOG_ARTIFACTS) {
            fs.mkdirSync(process.env.CHAPTARR_DIALOG_ARTIFACTS, { recursive: true });
            await page.screenshot({ path: path.join(process.env.CHAPTARR_DIALOG_ARTIFACTS, `${theme}-${width}-retry.png`) });
          }
          await pressFocused(page, page.getByRole('button', { name: translations.Cancel, exact: true }));
          assert.deepEqual(await calls(page), []);
          await retryToolbar(page).click();
          await page.keyboard.press('Escape');
          assert.deepEqual(await calls(page), []);
          await retryToolbar(page).click();
          await page.locator('#portal-root').getByRole('button', { name: translations.UnmappedFilesRetryMatch, exact: true }).click();
          assert.deepEqual(await calls(page), [{ mediaType: 'audiobook', selection: { scope: 'all' } }]);
        });
        for (const [groups, parts] of [[5, 1], [1, 10]]) {
          await check(`Retry ${groups} books / ${groups * parts} files runs immediately`, `unmapped-retry&groups=${groups}&parts=${parts}`, async(page) => {
            await chooseAudio(page);
            await retryToolbar(page).click();
            assert.deepEqual(await calls(page), [{ mediaType: 'audiobook', selection: { scope: 'all' } }]);
            assert.equal(await page.locator('#portal-root button').count(), 0);
          });
        }
        await check('Selected book groups, select-all exclusions and media filter determine retry count', 'unmapped-retry', async(page) => {
          await chooseAudio(page);
          for (let id = 1; id <= 5; id++) {
            await page.evaluate((fileId) => window.unmappedTable.onSelectedChange({ id: fileId, value: true }), id);
          }
          await retryToolbar(page).click();
          assert.deepEqual(await calls(page), [{ mediaType: 'audiobook', selection: { scope: 'selected', bookFileIds: [1, 2, 3, 4, 5] } }]);
          await page.evaluate(() => window.unmappedTable.onSelectedChange({ id: 6, value: true }));
          await retryToolbar(page).click();
          await page.getByText('This will retry matching for 6 books. Continue?').waitFor();
          await page.locator('#portal-root').getByRole('button', { name: translations.UnmappedFilesRetryMatch, exact: true }).click();
          assert.equal((await calls(page)).length, 2);
          assert.deepEqual((await calls(page))[1].selection.bookFileIds, [1, 2, 3, 4, 5, 6]);
          await page.evaluate(() => window.unmappedTable.onSelectAllChange({ value: true }));
          await page.evaluate(() => window.unmappedTable.onSelectedChange({ id: 6, value: false }));
          await retryToolbar(page).click();
          assert.deepEqual((await calls(page))[2], { mediaType: 'audiobook', selection: { scope: 'all', exceptBookFileIds: [6] } });
          await page.getByRole('button', { name: /eBooks/ }).click();
          await retryToolbar(page).click();
          assert.deepEqual((await calls(page))[3], { mediaType: 'ebook', selection: { scope: 'all' } });
        });

        await check('Initial focus confirms once', 'confirm', async(page) => {
          const button = page.getByRole('button', { name: 'Confirm', exact: true });
          assert.equal(await button.evaluate((el) => el === document.activeElement), true);
          await page.keyboard.press('Enter');
          await assertCalls(page, { confirm: 1 });
          await page.keyboard.press('Enter');
          await assertCalls(page, { confirm: 1 });
        });
        for (const key of ['Enter', 'Space']) {
          await check(`Cancel owns ${key}`, 'confirm', async(page) => {
            await pressFocused(page, page.getByRole('button', { name: 'Cancel', exact: true }), key);
            await assertCalls(page, { cancel: 1 });
          });
        }
        await check('IME, repeats, modifiers and handled Enter do not confirm', 'confirm', async(page) => {
          assert.deepEqual(await page.evaluate(() => window.fixtureShortcuts.ACCEPT_CONFIRM_MODAL), {
            key: 'Enter', name: 'Accept Confirmation Modal'
          });
          const content = page.getByTestId('modal-content');
          for (const option of ['isComposing', 'repeat', 'ctrlKey', 'altKey', 'metaKey', 'shiftKey']) {
            await content.dispatchEvent('keydown', { key: 'Enter', code: 'Enter', keyCode: 13, [option]: true });
            await assertCalls(page, {});
          }
          await content.evaluate((element) => element.addEventListener('keydown', (event) => event.preventDefault(), { once: true }));
          await content.dispatchEvent('keydown', { key: 'Enter', code: 'Enter', keyCode: 13 });
          await assertCalls(page, {});
          await pressFocused(page, content);
          await assertCalls(page, { confirm: 1 });
        });
        await check('Content Enter confirms once', 'confirm', async(page) => {
          await pressFocused(page, page.getByTestId('modal-content'));
          await assertCalls(page, { confirm: 1 });
        });
        await check('Link Enter only follows link', 'confirm', async(page) => {
          await pressFocused(page, page.getByRole('link', { name: 'Read details' }));
          await assertCalls(page, { link: 1 });
        });
        await check('Busy confirm cannot be repeated', 'busy', async(page) => {
          const button = page.locator('button').filter({ hasText: 'Confirm' });
          await pressFocused(page, button);
          assert.equal(await button.isDisabled(), true);
          await page.keyboard.press('Enter');
          await pressFocused(page, page.getByTestId('modal-content'));
          await page.keyboard.press('Enter');
          await assertCalls(page, { confirm: 1 });
        });
        await check('Escape uses Cancel', 'confirm', async(page) => {
          await page.keyboard.press('Escape');
          await assertCalls(page, { cancel: 1 });
        });
        await check('Close button uses Cancel', 'confirm', async(page) => {
          await page.locator('#portal-root button').first().click();
          await assertCalls(page, { cancel: 1 });
        });
        if (width === 1440) {
          await check('Backdrop uses Cancel', 'confirm', async(page) => {
            await page.locator('#portal-root [class*="modalBackdrop"]').click({ position: { x: 5, y: 5 } });
            await assertCalls(page, { cancel: 1 });
          });
        }
        await check('Input Enter never confirms', 'inputs', async(page) => {
          for (const selector of ['input[name=text]', 'textarea', 'select', '[contenteditable=true]']) {
            await pressFocused(page, page.locator(selector));
          }
          assert.match(await page.locator('textarea').inputValue(), /\n/);
          await assertCalls(page, {});
        });
        await check('Only top dialog handles Enter and Escape', 'nested', async(page) => {
          await page.getByRole('button', { name: 'Open child' }).click();
          await pressFocused(page, page.getByTestId('child-content'));
          await assertCalls(page, { nested: 1 });
          await page.getByRole('button', { name: 'Open child' }).click();
          await page.keyboard.press('Escape');
          await assertCalls(page, { nested: 1 });
          await page.getByRole('button', { name: 'Confirm', exact: true }).waitFor();
          await page.keyboard.press('Escape');
          await assertCalls(page, { nested: 1, cancel: 1 });
        });
        await check('Reopening does not retain shortcuts', 'reopen', async(page) => {
          for (let i = 0; i < 3; i++) {
            await page.getByRole('button', { name: 'Open dialog' }).click();
            await page.keyboard.press('Enter');
          }
          await assertCalls(page, { confirm: 3 });
        });
        for (const action of ['Enter', 'Space', 'click', 'Escape', 'discard']) {
          await check(`Unsaved settings: ${action}`, 'settings', async(page) => {
            await page.locator('input[name=setting]').fill('Edited');
            await page.getByRole('link', { name: 'Leave settings' }).click();
            const stay = page.getByRole('button', { name: translations.PendingChangesStayReview });
            await stay.waitFor();
            if (action === 'click' && process.env.CHAPTARR_DIALOG_ARTIFACTS) {
              fs.mkdirSync(process.env.CHAPTARR_DIALOG_ARTIFACTS, { recursive: true });
              await page.screenshot({ path: path.join(process.env.CHAPTARR_DIALOG_ARTIFACTS, `${theme}-${width}-pending.png`) });
            }
            if (action === 'click') {
              await assertFooterFits(page);
            }
            if (action === 'discard') {
              await pressFocused(page, page.getByRole('button', { name: translations.PendingChangesDiscardLeave }));
            } else if (action === 'Escape') {
              await page.keyboard.press('Escape');
            } else if (action === 'click') {
              await stay.click();
            } else {
              await pressFocused(page, stay, action);
            }
            assert.equal(await page.evaluate(() => window.fixtureHistory.location.pathname), action === 'discard' ? '/other' : '/settings/general');
            await assertCalls(page, { unmount: action === 'discard' ? 1 : 0 });
            if (action !== 'discard') {
              assert.equal(await page.locator('input[name=setting]').inputValue(), 'Edited');
            }
          });
        }
        await check('Overwide caption wraps and busy action stays disabled', 'long-label', async(page) => {
          await assertFooterFits(page);
          if (process.env.CHAPTARR_DIALOG_ARTIFACTS) {
            fs.mkdirSync(process.env.CHAPTARR_DIALOG_ARTIFACTS, { recursive: true });
            await page.screenshot({ path: path.join(process.env.CHAPTARR_DIALOG_ARTIFACTS, `${theme}-${width}-long-label.png`) });
          }
          await page.keyboard.press('Enter');
          const button = page.locator('#portal-root button').last();
          assert.equal(await button.isDisabled(), true);
          await page.keyboard.press('Enter');
          await assertCalls(page, { confirm: 1 });
          await assertFooterFits(page);
        });
        await check('Grouped import footer keeps both action groups', 'grouped-footer', async(page) => {
          await assertFooterFits(page);
          const groups = await page.locator('#portal-root [class*="ModalFooter-modalFooter"] > div').evaluateAll((elements) => elements.map((element) => {
            const box = element.getBoundingClientRect();
            return { top: box.top, width: box.width };
          }));
          assert.equal(groups.length, 2);
          assert.equal(groups[0].top, groups[1].top, 'Existing half-width groups stay beside each other');
          assert.equal(groups[0].width, groups[1].width);
          const actionGap = await page.locator('#portal-root [class*="ModalFooter-modalFooter"] > div').last().locator('button').evaluateAll((buttons) => {
            const [cancel, confirm] = buttons.map((button) => button.getBoundingClientRect());
            return confirm.top > cancel.top ? confirm.top - cancel.bottom : confirm.left - cancel.right;
          });
          assert.equal(actionGap, width < 768 ? 5 : 10, 'Grouped buttons retain their existing mobile/desktop spacing');
        });
        await check('Layout and tab stay inside the dialog', 'confirm', async(page) => {
          await assertFooterFits(page);
          for (let i = 0; i < 8; i++) {
            await page.keyboard.press(i < 4 ? 'Tab' : 'Shift+Tab');
            assert.equal(await page.evaluate(() => document.getElementById('portal-root').contains(document.activeElement)), true);
          }
          const bounds = await page.getByTestId('modal-content').evaluate((el) => {
            const r = el.getBoundingClientRect();
            return { left: r.left, right: r.right, viewport: innerWidth };
          });
          assert(bounds.left >= 0 && bounds.right <= bounds.viewport);
          if (process.env.CHAPTARR_DIALOG_ARTIFACTS) {
            fs.mkdirSync(process.env.CHAPTARR_DIALOG_ARTIFACTS, { recursive: true });
            await page.screenshot({ path: path.join(process.env.CHAPTARR_DIALOG_ARTIFACTS, `${theme}-${width}-confirm.png`) });
          }
        });
      }
    }

    console.log(JSON.stringify(receipts, null, 2));
    assert.equal(receipts.filter((item) => !item.passed).length, 0, 'All dialog browser checks must pass');
    console.log(`${receipts.length} dialog browser checks passed.`);
  } finally {
    if (process.env.CHAPTARR_DIALOG_ARTIFACTS) {
      fs.mkdirSync(process.env.CHAPTARR_DIALOG_ARTIFACTS, { recursive: true });
      fs.writeFileSync(path.join(process.env.CHAPTARR_DIALOG_ARTIFACTS, 'receipts.json'), `${JSON.stringify(receipts, null, 2)}\n`);
    }
    if (browser) {
      await browser.close();
    }
    fs.rmSync(output, { recursive: true, force: true });
  }
})().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
