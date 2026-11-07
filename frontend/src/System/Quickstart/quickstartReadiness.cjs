function hasReviewedCoreSetup({ hasRootFolder, hasEnabledIndexer, hasEnabledDownloadClient, interactions }) {
  return !!((hasRootFolder || interactions.rootFolders) &&
    (hasEnabledIndexer || interactions.indexers) &&
    (hasEnabledDownloadClient || interactions.downloadClients));
}

function getQuickstartReadiness({
  isSetupStateLoaded,
  hasRootFolder,
  hasEnabledIndexer,
  hasEnabledDownloadClient,
  mediaScope,
  interactions = {}
}) {
  const coreSetupReviewed = hasReviewedCoreSetup({
    hasRootFolder,
    hasEnabledIndexer,
    hasEnabledDownloadClient,
    interactions
  });
  const guidedCompletedSectionKeys = [
    (mediaScope || interactions.mediaScope) && 'mediaScope',
    (hasRootFolder || interactions.rootFolders) && 'rootFolders',
    (hasEnabledIndexer || interactions.indexers) && 'indexers',
    (hasEnabledDownloadClient || interactions.downloadClients) && 'downloadClients',
    interactions.optionalConnections && 'optionalConnections',
    interactions.customFormats && 'customFormats',
    interactions.qualityProfiles && 'qualityProfiles',
    interactions.metadataProfiles && 'metadataProfiles',
    interactions.matching && 'matching',
    interactions.quickstartReview && 'quickstartReview'
  ].filter(Boolean);

  return {
    coreSetupReviewed,
    showGuidance: !!isSetupStateLoaded && (
      !coreSetupReviewed ||
      (interactions.guidedSetup && !interactions.quickstartReview)
    ),
    guidedCompletedSectionKeys
  };
}

function getQuickstartConnectionVisibility({
  selectedMediaTypes = [],
  configuredMediaTypes = [],
  audioBookShelfConfigured = false,
  grimmoryConfigured = false,
  hardcoverConfigured = false
}) {
  const hasAudiobookWorkflow = selectedMediaTypes.includes('audiobook') ||
    configuredMediaTypes.includes('audiobook');
  const hasBookWorkflow = selectedMediaTypes.length > 0 || configuredMediaTypes.length > 0;

  return {
    showAudioBookShelf: hasAudiobookWorkflow || audioBookShelfConfigured,
    showGrimmory: hasBookWorkflow || grimmoryConfigured,
    showHardcover: hasBookWorkflow || hardcoverConfigured
  };
}

module.exports = {
  getQuickstartConnectionVisibility,
  getQuickstartReadiness
};
