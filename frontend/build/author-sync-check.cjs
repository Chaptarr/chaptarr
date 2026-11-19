/* Real connected forms and production Redux actions; all HTTP is intercepted. */
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

module.exports = async(page, { importRequests, checkAddAuthorPayloads, selectAddAuthorMonitor, setAddAuthorCheckbox, strings, beforeInjectedFailure, expectHttpError }) => {
  const artifacts = process.env.CHAPTARR_LAYOUT_ARTIFACTS;
  const receipts = [];
  const checkbox = () => page.locator('input[name="syncMonitoredAcrossFormats"]');
  const add = () => page.locator('[class*="AddNewAuthorModalContent-addButton"]');
  const mediaButton = (name) => page.locator('[class*="MediaTypeToggle-toggleContainer"]').getByRole('button', { name });
  const render = async(mode, options = {}, family = 'sync') => {
    await page.evaluate((args) => window.renderAddAuthorFixture(...args), [mode, family, null, options]);
    await checkbox().waitFor({ state: 'attached' });
    await page.locator('select[name$="MetadataProfileId"] option[value="21"], select[name$="MetadataProfileId"] option[value="23"]').first().waitFor({ state: 'attached' });
  };
  const choose = (label, value) => selectAddAuthorMonitor(page, label, 0, value);
  const existing = (values = {}) => ({
    id: 10, foreignAuthorId: 'gr:fixture-selected-author', authorName: 'Selected Search Writer',
    path: '/fixture/audio/Selected Search Writer', rootFolderPath: '/fixture/audio',
    audiobookRootFolderPath: '/fixture/audio', ebookRootFolderPath: '/fixture/ebook',
    audiobookPath: '/fixture/audio/Selected Search Writer', ebookPath: '/fixture/ebook/Selected Search Writer',
    audiobookQualityProfileId: 11, ebookQualityProfileId: 13, audiobookMetadataProfileId: 21, ebookMetadataProfileId: 23,
    audiobookMonitored: false, ebookMonitored: true, audiobookMonitorNewItems: 'all', ebookMonitorNewItems: 'none',
    audiobookMonitorExisting: 0, audiobookMonitorFuture: false, ebookMonitorExisting: 2, ebookMonitorFuture: false,
    syncMonitoredAcrossFormats: true, tags: [41, 42], audiobookTags: [41], ebookTags: [42], images: [],
    ...values
  });
  const submit = async() => {
    const start = importRequests.length;
    await add().click();
    await page.waitForFunction(() => window.readAddAuthorFixtureState().isQueued && !window.readAddAuthorFixtureState().isAdding);
    assert.equal(importRequests.length - start, 1);
    return importRequests[start].body;
  };

  // Existing all-field payload coverage, now with one Both request.
  await checkAddAuthorPayloads(page, importRequests);
  receipts.push({ case: 'all-fields-unsynced', passed: true });

  for (const theme of ['light', 'dark']) {
    for (const width of [390, 1440]) {
      await page.setViewportSize({ width, height: 1000 });
      await render('both', { theme, roots: [{ defaultSyncMonitoredAcrossFormats: true }, { defaultSyncMonitoredAcrossFormats: true }] });
      assert.equal(await checkbox().isChecked(), true);
      assert.equal(await page.getByText(strings.InitialBookMonitoring, { exact: true }).count(), 1);
      assert.equal(await page.getByText(strings.MonitorNewBooks, { exact: true }).count(), 1);
      assert.equal(await add().isDisabled(), true, 'Differing inherited policies need a shared choice');
      await page.getByText(strings.AuthorSyncInitialMonitoringConflict, { exact: true }).waitFor();
      await choose(strings.InitialBookMonitoring, strings.BooksWithFiles);
      await choose(strings.MonitorNewBooks, strings.AllNewBooks);
      await setAddAuthorCheckbox(page, 'searchForMissingBooks', true);
      const body = await submit();
      assert.deepEqual(body, {
        foreignAuthorId: 'gr:fixture-selected-author', authorName: 'Selected Search Writer', mediaType: 'both',
        syncMonitoredAcrossFormats: true, manualFlag: true, searchForMissingBooks: true,
        audiobookRootFolderPath: '/fixture/audio', audiobookQualityProfileId: 11, audiobookMetadataProfileId: 21,
        audiobookTags: [41], audiobookMonitored: false, audiobookMonitorExistingMode: 'existing', audiobookMonitorNewItems: 'all',
        ebookRootFolderPath: '/fixture/ebook', ebookQualityProfileId: 13, ebookMetadataProfileId: 23,
        ebookTags: [42], ebookMonitored: true, ebookMonitorExistingMode: 'existing', ebookMonitorNewItems: 'all'
      });
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, 'No page horizontal overflow');
      if (artifacts) {
        fs.mkdirSync(artifacts, { recursive: true });
        await page.screenshot({ path: path.join(artifacts, `author-sync-${theme}-${width}.png`), fullPage: true });
      }
      receipts.push({ case: 'shared-both', theme, width, body });
    }
  }

  await render('both', { roots: [{ defaultSyncMonitoredAcrossFormats: true }, { defaultSyncMonitoredAcrossFormats: null }] });
  assert.equal(await checkbox().isChecked(), true, 'A null root default contributes no competing false policy');
  await render('both', { roots: [{ defaultSyncMonitoredAcrossFormats: null }, { defaultSyncMonitoredAcrossFormats: null }] });
  assert.equal(await checkbox().isChecked(), false, 'No explicit root sync default leaves sync off');
  assert.equal(await checkbox().evaluate((input) => input.indeterminate), false);

  await render('both', { roots: [{ defaultSyncMonitoredAcrossFormats: true }, { defaultSyncMonitoredAcrossFormats: false }] });
  assert.equal(await checkbox().evaluate((input) => input.indeterminate), true);
  assert.equal(await add().isDisabled(), true, 'Conflicting sync defaults are unresolved');
  await checkbox().focus();
  await page.keyboard.press('Space');
  assert.equal(await checkbox().isChecked(), true, 'Keyboard chooses sync explicitly');
  await setAddAuthorCheckbox(page, 'syncMonitoredAcrossFormats', false);
  await page.evaluate(() => window.updateAuthorSyncRoots([{ defaultSyncMonitoredAcrossFormats: true }, { defaultSyncMonitoredAcrossFormats: true }]));
  assert.equal(await checkbox().isChecked(), false, 'Explicit modal false survives a root-default change');
  assert.equal(await page.getByText(strings.InitialBookMonitoring, { exact: true }).count(), 2);
  await mediaButton(/Audiobooks/).click();
  assert.equal(await checkbox().isDisabled(), true);
  assert.equal(await checkbox().isChecked(), false);
  const sole = await submit();
  assert.equal('syncMonitoredAcrossFormats' in sole, false, 'An unavailable checkbox does not persist false');
  assert.equal('ebookRootFolderPath' in sole, false, 'Hidden preview root remains absent');
  assert.equal('ebookMonitorNewItems' in sole, false);
  receipts.push({ case: 'conflict-override-transition-to-sole', body: sole });

  for (const side of ['audiobook', 'ebook']) {
    await render(side, { roots: [{ defaultSyncMonitoredAcrossFormats: true }, { defaultSyncMonitoredAcrossFormats: true }] });
    assert.equal(await checkbox().isDisabled(), true);
    assert.equal(await checkbox().isChecked(), false);
    await mediaButton('Both').click();
    assert.equal(await checkbox().isDisabled(), false);
    assert.equal(await checkbox().isChecked(), true, 'Entering Both uses actual configured selected roots');
    await choose(strings.InitialBookMonitoring, strings.None);
    await choose(strings.MonitorNewBooks, strings.None);
    await setAddAuthorCheckbox(page, 'syncMonitoredAcrossFormats', false);
    assert.equal(await page.getByText(strings.None, { exact: true }).count() >= 4, true, 'Shared edits remain in both separate controls');
    await setAddAuthorCheckbox(page, 'syncMonitoredAcrossFormats', true);
    assert.equal(await add().isDisabled(), false);
  }

  await render('both', { roots: [{ defaultSyncMonitoredAcrossFormats: true }, { defaultSyncMonitoredAcrossFormats: true }] });
  await choose(strings.InitialBookMonitoring, strings.AllBooks);
  await choose(strings.MonitorNewBooks, strings.AllNewBooks);
  await setAddAuthorCheckbox(page, 'syncMonitoredAcrossFormats', false);
  await selectAddAuthorMonitor(page, strings.InitialBookMonitoring, 1, strings.None);
  await selectAddAuthorMonitor(page, strings.MonitorNewBooks, 1, strings.None);
  await setAddAuthorCheckbox(page, 'syncMonitoredAcrossFormats', true);
  assert.equal(await add().isDisabled(), true, 'Relinking cannot override newer separate choices with stale shared state');
  await choose(strings.InitialBookMonitoring, strings.BooksWithFiles);
  await choose(strings.MonitorNewBooks, strings.AllNewBooks);
  await page.locator('[class*="RootFolderSelectInputSelectedValue-path"]').filter({ hasText: '/fixture/audio' }).first().click();
  await page.locator('[class*="RootFolderSelectInputOption-value"]').filter({ hasText: '/fixture/audio-alt' }).click();
  assert.equal(await checkbox().isChecked(), true, 'An explicit sync choice survives a root selection');
  assert.equal(await add().isDisabled(), true, 'The selected root reloads its policy defaults and clears stale shared overrides');
  receipts.push({ case: 'root-default-reload-and-relink-conflicts', passed: true });

  // A missing side counts the saved other root, never the hidden preview root.
  const audioOnly = existing({ ebookRootFolderPath: null, ebookPath: null, ebookQualityProfileId: null, ebookMetadataProfileId: null });
  await render('ebook', { existingAuthor: audioOnly, roots: [{ defaultSyncMonitoredAcrossFormats: false }, { defaultSyncMonitoredAcrossFormats: false }] });
  assert.equal(await checkbox().isChecked(), true, 'An explicit saved true wins over root defaults');
  assert.equal(await checkbox().isDisabled(), false);
  assert.equal(await add().isDisabled(), true, 'Existing and requested ongoing policies require one shared choice');
  await choose(strings.MonitorNewBooks, strings.FutureReleases);
  const missing = await submit();
  assert.equal(missing.mediaType, 'ebook');
  assert.equal(missing.syncMonitoredAcrossFormats, true);
  assert.equal(missing.audiobookMonitorNewItems, 'new');
  assert.equal(missing.ebookMonitorNewItems, 'new');
  assert.equal(missing.ebookMonitorExistingMode, 'missing');
  assert.equal('audiobookMonitorExistingMode' in missing, false, 'No initial-selection policy is invented for the untouched side');
  assert.equal('audiobookRootFolderPath' in missing, false);
  receipts.push({ case: 'missing-side-saved-true', body: missing });
  await render('ebook', { existingAuthor: { ...audioOnly, syncMonitoredAcrossFormats: false }, roots: [{ defaultSyncMonitoredAcrossFormats: true }, { defaultSyncMonitoredAcrossFormats: true }] });
  assert.equal(await checkbox().isChecked(), false, 'An explicit saved false wins over true roots');
  const savedFalse = await submit();
  assert.equal(savedFalse.syncMonitoredAcrossFormats, false);
  await render('ebook', { existingAuthor: { ...audioOnly, syncMonitoredAcrossFormats: null }, roots: [{ defaultSyncMonitoredAcrossFormats: false }, { defaultSyncMonitoredAcrossFormats: true }] });
  assert.equal(await checkbox().evaluate((input) => input.indeterminate), true, 'Missing-side inheritance compares both resulting roots');

  // Exercise actual Edit PUT serialization and the distinct gate-toggle writer.
  const putRequests = [];
  await page.route('**/api/v1/author/10*', async(route) => {
    if (route.request().method() !== 'PUT') {
      return route.fallback();
    }
    const body = route.request().postDataJSON();
    putRequests.push(body);
    return route.fulfill({ json: { ...existing(), ...body } });
  });
  await render('audiobook', { existingAuthor: existing() }, 'edit-sync');
  await page.locator('select[name="audiobookQualityProfileId"] option[value="12"]').waitFor({ state: 'attached' });
  await page.locator('select[name="audiobookQualityProfileId"]').selectOption('12');
  await page.getByRole('button', { name: strings.Save, exact: true }).click();
  await page.waitForFunction(() => !window.readAuthorSyncState().isSaving);
  assert.equal(putRequests.length, 1);
  for (const field of ['syncMonitoredAcrossFormats', 'audiobookMonitorNewItems', 'ebookMonitorNewItems',
    'audiobookMonitorExisting', 'audiobookMonitorFuture', 'ebookMonitorExisting', 'ebookMonitorFuture']) {
    assert.equal(field in putRequests[0], false, 'Unrelated save preserves legacy policy state by omission');
  }
  await render('audiobook', { existingAuthor: existing() }, 'edit-sync');
  await choose(strings.MonitorNewBooks, strings.FutureReleases);
  const pending = await page.evaluate(() => window.readAuthorSyncState().pendingChanges);
  assert.equal(pending.audiobookMonitorNewItems, 'new');
  assert.equal(pending.ebookMonitorNewItems, 'new');
  await page.getByRole('button', { name: strings.Save, exact: true }).click();
  await page.waitForFunction(() => !window.readAuthorSyncState().isSaving);
  assert.equal(putRequests[1].audiobookMonitorNewItems, 'new');
  assert.equal(putRequests[1].ebookMonitorNewItems, 'new');
  await render('audiobook', { existingAuthor: existing({ syncMonitoredAcrossFormats: false }) }, 'edit-sync');
  await setAddAuthorCheckbox(page, 'syncMonitoredAcrossFormats', true);
  assert.equal(await page.getByRole('button', { name: strings.Save, exact: true }).isDisabled(), true);
  await choose(strings.MonitorNewBooks, strings.FutureReleases);
  assert.equal(await page.getByRole('button', { name: strings.Save, exact: true }).isDisabled(), false);
  await page.getByRole('button', { name: strings.Save, exact: true }).click();
  await page.waitForFunction(() => !window.readAuthorSyncState().isSaving);
  assert.equal(putRequests[2].syncMonitoredAcrossFormats, true);
  assert.equal(putRequests[2].audiobookMonitorNewItems, 'new');
  assert.equal(putRequests[2].ebookMonitorNewItems, 'new');
  await page.evaluate(() => window.toggleAuthorSyncGate('audiobook', true));
  await page.waitForFunction(() => !window.readAuthorSyncState().items[0].isSaving);
  for (const field of ['syncMonitoredAcrossFormats', 'audiobookMonitorNewItems', 'ebookMonitorNewItems',
    'audiobookMonitorExisting', 'audiobookMonitorFuture', 'ebookMonitorExisting', 'ebookMonitorFuture']) {
    assert.equal(field in putRequests[3], false, 'Gate toggle does not resubmit policy intent');
  }
  receipts.push({ case: 'edit-and-gate-payloads', putRequests });

  // A held request blocks duplicate submission; an error leaves the choices editable.
  beforeInjectedFailure();
  let releaseHeldRequest = null;
  const heldRequest = new Promise((resolve) => {
    releaseHeldRequest = resolve;
  });
  await page.route('**/api/v1/author/import', releaseHeldRequest);
  await render('audiobook');
  await add().click();
  assert.equal(await add().isDisabled(), true);
  await page.waitForFunction(() => window.readAddAuthorFixtureState().isAdding);
  const held = await heldRequest;
  expectHttpError(held.request().url(), 400);
  await held.fulfill({ status: 400, json: [{ propertyName: 'audiobookMonitor', errorMessage: 'Fixture import failure' }] });
  await page.waitForFunction(() => !window.readAddAuthorFixtureState().isAdding);
  assert.equal(await add().isDisabled(), false);
  assert.equal(await checkbox().isDisabled(), true);
  receipts.push({ case: 'loading-error-retry', passed: true });
  if (artifacts) {
    fs.writeFileSync(path.join(artifacts, 'author-sync-receipts.json'), JSON.stringify(receipts, null, 2));
  }
};
