import PropTypes from 'prop-types';
import { HTML5toTouch } from 'rdndmb-html5-to-touch';
import React, { useEffect, useRef, useState } from 'react';
import { DndProvider } from 'react-dnd-multi-backend';
import Alert from 'Components/Alert';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageToolbar from 'Components/Page/Toolbar/PageToolbar';
import PageToolbarButton from 'Components/Page/Toolbar/PageToolbarButton';
import PageToolbarSection from 'Components/Page/Toolbar/PageToolbarSection';
import { align, icons, kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import translateServerMessage from 'Utilities/String/translateServerMessage';
import QuickstartAudioBookShelfSection from './QuickstartAudioBookShelfSection';
import QuickstartCustomFormatsSection from './QuickstartCustomFormatsSection';
import QuickstartDownloadClientsSection from './QuickstartDownloadClientsSection';
import QuickstartGrimmorySection from './QuickstartGrimmorySection';
import QuickstartHardcoverSection from './QuickstartHardcoverSection';
import QuickstartMAMSection from './QuickstartMAMSection';
import QuickstartMatchingSection from './QuickstartMatchingSection';
import QuickstartMediaScopeSection from './QuickstartMediaScopeSection';
import QuickstartMetadataProfilesSection from './QuickstartMetadataProfilesSection';
import QuickstartProxySection from './QuickstartProxySection';
import QuickstartQualityProfilesSection from './QuickstartQualityProfilesSection';
import quickstartReadiness from './quickstartReadiness.cjs';
import QuickstartReviewSection from './QuickstartReviewSection';
import QuickstartRootFoldersSection from './QuickstartRootFoldersSection';
import QuickstartSection, { QuickstartSectionProvider, useQuickstartSections } from './QuickstartSection';
import QuickstartSettingsBackupSection from './QuickstartSettingsBackupSection';
import {
  getConfiguredMediaScope,
  getMediaTypesForScope,
  hasRootFolderForScope
} from './QuickstartSetupUtils';
import styles from './Quickstart.css';

const {
  getQuickstartConnectionVisibility,
  getQuickstartReadiness
} = quickstartReadiness;

const QUICKSTART_SECTION_KEYS = [
  'mediaScope',
  'rootFolders',
  'indexers',
  'downloadClients',
  'optionalConnections',
  'customFormats',
  'qualityProfiles',
  'metadataProfiles',
  'matching',
  'quickstartReview',
  'settingsBackup'
];

const GUIDED_QUICKSTART_SECTION_KEYS = QUICKSTART_SECTION_KEYS.filter((sectionKey) => sectionKey !== 'settingsBackup');
const GUIDED_REVIEW_SECTION_KEYS = [
  'mediaScope',
  'rootFolders',
  'indexers',
  'downloadClients',
  'optionalConnections',
  'customFormats',
  'qualityProfiles',
  'metadataProfiles',
  'matching'
];

const MEDIA_SCOPE_SESSION_KEY = 'quickstartMediaScope';

function getSessionMediaScope(installationId) {
  if (typeof window === 'undefined') {
    return null;
  }

  try {
    const savedScope = JSON.parse(window.sessionStorage.getItem(MEDIA_SCOPE_SESSION_KEY) || 'null');
    const isValidScope = ['audiobook', 'ebook', 'both'].includes(savedScope?.mediaScope);

    if (!isValidScope) {
      return null;
    }

    if (savedScope.installationId && installationId && savedScope.installationId !== installationId) {
      window.sessionStorage.removeItem(MEDIA_SCOPE_SESSION_KEY);
      return null;
    }

    if (savedScope.installationId && !installationId) {
      return null;
    }

    return savedScope.mediaScope;
  } catch (e) {
    return null;
  }
}

function saveSessionMediaScope(mediaScope, installationId) {
  if (typeof window === 'undefined' || !['audiobook', 'ebook', 'both'].includes(mediaScope)) {
    return;
  }

  try {
    const savedScope = JSON.parse(window.sessionStorage.getItem(MEDIA_SCOPE_SESSION_KEY) || 'null');

    // Wait for the server installation ID before replacing a scope that is
    // already associated with a specific installation.
    if (!installationId && savedScope?.installationId) {
      return;
    }

    window.sessionStorage.setItem(MEDIA_SCOPE_SESSION_KEY, JSON.stringify({
      installationId: installationId || null,
      mediaScope
    }));
  } catch (e) {
    // Session storage is optional; Quickstart still works without it.
  }
}

function QuickstartSectionToolbar(props) {
  const {
    showGuidance
  } = props;

  const {
    allExpanded,
    allCollapsed,
    setAllExpanded
  } = useQuickstartSections();

  let expandIcon = icons.EXPAND_INDETERMINATE;

  if (allExpanded) {
    expandIcon = icons.COLLAPSE;
  } else if (allCollapsed) {
    expandIcon = icons.EXPAND;
  }

  return (
    <PageToolbar>
      {
        showGuidance &&
          <PageToolbarSection
            alignContent={align.LEFT}
            collapseButtons={false}
          >
            <div className={styles.quickstartToolbarMessage}>
              {translate('QuickstartWorkTopToBottomHint')}
            </div>
          </PageToolbarSection>
      }

      <PageToolbarSection alignContent={align.RIGHT}>
        <PageToolbarButton
          label={allExpanded ? translate('AllExpandedCollapseAll') : translate('AllExpandedExpandAll')}
          iconName={expandIcon}
          onPress={() => setAllExpanded(!allExpanded)}
        />
      </PageToolbarSection>
    </PageToolbar>
  );
}

QuickstartSectionToolbar.propTypes = {
  showGuidance: PropTypes.bool.isRequired
};

function Quickstart(props) {
  const {
    hasActiveAudioBookShelf,
    audioBookShelfNotification,
    hasActiveGrimmory,
    grimmoryNotification,
    mamIndexer,
    indexersState,
    notificationsState,
    downloadClientsState,
    rootFoldersState,
    quickstartState,
    fetchIndexerSchema,
    selectIndexerSchema,
    fetchNotificationSchema,
    selectNotificationSchema,
    fetchDownloadClientSchema,
    selectDownloadClientSchema,
    markSectionInteracted,
    fetchNotifications,
    isHardcoverConfigured,
    hardcoverUsername,
    hardcoverAvatarUrl,
    proxies,
    isProxyConfigured
  } = props;

  const interactions = quickstartState?.interactions || {};
  const installationId = quickstartState?.installId || null;
  const isRootFoldersLoaded = rootFoldersState?.isPopulated === true;
  const isSetupStateLoaded = isRootFoldersLoaded &&
    indexersState?.isPopulated === true &&
    downloadClientsState?.isPopulated === true;
  const rootFolders = rootFoldersState?.items || [];
  const configuredMediaScope = getConfiguredMediaScope(rootFolders);
  const [mediaScope, setMediaScopeState] = useState(() =>
    getSessionMediaScope(installationId) || configuredMediaScope
  );
  const hasSelectedMediaScope = useRef(false);
  const [connectionResults, setConnectionResults] = useState({});

  useEffect(() => {
    const savedMediaScope = getSessionMediaScope(installationId);

    if (savedMediaScope && !hasSelectedMediaScope.current && savedMediaScope !== mediaScope) {
      setMediaScopeState(savedMediaScope);
      return;
    }

    if (!mediaScope && isRootFoldersLoaded && configuredMediaScope) {
      setMediaScopeState(configuredMediaScope);
      return;
    }

    saveSessionMediaScope(mediaScope, installationId);
  }, [configuredMediaScope, installationId, isRootFoldersLoaded, mediaScope]);

  const onMediaScopeChange = (newMediaScope) => {
    hasSelectedMediaScope.current = true;
    setMediaScopeState(newMediaScope);
    saveSessionMediaScope(newMediaScope, installationId);
  };

  const enabledIndexers = (indexersState?.items || []).filter((indexer) => indexer.enable);
  const enabledDownloadClients = (downloadClientsState?.items || []).filter((downloadClient) => downloadClient.enable);
  const hasRootFolders = hasRootFolderForScope(rootFolders, mediaScope);
  const selectedMediaTypes = getMediaTypesForScope(mediaScope);
  const guidedReadiness = getQuickstartReadiness({
    isSetupStateLoaded,
    hasRootFolder: hasRootFolders,
    hasEnabledIndexer: enabledIndexers.length > 0,
    hasEnabledDownloadClient: enabledDownloadClients.length > 0,
    mediaScope,
    interactions
  });
  const { showGuidance } = guidedReadiness;
  const connectionVisibility = getQuickstartConnectionVisibility({
    selectedMediaTypes,
    configuredMediaTypes: getMediaTypesForScope(configuredMediaScope),
    audioBookShelfConfigured: !!audioBookShelfNotification,
    grimmoryConfigured: !!grimmoryNotification,
    hardcoverConfigured: isHardcoverConfigured === true
  });

  useEffect(() => {
    if (showGuidance && !interactions.guidedSetup) {
      markSectionInteracted({ section: 'guidedSetup' });
    }
  }, [interactions.guidedSetup, markSectionInteracted, showGuidance]);
  const onConnectionTestSuccess = (name) => {
    setConnectionResults((results) => ({
      ...results,
      [name]: false
    }));
  };
  const onConnectionTestFailure = (name, error) => {
    const responseJson = error?.responseJSON;
    const responseMessage = Array.isArray(responseJson) ?
      responseJson.map((failure) => failure?.errorMessage || failure?.message).filter(Boolean).map(translateServerMessage).join(' ') :
      responseJson?.error || responseJson?.message;

    setConnectionResults((results) => ({
      ...results,
      [name]: translateServerMessage(responseMessage || error?.statusText || error?.message) || translate('QuickstartConnectionFailed')
    }));
  };

  const failedOptionalConnections = Object.entries(connectionResults)
    .filter(([, message]) => !!message);

  const guidedCompletedSectionKeys = guidedReadiness.guidedCompletedSectionKeys;

  return (
    <PageContent title={translate('Quickstart')}>
      <QuickstartSectionProvider
        sectionKeys={QUICKSTART_SECTION_KEYS}
        guidedSectionKeys={GUIDED_QUICKSTART_SECTION_KEYS}
        guidedInitialSectionKey="mediaScope"
        guidedReviewSectionKeys={GUIDED_REVIEW_SECTION_KEYS}
        guidedCompletedSectionKeys={guidedCompletedSectionKeys}
        isGuidedMode={showGuidance}
        onGuidedSectionReviewed={(sectionKey) => markSectionInteracted({ section: sectionKey })}
      >
        <QuickstartSectionToolbar showGuidance={showGuidance} />

        <PageContentBody>
          <DndProvider options={HTML5toTouch}>
            <QuickstartMediaScopeSection
              mediaScope={mediaScope}
              onMediaScopeChange={onMediaScopeChange}
            />

            <QuickstartRootFoldersSection
              mediaScope={mediaScope}
              markSectionInteracted={markSectionInteracted}
              quickstartState={quickstartState}
            />

            <QuickstartMAMSection
              mamIndexer={mamIndexer}
              indexersState={indexersState}
              fetchIndexerSchema={fetchIndexerSchema}
              selectIndexerSchema={selectIndexerSchema}
              markSectionInteracted={markSectionInteracted}
              proxies={proxies}
            />

            <QuickstartDownloadClientsSection
              downloadClientsState={downloadClientsState}
              fetchDownloadClientSchema={fetchDownloadClientSchema}
              selectDownloadClientSchema={selectDownloadClientSchema}
              markSectionInteracted={markSectionInteracted}
            />

            <QuickstartSection
              sectionKey="optionalConnections"
              title={translate('QuickstartOptionalConnectionsTitle')}
              isComplete={!!interactions.optionalConnections}
            >
              <div className={styles.optionalConnectionsRow}>
                {connectionVisibility.showAudioBookShelf && (
                  <div className={styles.optionalConnectionItem}>
                    <QuickstartAudioBookShelfSection
                      hasActiveAudioBookShelf={hasActiveAudioBookShelf}
                      audioBookShelfNotification={audioBookShelfNotification}
                      notificationsState={notificationsState}
                      fetchNotificationSchema={fetchNotificationSchema}
                      selectNotificationSchema={selectNotificationSchema}
                      markSectionInteracted={markSectionInteracted}
                      fetchNotifications={fetchNotifications}
                      wasSkipped={!!interactions.optionalConnections}
                      onConnectionTestSuccess={() => onConnectionTestSuccess('audioBookShelf')}
                      onConnectionTestFailure={(error) => onConnectionTestFailure('audioBookShelf', error)}
                      compact={true}
                    />
                  </div>
                )}

                {connectionVisibility.showGrimmory && (
                  <div className={styles.optionalConnectionItem}>
                    <QuickstartGrimmorySection
                      hasActiveGrimmory={hasActiveGrimmory}
                      grimmoryNotification={grimmoryNotification}
                      notificationsState={notificationsState}
                      fetchNotificationSchema={fetchNotificationSchema}
                      selectNotificationSchema={selectNotificationSchema}
                      markSectionInteracted={markSectionInteracted}
                      fetchNotifications={fetchNotifications}
                      wasSkipped={!!interactions.optionalConnections}
                      onConnectionTestSuccess={() => onConnectionTestSuccess('grimmory')}
                      onConnectionTestFailure={(error) => onConnectionTestFailure('grimmory', error)}
                      compact={true}
                    />
                  </div>
                )}

                {connectionVisibility.showHardcover && (
                  <div className={styles.optionalConnectionItem}>
                    <QuickstartHardcoverSection
                      compact={true}
                      isHardcoverConfigured={isHardcoverConfigured}
                      hardcoverUsername={hardcoverUsername}
                      hardcoverAvatarUrl={hardcoverAvatarUrl}
                      wasSkipped={!!interactions.optionalConnections}
                      markSectionInteracted={markSectionInteracted}
                      onConnectionTestSuccess={() => onConnectionTestSuccess('hardcover')}
                      onConnectionTestFailure={(error) => onConnectionTestFailure('hardcover', error)}
                    />
                  </div>
                )}

                <div className={styles.optionalConnectionItem}>
                  <QuickstartProxySection proxies={proxies} />
                </div>
              </div>
              {failedOptionalConnections.map(([connection, message]) => (
                <Alert
                  key={connection}
                  kind={kinds.DANGER}
                >
                  {translate('QuickstartConnectionFailureMessage', {
                    name: {
                      audioBookShelf: 'AudioBookShelf',
                      grimmory: 'Grimmory',
                      hardcover: 'Hardcover'
                    }[connection] || connection,
                    error: message
                  })}
                </Alert>
              ))}
            </QuickstartSection>

            <QuickstartCustomFormatsSection
              markSectionInteracted={markSectionInteracted}
              quickstartState={quickstartState}
            />

            <QuickstartSection
              sectionKey="qualityProfiles"
              title={translate('QuickstartQualityProfilesTitle')}
              isComplete={!!interactions.qualityProfiles}
            >
              <div className={styles.sectionDescription}>
                {translate('QuickstartQualityProfilesDescription')}
              </div>

              {selectedMediaTypes.length > 0 ? (
                <QuickstartQualityProfilesSection
                  mediaTypes={selectedMediaTypes}
                  markSectionInteracted={markSectionInteracted}
                />
              ) : (
                <div className={styles.sectionDescription}>
                  {translate('QuickstartSelectFormatsFirst')}
                </div>
              )}
            </QuickstartSection>

            <QuickstartSection
              sectionKey="metadataProfiles"
              title={translate('QuickstartMetadataProfilesTitle')}
              isComplete={!!interactions.metadataProfiles}
            >
              <div className={styles.sectionDescription}>
                {translate('QuickstartMetadataProfilesDescription')}
              </div>

              {selectedMediaTypes.length > 0 ? (
                <QuickstartMetadataProfilesSection
                  mediaTypes={selectedMediaTypes}
                  markSectionInteracted={markSectionInteracted}
                />
              ) : (
                <div className={styles.sectionDescription}>
                  {translate('QuickstartSelectFormatsFirst')}
                </div>
              )}
            </QuickstartSection>

            <QuickstartMatchingSection
              markSectionInteracted={markSectionInteracted}
              quickstartState={quickstartState}
            />

            <QuickstartReviewSection
              mediaScope={mediaScope}
              rootFolders={rootFolders}
              indexers={enabledIndexers}
              downloadClients={enabledDownloadClients}
              hasActiveAudioBookShelf={hasActiveAudioBookShelf}
              hasActiveGrimmory={hasActiveGrimmory}
              isHardcoverConfigured={isHardcoverConfigured}
              showAudioBookShelf={connectionVisibility.showAudioBookShelf}
              showGrimmory={connectionVisibility.showGrimmory}
              showHardcover={connectionVisibility.showHardcover}
              isProxyConfigured={isProxyConfigured}
              interactions={interactions}
              connectionResults={connectionResults}
              schemaErrors={{
                indexers: !!indexersState?.schemaError || !!indexersState?.saveError,
                downloadClients: !!downloadClientsState?.schemaError || !!downloadClientsState?.saveError,
                notifications: !!notificationsState?.schemaError
              }}
              isGuidedMode={showGuidance}
              onReviewComplete={() => markSectionInteracted({ section: 'quickstartReview' })}
            />

            <QuickstartSettingsBackupSection />
          </DndProvider>
        </PageContentBody>
      </QuickstartSectionProvider>
    </PageContent>
  );
}

Quickstart.propTypes = {
  hasActiveAudioBookShelf: PropTypes.bool,
  audioBookShelfNotification: PropTypes.object,
  hasActiveGrimmory: PropTypes.bool,
  grimmoryNotification: PropTypes.object,
  mamIndexer: PropTypes.object,
  indexersState: PropTypes.object.isRequired,
  notificationsState: PropTypes.object.isRequired,
  downloadClientsState: PropTypes.object.isRequired,
  rootFoldersState: PropTypes.object.isRequired,
  quickstartState: PropTypes.object,
  proxies: PropTypes.arrayOf(PropTypes.object),
  isProxyConfigured: PropTypes.bool,
  isHardcoverConfigured: PropTypes.bool,
  hardcoverUsername: PropTypes.string,
  hardcoverAvatarUrl: PropTypes.string,
  fetchIndexerSchema: PropTypes.func.isRequired,
  selectIndexerSchema: PropTypes.func.isRequired,
  fetchNotificationSchema: PropTypes.func.isRequired,
  selectNotificationSchema: PropTypes.func.isRequired,
  fetchDownloadClientSchema: PropTypes.func.isRequired,
  selectDownloadClientSchema: PropTypes.func.isRequired,
  markSectionInteracted: PropTypes.func.isRequired,
  fetchNotifications: PropTypes.func.isRequired
};

export default Quickstart;
