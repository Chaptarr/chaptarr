const test = require('node:test');
const assert = require('node:assert/strict');
const {
  getQuickstartConnectionVisibility,
  getQuickstartReadiness
} = require('./quickstartReadiness.cjs');

test('waits for root, indexer, and client data before deciding guided readiness', () => {
  const readiness = getQuickstartReadiness({
    isSetupStateLoaded: false,
    hasRootFolder: false,
    hasEnabledIndexer: false,
    hasEnabledDownloadClient: false
  });

  assert.equal(readiness.showGuidance, false);
});

test('skips configured core sections and opens the first missing provider', () => {
  const readiness = getQuickstartReadiness({
    isSetupStateLoaded: true,
    hasRootFolder: true,
    hasEnabledIndexer: true,
    hasEnabledDownloadClient: false,
    mediaScope: 'ebook'
  });

  assert.equal(readiness.showGuidance, true);
  assert.deepEqual(readiness.guidedCompletedSectionKeys, ['mediaScope', 'rootFolders', 'indexers']);
});

test('keeps an unconfigured root as the next core section after providers are reviewed', () => {
  const readiness = getQuickstartReadiness({
    isSetupStateLoaded: true,
    hasRootFolder: false,
    hasEnabledIndexer: false,
    hasEnabledDownloadClient: false,
    mediaScope: 'both',
    interactions: { indexers: true, downloadClients: true }
  });

  assert.equal(readiness.showGuidance, true);
  assert.equal(readiness.coreSetupReviewed, false);
  assert.deepEqual(readiness.guidedCompletedSectionKeys, ['mediaScope', 'indexers', 'downloadClients']);
});

test('allows Review to finish when missing core items were deliberately skipped', () => {
  const readiness = getQuickstartReadiness({
    isSetupStateLoaded: true,
    hasRootFolder: false,
    hasEnabledIndexer: false,
    hasEnabledDownloadClient: false,
    mediaScope: 'ebook',
    interactions: {
      rootFolders: true,
      indexers: true,
      downloadClients: true,
      guidedSetup: true,
      quickstartReview: false
    }
  });

  assert.equal(readiness.coreSetupReviewed, true);
  assert.equal(readiness.showGuidance, true);

  const finished = getQuickstartReadiness({
    isSetupStateLoaded: true,
    hasRootFolder: false,
    hasEnabledIndexer: false,
    hasEnabledDownloadClient: false,
    mediaScope: 'ebook',
    interactions: {
      rootFolders: true,
      indexers: true,
      downloadClients: true,
      guidedSetup: true,
      quickstartReview: true
    }
  });

  assert.equal(finished.showGuidance, false);
});

test('keeps an existing optional connection visible when its format is not selected', () => {
  const visibility = getQuickstartConnectionVisibility({
    selectedMediaTypes: ['ebook'],
    audioBookShelfConfigured: true
  });

  assert.equal(visibility.showAudioBookShelf, true);
  assert.equal(visibility.showGrimmory, true);
  assert.equal(visibility.showHardcover, true);
});

test('shows connections for formats already present in saved roots', () => {
  const visibility = getQuickstartConnectionVisibility({
    selectedMediaTypes: ['ebook'],
    configuredMediaTypes: ['audiobook']
  });

  assert.equal(visibility.showAudioBookShelf, true);
  assert.equal(visibility.showGrimmory, true);
  assert.equal(visibility.showHardcover, true);
});

test('shows optional connections for their selected formats', () => {
  const audioOnly = getQuickstartConnectionVisibility({
    selectedMediaTypes: ['audiobook']
  });
  const ebookOnly = getQuickstartConnectionVisibility({
    selectedMediaTypes: ['ebook']
  });

  assert.equal(audioOnly.showAudioBookShelf, true);
  assert.equal(ebookOnly.showAudioBookShelf, false);
  assert.equal(ebookOnly.showGrimmory, true);
  assert.equal(ebookOnly.showHardcover, true);
});
