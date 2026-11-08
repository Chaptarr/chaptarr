const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

module.exports = async(page, { makeRelease, beginSearch, requestCount, artifacts, captureOnly = false }) => {
  const initialViewport = page.viewportSize();
  const receipts = [];
  if (artifacts) {
    fs.mkdirSync(artifacts, { recursive: true });
  }
  for (const mediaType of ['audiobook', 'ebook']) {
    const group = (bookId, title, releases = [], hiddenReleases = [], status = 'complete') => ({ bookId, title, mediaType, status, releases, hiddenReleases });
    const updateSearch = await beginSearch(mediaType, [
      group(701, 'Preferred and Alternatives', [
        makeRelease({ guid: 'table-preferred', title: 'A Long Preferred Release Title With Complete Edition Information', bookId: 701, rank: 1, preferred: true }),
        makeRelease({ guid: 'table-alternative', title: 'Alternative Release', bookId: 701, rank: 2 })
      ]),
      group(702, 'Hidden Only', [], [makeRelease({ guid: 'table-hidden', title: 'Hidden Release', bookId: 702, rank: 3 })]),
      group(703, 'Empty'),
      group(704, 'One Preferred', [makeRelease({ guid: 'table-single', title: 'Single Preferred Release', bookId: 704, rank: 1, preferred: true })]),
      group(705, 'Visible Only', [makeRelease({ guid: 'table-visible', title: 'Visible Release', bookId: 705, rank: 4 })]),
      group(706, 'Waiting', [], [], 'pending'),
      group(707, 'Failed', [], [], 'failed')
    ]);
    const summary = page.locator('table[class*="summaryTable"]');
    await summary.waitFor();
    const button = (title, expanded = false) => page.getByRole('button', { name: `${expanded ? 'Hide' : 'Show'} other releases for ${title}`, exact: true });
    const beforeRequests = requestCount();
    const state = { mediaType, collapsed: {}, layouts: [] };
    for (const title of ['Preferred and Alternatives', 'Hidden Only', 'Empty', 'One Preferred', 'Visible Only', 'Waiting', 'Failed']) {
      const control = button(title);
      state.collapsed[title] = await control.evaluate((element) => ({ disabled: element.disabled, opacity: getComputedStyle(element).opacity, disclosureColor: getComputedStyle(element.lastElementChild).color, expanded: element.getAttribute('aria-expanded') }));
      if (!captureOnly) {
        const empty = ['Empty', 'Waiting', 'Failed'].includes(title);
        assert.equal(state.collapsed[title].disabled, empty, `${mediaType}: ${title} Expand depends on visible OR hidden results`);
        if (empty) {
          assert.ok(Number(state.collapsed[title].opacity) < 1, 'The existing Button disabled treatment is visible');
          await control.click({ force: true });
          assert.equal(await control.getAttribute('aria-expanded'), 'false', 'Disabled disclosure cannot expand a blank row');
        }
      }
    }
    state.collapsed.badges = await summary.locator('[class*="preferredBadge"]').count();
    state.collapsed.headers = await summary.locator(':scope > thead th').allTextContents();
    if (!captureOnly) {
      assert.equal(state.collapsed.badges, 0, 'Collapsed preferred summaries leave the release title unbadged');
      assert.equal(state.collapsed.headers.includes('Narrator'), mediaType === 'audiobook');
      const count = mediaType === 'ebook' ? 6 : 7;
      assert.equal(state.collapsed.headers.length, count);
      assert.notEqual(state.collapsed.Empty.disclosureColor, state.collapsed['Hidden Only'].disclosureColor, 'Empty disclosure uses the existing disabled color');
      const titleGrid = await summary.locator('[class*="titleContent"]').first().evaluate((element) => ({ width: element.clientWidth, titleWidth: element.firstElementChild.getBoundingClientRect().width, tracks: getComputedStyle(element).gridTemplateColumns }));
      assert.ok(Math.abs(titleGrid.width - titleGrid.titleWidth) <= 1, 'Collapsed release title reclaims the entire badge track and gap');
      state.collapsed.titleGrid = titleGrid;
      for (const row of await summary.locator(':scope > tbody > tr').all()) {
        assert.equal(await row.locator(':scope > td').count(), count, 'All summary rows align with the selected media columns');
      }
    }
    for (const width of [1280, 390]) {
      await page.setViewportSize({ width, height: width === 390 ? 844 : 900 });
      if (artifacts) {
        await page.screenshot({ path: path.join(artifacts, `${mediaType}-collapsed-${width}.png`), fullPage: true });
      }
    }
    await page.setViewportSize({ width: 1280, height: 900 });
    await button('Preferred and Alternatives').focus();
    await page.keyboard.press('Enter');
    await page.getByText('Alternative Release', { exact: true }).waitFor();
    await button('Hidden Only').click();
    await page.getByRole('button', { name: 'Show 1 Hidden Results', exact: true }).click();
    await page.getByText('Hidden Release', { exact: true }).waitFor();
    await button('One Preferred').click();
    await page.getByText('No other releases returned', { exact: true }).waitFor();
    if (!captureOnly) {
      assert.equal(await summary.locator('[class*="preferredBadge"]').count(), 2, 'Each expanded preferred summary shows its existing badge');
      assert.equal(await page.getByText('Single Preferred Release', { exact: true }).count(), 1, 'Expansion never duplicates the preferred release');
      const expectedCells = mediaType === 'ebook' ? 13 : 14;
      for (const table of await summary.locator('td[class*="authorSearchExpandedCell"] table').all()) {
        assert.equal(await table.getByRole('columnheader', { name: 'Narrator', exact: true }).count(), mediaType === 'ebook' ? 0 : 1, 'Visible and hidden expanded tables share media columns');
        assert.equal(await table.locator(':scope > thead th').count(), expectedCells);
        assert.equal(await table.locator(':scope > tbody > tr > td').count(), expectedCells);
      }
      for (const cell of await summary.locator('td[class*="authorSearchExpandedCell"]').all()) {
        assert.equal(await cell.getAttribute('colspan'), mediaType === 'ebook' ? '6' : '7');
      }
      assert.equal(await summary.locator('td[class*="narrator"]').count(), mediaType === 'ebook' ? 0 : 4, 'No ebook narrator cells survive in summary, visible, or hidden tables');
      assert.equal(requestCount(), beforeRequests, 'Expanding groups and revealing hidden releases performs no search requests');
    }
    for (const width of [1280, 390]) {
      await page.setViewportSize({ width, height: width === 390 ? 844 : 900 });
      await page.evaluate(() => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve))));
      state.layouts.push(await summary.evaluate((table) => ({
        viewport: innerWidth,
        width: Math.round(table.getBoundingClientRect().width),
        scroller: { clientWidth: table.parentElement.clientWidth, scrollWidth: table.parentElement.scrollWidth },
        headers: [...table.querySelectorAll(':scope > thead th')].map((cell) => ({ title: cell.textContent, width: Math.round(cell.getBoundingClientRect().width) })),
        expanded: [...table.querySelectorAll('td[class*="authorSearchExpandedCell"] table')].map((nested) => ({
          width: Math.round(nested.getBoundingClientRect().width),
          headers: [...nested.querySelectorAll(':scope > thead th')].map((cell) => ({ title: cell.textContent, width: Math.round(cell.getBoundingClientRect().width) }))
        }))
      })));
      if (artifacts) {
        await page.screenshot({ path: path.join(artifacts, `${mediaType}-expanded-${width}.png`), fullPage: true });
      }
    }
    if (!captureOnly) {
      for (const layout of state.layouts) {
        assert.ok(layout.width >= (mediaType === 'ebook' ? 990 : 1150));
        for (const table of layout.expanded) {
          assert.ok(table.width >= (mediaType === 'ebook' ? 1042 : 1172));
          assert.ok(table.headers[0].width <= 60, 'Rank retains its existing narrow width');
          assert.ok(table.headers.at(-1).width <= 55, 'Download retains its width after narrator removal');
          assert.ok(table.headers.find((header) => header.title === 'Quality').width >= 110, 'Quality remains aligned after narrator removal');
        }
        if (layout.viewport === 390) {
          assert.ok(layout.scroller.scrollWidth > layout.scroller.clientWidth, 'The incumbent table scroller contains the mobile overflow');
        }
      }
      await button('Preferred and Alternatives', true).click();
      await button('One Preferred', true).click();
      assert.equal(await summary.locator('[class*="preferredBadge"]').count(), 0, 'Collapsing removes the badge again');
      await updateSearch([
        group(702, 'Hidden Only'), group(701, 'Preferred and Alternatives'),
        { ...group(708, 'All failed'), indexersQueried: 2, indexersFailed: 2 },
        { ...group(709, 'Partial failure'), indexersQueried: 3, indexersFailed: 1 },
        { ...group(710, 'No attempt'), indexersQueried: 0, indexersFailed: 0 }
      ]);
      await page.getByText('All indexers failed', { exact: true }).waitFor();
      await page.getByText('No results (1 of 3 indexers failed)', { exact: true }).waitFor();
      assert.ok(await page.getByText('No results returned', { exact: true }).count() >= 1);

      assert.equal(await button('Hidden Only').isDisabled(), true, 'A later empty snapshot disables the formerly expanded hidden group');
      assert.equal(await button('Hidden Only').getAttribute('aria-expanded'), 'false', 'A resultless group renders collapsed even when its saved expansion is true');
      assert.equal(await summary.locator('td[class*="authorSearchExpandedCell"]').count(), 0, 'No empty expanded row remains behind a disabled disclosure');
    }
    await updateSearch([], true);
    receipts.push(state);
  }
  if (artifacts) {
    fs.writeFileSync(path.join(artifacts, 'table-receipts.json'), JSON.stringify({ captureOnly, provenance: 'Constructed media-specific groups rendered through production components/store; all HTTP requests intercepted locally.', receipts }, null, 2));
  }
  console.log(`Author-search table receipts: ${JSON.stringify(receipts)}`);
  await page.setViewportSize(initialViewport);
};
