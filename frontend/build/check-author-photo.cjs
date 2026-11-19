// CHAPTARR_PHOTO_RESPONSE points to the response emitted by the cover fixture in this run.
// Test output locations must not depend on the backend's target framework.
// Optional live observations use read-only saved JSON and mounted photo bytes; no requests reach the app.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const webpack = require('webpack');
const { chromium } = require('playwright');
const darkTheme = require('../src/Styles/Themes/dark');
const lightTheme = require('../src/Styles/Themes/light');

const themeVariables = { dark: darkTheme, light: lightTheme };

const root = path.resolve(__dirname, '../..');
assert(process.env.CHAPTARR_PHOTO_RESPONSE, 'Set CHAPTARR_PHOTO_RESPONSE for both the Core cover fixture and yarn check-author-photo.');
const response = JSON.parse(fs.readFileSync(process.env.CHAPTARR_PHOTO_RESPONSE));
const selectionResponses = JSON.parse(fs.readFileSync(`${process.env.CHAPTARR_PHOTO_RESPONSE}.selections.json`));
const output = fs.mkdtempSync(path.join(os.tmpdir(), 'chaptarr-photo-'));
console.log(`Photo evidence: ${output}`);
const config = require('./webpack.config')({ production: true });

config.entry = { fixture: path.join(__dirname, 'author-photo-fixture.js') };
config.output = { ...config.output, path: output, filename: 'fixture.js' };
config.optimization = { minimize: false };
config.plugins = config.plugins.filter((p) => ['DefinePlugin', 'MiniCssExtractPlugin'].includes(p.constructor.name));
for (const rule of config.module.rules) {
  if (Array.isArray(rule.use)) {
    rule.use = rule.use.filter((loader) => !String(typeof loader === 'string' ? loader : loader.loader).includes('css-modules-typescript-loader'));
  }
}
const placeholder = '/Content/Images/author-placeholder-muted.png';
const photo = '<svg xmlns="http://www.w3.org/2000/svg" width="200" height="250"><rect width="200" height="250" fill="#226688"/></svg>';
const good = response.images.find((image) => image.remoteUrl.endsWith('/fallback.jpg'));
const bad = response.images.find((image) => image.remoteUrl.endsWith('/failed.jpg'));
const alternate = { ...response.images.find((image) => image.remoteUrl.endsWith('/alternate.jpg')), url: '/MediaCover/12/poster-aaaaaaaaaaaaaaaa.jpg' };

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
    for (const theme of ['dark', 'light']) {
      for (const width of [412, 1440]) {
        const page = await browser.newPage({ viewport: { width, height: 850 }, hasTouch: width === 412 });
        const errors = [];
        const imageRequests = [];
        const selections = [];
        let heldImage = null;
        let heldLoad = null;
        let heldRepairLoad = null;
        let resolveHeldRepairLoad = null;
        let holdImage = false;
        let holdLoad = false;
        let holdRepairLoad = false;
        let failLoad = false;
        let failedImagePath = null;
        let unavailableRecoveryPath = null;
        let repairImageUrl = null;
        let repairLoadResult = null;
        let repairLoadStatus = 200;
        const imageLoadRequests = [];
        let cycleUpdates = false;
        let nextAuthorUpdate = null;
        const waitForHeldRepairLoad = () => new Promise((resolve) => {
          resolveHeldRepairLoad = resolve;
        });
        page.on('pageerror', (error) => errors.push(error.message));
        await page.route('**/*', (route) => {
          const pathname = new URL(route.request().url()).pathname;
          if (pathname === '/fixture.js') {
            return route.fulfill({ path: path.join(output, 'fixture.js'), contentType: 'text/javascript' });
          }
          if (pathname === '/style.css') {
            const css = fs.readdirSync(path.join(output, 'Content')).find((file) => file.endsWith('.css'));
            return route.fulfill({ path: path.join(output, 'Content', css), contentType: 'text/css' });
          }
          if (pathname.endsWith('/localization')) {
            return route.fulfill({ json: { Strings: JSON.parse(fs.readFileSync(path.join(root, 'src/NzbDrone.Core/Localization/Core/en.json'))) } });
          }
          if (pathname.endsWith('/loadImage')) {
            const imageUrl = route.request().postDataJSON().ImageUrl;
            imageLoadRequests.push(imageUrl);
            if (cycleUpdates) {
              const update = selectionResponses.find((author) => author.images.some((image) => image.remoteUrl === imageUrl && image.hash === author.selectedPosterHash));
              return route.fulfill({ json: { status: 'success', localPath: update.images.find((image) => image.hash === update.selectedPosterHash).url } });
            }
            if (imageUrl === repairImageUrl && holdRepairLoad) {
              heldRepairLoad = route;
              resolveHeldRepairLoad?.(route);
              resolveHeldRepairLoad = null;
              return;
            }
            if (imageUrl === repairImageUrl && repairLoadResult) {
              if (repairLoadResult.status === 'success') {
                failedImagePath = null;
              }
              return route.fulfill({ status: repairLoadStatus, json: repairLoadResult });
            }
            if (holdLoad) {
              heldLoad = route;
              return;
            }
            return route.fulfill({ json: failLoad ? { status: 'failed', errorCode: 'unavailable' } : { status: 'success', localPath: alternate.url } });
          }
          if (pathname.endsWith('/primaryPhoto')) {
            selections.push(route.request().postDataJSON());
            if (cycleUpdates) {
              nextAuthorUpdate = selectionResponses.find((author) => author.images.some((image) => image.remoteUrl === selections.at(-1).PhotoUrl && image.hash === author.selectedPosterHash));
            }
            return route.fulfill({ json: {} });
          }
          if (pathname.startsWith('/Content/Images/') || pathname.startsWith('/chaptarr/Content/Images/')) {
            return route.fulfill({ path: path.join(root, 'frontend/src', pathname.replace(/^\/chaptarr/, '')), contentType: pathname.endsWith('.svg') ? 'image/svg+xml' : 'image/png' });
          }
          if (pathname.startsWith('/MediaCover') || pathname.startsWith('/chaptarr/MediaCover/')) {
            imageRequests.push(pathname);
            // Author originals are removed after resizing; only the server's renditions exist.
            if (pathname.endsWith('/fanart.jpg') || pathname === failedImagePath || pathname === unavailableRecoveryPath || pathname.startsWith('/MediaCoverProxy/') || pathname.includes('/missing/')) {
              return route.fulfill({ status: 404, headers: { 'cache-control': 'no-store' }, body: '' });
            }
            if (holdImage) {
              heldImage = route;
              return;
            }
            if (process.env.CHAPTARR_PHOTO_MEDIA_ROOT) {
              const local = path.join(process.env.CHAPTARR_PHOTO_MEDIA_ROOT, pathname);
              if (fs.existsSync(local)) {
                return route.fulfill({ path: local, contentType: 'image/jpeg' });
              }
            }
            return route.fulfill({ contentType: 'image/svg+xml', body: photo });
          }
          if (pathname !== '/') {
            return route.fulfill({ status: 404, body: '' });
          }
          const variables = Object.entries(themeVariables[theme]).map(([key, value]) => `--${key}:${value}`).join(';');
          return route.fulfill({ contentType: 'text/html', body: `<!doctype html><meta name="viewport" content="width=device-width,initial-scale=1"><link rel="stylesheet" href="/style.css"><style>:root{${variables}}body{background:var(--pageBackground);color:var(--textColor)}#root{width:220px;margin:20px}</style><div id="root"></div><div id="portal-root"></div><script>window.Chaptarr={urlBase:'',apiRoot:'/api/v1',isProduction:true}</script><script src="/fixture.js"></script>` });
        });
        await page.goto('http://photo.test/');
        await page.evaluate(() => window.preparePhotoFixture());
        const show = (mode, props, remount = true) => page.evaluate(
          (args) => window.renderPhotoFixture(args.mode, args.props, args.remount),
          { mode, props, remount });
        const expectSource = async(src) => {
          await page.waitForFunction((expected) => [...document.querySelectorAll('#root img')].some((img) => img.getAttribute('src') === expected && img.complete && img.naturalWidth > 0), src);
        };
        const hoverPhoto = async() => {
          await page.mouse.move(width - 1, 849);
          await page.locator('#root img').hover();
        };
        const props = { ...response, authorId: response.id };
        // The details stylesheet hides its poster on phones; exercise that shared
        // carousel directly there, and the real details header on desktop.
        const showCarousel = (author, remount = true) => show(width > 1024 ? 'details' : 'poster',
          { ...author, authorId: author.id, showArrowsOnHover: true }, remount);
        const carouselPlaceholder = placeholder;

        // This list is serialized by the real backend mapper, not reordered by this test.
        assert(good.url.startsWith('/MediaCover/'));
        await show('tile', props);
        await expectSource(good.url);
        assert.equal(imageRequests.filter((p) => p.startsWith('/MediaCoverProxy/')).length, 0);
        await page.screenshot({ path: path.join(output, `${theme}-${width}-tile.png`) });

        // The real mapper supplies provider order and a local fallback at position two.
        await showCarousel(props);
        await expectSource(good.url);
        await hoverPhoto();
        assert.equal((await page.locator('[class*="photoCounter"]').innerText()).trim(), '2 / 3');
        await page.screenshot({ path: path.join(output, `${theme}-${width}-details-default.png`) });

        // Duplicate size variants must retain the local rendition without changing the photo's position.
        const remoteSmall = { ...good, url: '/MediaCoverProxy/small/fallback.jpg' };
        const remoteLarge = { ...good, remoteUrl: good.remoteUrl.replace('.jpg', '-500.jpg'), url: '/MediaCoverProxy/large/fallback-500.jpg' };
        const localLarge = { ...remoteLarge, url: '/MediaCover/12/poster-bbbbbbbbbbbbbbbb.jpg' };
        for (const [small, large, expected] of [
          [good, remoteLarge, good],
          [remoteSmall, localLarge, localLarge],
          [good, localLarge, localLarge],
          [remoteSmall, remoteLarge, remoteLarge]
        ]) {
          for (const variants of [[small, large], [large, small]]) {
            const requestsBefore = imageRequests.length;
            await showCarousel({ ...props, images: [bad, ...variants], selectedPosterHash: good.hash });
            await expectSource(expected === remoteLarge ? carouselPlaceholder : expected.url);
            await hoverPhoto();
            assert.equal((await page.locator('[class*="photoCounter"]').innerText()).trim(), '2 / 2');
            const expectedPath = new URL(expected.url, 'http://photo.test').pathname;
            // A remount can reuse the loaded image without issuing another request.
            assert(imageRequests.includes(expectedPath));
            assert(imageRequests.slice(requestsBefore).every((pathname) => pathname === expectedPath));
          }
        }
        for (const variants of [[good, remoteLarge], [remoteLarge, good]]) {
          const variantProps = { ...props, images: [bad, ...variants] };
          await showCarousel(variantProps);
          await expectSource(good.url);
          await hoverPhoto();
          assert.equal((await page.locator('[class*="photoCounter"]').innerText()).trim(), '2 / 2');
          await page.evaluate(({ id, key }) => localStorage.setItem(`authorPhotoChoice:${id}`, key), { id: props.id, key: remoteLarge.remoteUrl });
          await showCarousel(variantProps);
          await expectSource(good.url);
          await hoverPhoto();
          assert.equal((await page.locator('[class*="photoCounter"]').innerText()).trim(), '2 / 2');
          await page.evaluate(() => localStorage.clear());
        }
        await page.screenshot({ path: path.join(output, `${theme}-${width}-local-variant.png`) });

        cycleUpdates = true;
        const assertSelection = async(author, index) => {
          const selectedImage = author.images.find((image) => image.hash === author.selectedPosterHash);
          await expectSource(selectedImage.url);
          await hoverPhoto();
          assert.equal((await page.locator('[class*="photoCounter"]').innerText()).trim(), `${index + 1} / 3`);
          assert.equal(selectedImage.remoteUrl, response.images[index].remoteUrl, 'Photo identity must agree with its displayed position');
        };
        await showCarousel(selectionResponses[0]);
        await assertSelection(selectionResponses[0], 0);
        for (const direction of ['Next photo', 'Previous photo']) {
          for (const index of direction === 'Next photo' ? [1, 2, 0] : [2, 1, 0]) {
            const saved = page.waitForResponse((r) => r.url().endsWith('/primaryPhoto'));
            await page.getByTitle(direction, { exact: true }).click();
            await saved;
            assert(nextAuthorUpdate);
            // Match the author resource update after saving, without remounting the carousel.
            await showCarousel(nextAuthorUpdate, false);
            await assertSelection(nextAuthorUpdate, index);
          }
        }
        await page.screenshot({ path: path.join(output, `${theme}-${width}-details-cycle.png`) });
        cycleUpdates = false;
        await page.evaluate(() => localStorage.clear());

        const reduced = { ...selectionResponses[2], images: selectionResponses[2].images.filter((image) => image.remoteUrl !== response.images[0].remoteUrl) };
        await showCarousel(reduced, false);
        await expectSource(reduced.images.find((image) => image.hash === reduced.selectedPosterHash).url);
        await hoverPhoto();
        assert.equal((await page.locator('[class*="photoCounter"]').innerText()).trim(), '2 / 2', 'Removing a rejected candidate must update the total');

        const selected = { ...props, images: [bad, good, alternate], selectedPosterHash: good.hash };
        await show('poster', selected);
        await expectSource(good.url);
        await show('poster', { ...selected, images: [good, alternate, bad] }, false);
        await expectSource(good.url);
        await show('poster', { ...selected, images: [good, alternate], selectedPosterHash: alternate.hash }, false);
        await expectSource(alternate.url);

        await page.evaluate(({ id, key }) => localStorage.setItem(`authorPhotoChoice:${id}`, key), { id: props.id, key: good.remoteUrl });
        await show('poster', { ...selected, selectedPosterHash: alternate.hash });
        await expectSource(good.url);
        await show('poster', { ...selected, images: [alternate, good, bad], selectedPosterHash: alternate.hash }, false);
        await expectSource(good.url);
        await show('poster', { ...selected, images: [alternate], selectedPosterHash: alternate.hash }, false);
        await expectSource(alternate.url);
        await show('poster', { ...selected, images: [] });
        await expectSource(placeholder);
        await show('poster', selected, false);
        await expectSource(good.url);
        await page.evaluate(() => localStorage.clear());

        await show('tile', { ...props, images: [bad] });
        await expectSource(placeholder);
        await page.screenshot({ path: path.join(output, `${theme}-${width}-fallback.png`) });
        await show('tile', props, false);
        await expectSource(good.url);
        await show('poster', { ...props, images: [bad] });
        await expectSource(placeholder);
        await show('poster', props, false);
        await expectSource(good.url);

        holdImage = true;
        await show('poster', { ...props, images: [{ ...good, url: '/MediaCover/12/slow.jpg' }] });
        await page.waitForTimeout(100);
        assert(heldImage);
        assert.equal(await page.locator(`#root img[src="${placeholder}"]`).count(), 0);
        await heldImage.fulfill({ contentType: 'image/svg+xml', body: photo });
        holdImage = false;
        await expectSource('/MediaCover/12/slow-250.jpg');
        failLoad = true;
        const missingRepairResponse = page.waitForResponse((apiResponse) =>
          apiResponse.url().endsWith(`/author/${props.id}/loadImage`)
        );
        await show('poster', { ...props, images: [{ ...good, url: '/MediaCover/missing/poster.jpg' }] });
        await page.waitForTimeout(200);
        assert.equal(await page.locator(`#root img[src="${placeholder}"]`).count(), 0);
        await missingRepairResponse;
        await expectSource(placeholder);
        failLoad = false;
        await page.evaluate(() => {
          document.getElementById('root').style.marginTop = '1500px';
          scrollTo(0, 0);
        });
        await show('poster', { ...props, images: [good], lazy: true });
        assert.equal(await page.locator('#root img').count(), 0);
        await page.evaluate(() => {
          document.getElementById('root').style.marginTop = '20px';
        });

        const carousel = { ...props, images: [good, alternate, bad], isEditorActive: true };
        holdLoad = true;
        await show('poster', carousel);
        await expectSource(good.url);
        await page.getByTitle('Next photo', { exact: true }).focus();
        await page.keyboard.press('Enter');
        await page.waitForTimeout(100);
        assert(heldLoad);
        await show('poster', { ...carousel, images: [alternate, bad, good] }, false);
        const saved = page.waitForResponse((r) => r.url().endsWith('/primaryPhoto'));
        await heldLoad.fulfill({ json: { status: 'success', localPath: alternate.url } });
        holdLoad = false;
        await expectSource(alternate.url);
        await page.waitForFunction(() => localStorage.length > 0);
        await saved;
        assert.equal(selections.at(-1)?.PhotoUrl, alternate.remoteUrl);
        await show('poster', { ...carousel, images: [bad, good, alternate] });
        await expectSource(alternate.url);
        await page.evaluate(() => localStorage.clear());

        failLoad = true;
        await show('poster', carousel);
        await page.getByTitle('Next photo', { exact: true }).click();
        await page.waitForTimeout(150);
        await expectSource(good.url);
        failLoad = false;

        // A failed choice remains the same failed photo after a reorder, not the same index.
        await show('poster', { ...carousel, images: [alternate, good, bad] }, false);
        const savedNext = page.waitForResponse((r) => r.url().endsWith('/primaryPhoto'));
        await page.getByTitle('Previous photo', { exact: true }).click();
        await savedNext;
        assert.equal(selections.at(-1)?.PhotoUrl, bad.remoteUrl);
        await page.evaluate(() => localStorage.clear());

        holdLoad = true;
        heldLoad = null;
        await show('poster', carousel);
        await page.getByTitle('Next photo', { exact: true }).click();
        await page.waitForTimeout(100);
        assert(heldLoad);
        const savedCount = selections.length;
        await show('poster', { ...carousel, images: [good] }, false);
        await heldLoad.fulfill({ json: { status: 'success', localPath: alternate.url } });
        await page.waitForTimeout(150);
        await expectSource(good.url);
        assert.equal(selections.length, savedCount, 'removed choice must not be persisted by a late response');
        holdLoad = false;

        if (theme === 'dark' && width === 412) {
          await page.evaluate(() => localStorage.clear());
          await page.evaluate(() => {
            window.Chaptarr.urlBase = '/chaptarr';
          });
          const hashedPath = '/chaptarr/MediaCover/12/poster-cccccccccccccccc.jpg';
          const serverHashedPath = '/MediaCover/12/poster-cccccccccccccccc.jpg';
          unavailableRecoveryPath = serverHashedPath;
          const hashedPhoto = { ...good, url: hashedPath };
          const recoveryProps = {
            ...props,
            images: [hashedPhoto, alternate],
            selectedPosterHash: good.hash,
            isEditorActive: true
          };
          const repairRequest = () => page.waitForRequest((request) =>
            request.url().endsWith(`/author/${props.id}/loadImage`) &&
            request.method() === 'POST' &&
            request.postDataJSON().ImageUrl === good.remoteUrl
          );
          const hashedRequestCount = () => imageRequests.filter((url) => url === hashedPath).length;

          repairImageUrl = good.remoteUrl;
          repairLoadResult = { status: 'success', localPath: serverHashedPath };
          repairLoadStatus = 200;
          failedImagePath = hashedPath;
          holdRepairLoad = true;
          heldRepairLoad = null;
          const beforeLoadRequests = imageLoadRequests.length;
          const beforeSelections = selections.length;
          const beforeImageErrors = await page.evaluate(() => window.photoImageErrorCount || 0);
          const heldRepairPending = waitForHeldRepairLoad();
          const repairRequestPending = repairRequest();
          await show('poster', recoveryProps);
          const repair = await repairRequestPending;
          await heldRepairPending;

          assert.equal(repair.postDataJSON().ImageUrl, good.remoteUrl);
          assert(heldRepairLoad);
          assert.equal(hashedRequestCount(), 4, 'the selected hashed rendition must exhaust its existing retries first');
          assert.equal(imageLoadRequests.length - beforeLoadRequests, 1);
          await expectSource(`/chaptarr${placeholder}`);
          assert.equal(await page.evaluate(() => window.photoImageErrorCount), beforeImageErrors + 1);

          holdRepairLoad = false;
          failedImagePath = null;
          await heldRepairLoad.fulfill({ json: repairLoadResult });
          heldRepairLoad = null;
          await page.waitForFunction((expectedPath) => [...document.querySelectorAll('#root img')].some((img) =>
            img.getAttribute('src')?.startsWith(`${expectedPath}?v=`) && img.complete && img.naturalWidth > 0
          ), hashedPath);
          await hoverPhoto();
          assert.equal((await page.locator('[class*="photoCounter"]').innerText()).trim(), '1 / 2');
          assert.equal(selections.length, beforeSelections, 'recovery must not persist a photo selection');
          assert.equal(await page.evaluate((id) => localStorage.getItem(`authorPhotoChoice:${id}`), props.id), null);
          await page.screenshot({ path: path.join(output, 'dark-412-author-recovery.png') });

          const runFailedRepair = async(result, statusCode) => {
            repairLoadResult = result;
            repairLoadStatus = statusCode;
            failedImagePath = hashedPath;
            const loadRequestsBefore = imageLoadRequests.length;
            const failedImageRequestsBefore = hashedRequestCount();
            const requestPending = repairRequest();
            const responsePending = page.waitForResponse((apiResponse) =>
              apiResponse.url().endsWith(`/author/${props.id}/loadImage`)
            );
            await show('poster', recoveryProps);
            await requestPending;
            await responsePending;
            await expectSource(`/chaptarr${placeholder}`);
            await page.waitForTimeout(150);
            assert.equal(imageLoadRequests.length - loadRequestsBefore, 1, 'a failed or pending recovery must not loop');
            assert.equal(hashedRequestCount() - failedImageRequestsBefore, 4);
            assert.equal(selections.length, beforeSelections, 'failed recovery must not persist a photo selection');
            assert.equal(await page.evaluate((id) => localStorage.getItem(`authorPhotoChoice:${id}`), props.id), null);
          };

          await runFailedRepair({ status: 'error', errorCode: 'unavailable' }, 502);
          await runFailedRepair({ status: 'pending' }, 200);

          repairLoadResult = { status: 'success', localPath: serverHashedPath };
          repairLoadStatus = 200;
          failedImagePath = hashedPath;
          holdRepairLoad = true;
          heldRepairLoad = null;
          const beforeStaleRequests = hashedRequestCount();
          const beforeStaleSelections = selections.length;
          const staleHeldPending = waitForHeldRepairLoad();
          const staleRepairPending = repairRequest();
          await show('poster', recoveryProps);
          await staleRepairPending;
          await staleHeldPending;
          assert(heldRepairLoad);
          assert.equal(hashedRequestCount() - beforeStaleRequests, 4);

          const staleSelectionPath = '/MediaCover/12/poster-dddddddddddddddd.jpg';
          const staleSelectionExpectedPath = `/chaptarr${staleSelectionPath}`;
          const staleSelectionPhoto = { ...alternate, url: staleSelectionExpectedPath };
          await show('poster', {
            ...recoveryProps,
            images: [hashedPhoto, staleSelectionPhoto],
            selectedPosterHash: alternate.hash
          }, false);
          await expectSource(staleSelectionExpectedPath);
          holdRepairLoad = false;
          failedImagePath = null;
          const staleRepairResponse = page.waitForResponse((apiResponse) =>
            apiResponse.url().endsWith(`/author/${props.id}/loadImage`)
          );
          const stalePreloadResponse = page.waitForResponse((apiResponse) =>
            new URL(apiResponse.url()).pathname === hashedPath && apiResponse.request().resourceType() === 'image'
          );
          await heldRepairLoad.fulfill({ json: repairLoadResult });
          heldRepairLoad = null;
          await staleRepairResponse;
          await stalePreloadResponse;
          await page.waitForFunction(() => !document.querySelector('#root [title="Loading image..."]'));
          await expectSource(staleSelectionExpectedPath);
          await hoverPhoto();
          assert.equal((await page.locator('[class*="photoCounter"]').innerText()).trim(), '2 / 2');
          assert.equal(selections.length, beforeStaleSelections, 'resource selection changes must not persist a user preference');
          assert.equal(await page.evaluate((id) => localStorage.getItem(`authorPhotoChoice:${id}`), props.id), null);

          await page.evaluate(() => localStorage.clear());
          repairImageUrl = good.remoteUrl;
          repairLoadResult = { status: 'success', localPath: serverHashedPath };
          repairLoadStatus = 200;
          failedImagePath = hashedPath;
          holdRepairLoad = true;
          heldRepairLoad = null;
          const beforeUnmountRequests = hashedRequestCount();
          const beforeUnmountSelections = selections.length;
          const unmountHeldPending = waitForHeldRepairLoad();
          const unmountRepairPending = repairRequest();
          await show('poster', recoveryProps);
          await unmountRepairPending;
          await unmountHeldPending;
          assert(heldRepairLoad);
          await show('poster', props);
          await expectSource(good.url);
          holdRepairLoad = false;
          failedImagePath = null;
          await heldRepairLoad.fulfill({ json: repairLoadResult });
          heldRepairLoad = null;
          await page.waitForTimeout(100);
          assert.equal(hashedRequestCount() - beforeUnmountRequests, 4, 'unmounted recovery must not reload stale bytes');
          assert.equal(selections.length, beforeUnmountSelections);
          assert.equal(await page.evaluate((id) => localStorage.getItem(`authorPhotoChoice:${id}`), props.id), null);
          await page.evaluate(() => {
            window.Chaptarr.urlBase = '';
          });
          unavailableRecoveryPath = null;
        }

        await show('banner', { images: [{ ...bad, coverType: 'banner' }] });
        await expectSource('/Content/Images/chaptarr-logo.svg');
        for (const urlBase of ['', '/chaptarr']) {
          await page.evaluate((base) => {
            window.Chaptarr.urlBase = base;
          }, urlBase);
          for (const mode of ['poster', 'tile', ...(width > 1024 ? ['details'] : [])]) {
            await show(mode, { ...props, images: [], placeholder: undefined });
            await expectSource(`${urlBase}${placeholder}`);
            await show(mode, { ...props, images: [bad], placeholder: undefined });
            await expectSource(`${urlBase}${placeholder}`);
          }
          await page.screenshot({ path: path.join(output, `${theme}-${width}-placeholder${urlBase ? '-url-base' : ''}.png`) });
          await show('banner', { images: [] });
          await expectSource(`${urlBase}/Content/Images/chaptarr-logo.svg`);
          await show('poster', { ...props, images: [bad, { ...good, url: `${urlBase}${good.url}` }], isEditorActive: true });
          await expectSource(`${urlBase}${good.url}`);
          assert.equal((await page.locator('[class*="photoCounter"]').innerText()).trim(), '2 / 2');
          const local = { ...good, url: `${urlBase}/MediaCover/12/banner-70.jpg`, coverType: 'banner' };
          await show('banner', { images: [{ ...bad, coverType: 'banner' }, local] });
          await expectSource(local.url);
          const fanart = [{ ...bad, coverType: 'fanart' }, { ...local, url: `${urlBase}/MediaCover/12/fanart-180.jpg`, coverType: 'fanart' }];
          const expectedFanartUrl = `${urlBase}/MediaCover/12/fanart-360.jpg`;
          for (const mode of ['details', 'bookDetails']) {
            await show(mode, { ...props, title: 'Example Book', titleSlug: 'example', mediaType: 'ebook', images: fanart, author: { ...props, images: fanart } });
            assert.equal(await page.locator('[class*="backdrop"]').first().evaluate((element) => element.style.backgroundImage), `url("${expectedFanartUrl}")`);
            assert.equal(await page.evaluate((url) => new Promise((resolve) => {
              const image = new Image();
              image.onload = () => resolve(true);
              image.onerror = () => resolve(false);
              image.src = url;
            }), expectedFanartUrl), true, 'Header backdrop must load the rendition the server keeps');
            assert(imageRequests.includes(expectedFanartUrl));
          }
        }
        await page.evaluate(() => {
          window.Chaptarr.urlBase = '';
        });
        // Books retain their first-cover behavior, even when a later cover is local.
        await show('book', { images: [{ ...bad, coverType: 'cover' }, { ...good, coverType: 'cover' }] });
        await page.waitForFunction(() => document.querySelector('#root img')?.getAttribute('src').startsWith('data:image/png;'));
        await show('book', { images: [{ ...bad, coverType: 'cover' }] });
        await page.waitForFunction(() => document.querySelector('#root img')?.getAttribute('src').startsWith('data:image/png;'));
        await page.evaluate(() => {
          document.getElementById('root').style.marginTop = '1500px';
          scrollTo(0, 0);
        });
        await show('book', { images: [{ ...good, coverType: 'cover' }], lazy: true });
        assert((await page.locator('#root img').getAttribute('src')).startsWith('data:image/png;'));
        await page.evaluate(() => {
          document.getElementById('root').style.marginTop = '20px';
        });

        if (process.env.CHAPTARR_PHOTO_LIVE_JSON) {
          const authors = JSON.parse(fs.readFileSync(process.env.CHAPTARR_PHOTO_LIVE_JSON));
          for (const author of authors) {
            const images = author.images;
            await show('tile', { ...author, authorName: author.name, images });
            const selectedImage = images.find((image) => image.coverType === 'poster' && image.url.includes('/MediaCover/'));
            await expectSource(selectedImage?.url || placeholder);
            await page.screenshot({ path: path.join(output, `${theme}-${width}-live-${author.id}.png`) });
          }
        }
        assert.deepEqual(errors, []);
        console.log(`${theme} ${width}px: backend response, tile/details, forward/reverse save-update cycles, photo/counter agreement, loading/retry/failure, book/banner/backdrop checks passed`);
        await page.close();
      }
    }
  } finally {
    await browser.close();
  }
})().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
