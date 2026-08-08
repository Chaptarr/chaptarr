import PropTypes from 'prop-types';
import React from 'react';
import { DndProvider } from 'react-dnd-multi-backend';
import HTML5toTouch from 'react-dnd-multi-backend/dist/esm/HTML5toTouch';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageToolbar from 'Components/Page/Toolbar/PageToolbar';
import PageToolbarButton from 'Components/Page/Toolbar/PageToolbarButton';
import PageToolbarSection from 'Components/Page/Toolbar/PageToolbarSection';
import { align, icons } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import QuickstartAudioBookShelfSection from './QuickstartAudioBookShelfSection';
import QuickstartCustomFormatsSection from './QuickstartCustomFormatsSection';
import QuickstartDownloadClientsSection from './QuickstartDownloadClientsSection';
import QuickstartHardcoverSection from './QuickstartHardcoverSection';
import QuickstartMAMSection from './QuickstartMAMSection';
import QuickstartMatchingSection from './QuickstartMatchingSection';
import QuickstartMetadataProfilesSection from './QuickstartMetadataProfilesSection';
import QuickstartProxySection from './QuickstartProxySection';
import QuickstartQualityProfilesSection from './QuickstartQualityProfilesSection';
import QuickstartRootFoldersSection from './QuickstartRootFoldersSection';
import QuickstartSection, { QuickstartSectionProvider, useQuickstartSections } from './QuickstartSection';
import QuickstartSettingsBackupSection from './QuickstartSettingsBackupSection';
import styles from './Quickstart.css';

const QUICKSTART_SECTION_KEYS = [
  'optionalConnections',
  'indexers',
  'downloadClients',
  'customFormats',
  'qualityProfiles',
  'metadataProfiles',
  'matching',
  'rootFolders',
  'settingsBackup'
];

const GUIDED_QUICKSTART_SECTION_KEYS = QUICKSTART_SECTION_KEYS.filter((sectionKey) => sectionKey !== 'settingsBackup');
const GUIDED_REVIEW_SECTION_KEYS = [
  'optionalConnections',
  'indexers',
  'downloadClients',
  'customFormats',
  'qualityProfiles',
  'metadataProfiles',
  'matching'
];

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
    proxies
  } = props;

  const interactions = quickstartState?.interactions || {};
  const isRootFoldersLoaded = rootFoldersState?.isPopulated === true;
  const enabledIndexers = (indexersState?.items || []).filter((indexer) => indexer.enable);
  const enabledDownloadClients = (downloadClientsState?.items || []).filter((downloadClient) => downloadClient.enable);
  const hasRootFolders = (rootFoldersState?.items || []).length > 0;
  const showGuidance = isRootFoldersLoaded && !hasRootFolders;
  const guidedCompletedSectionKeys = [
    interactions.optionalConnections && 'optionalConnections',
    (enabledIndexers.length > 0 || interactions.indexers) && 'indexers',
    (enabledDownloadClients.length > 0 || interactions.downloadClients) && 'downloadClients',
    interactions.customFormats && 'customFormats',
    interactions.qualityProfiles && 'qualityProfiles',
    interactions.metadataProfiles && 'metadataProfiles',
    interactions.matching && 'matching',
    hasRootFolders && 'rootFolders'
  ].filter(Boolean);

  return (
    <PageContent title={translate('Quickstart')}>
      <QuickstartSectionProvider
        sectionKeys={QUICKSTART_SECTION_KEYS}
        guidedSectionKeys={GUIDED_QUICKSTART_SECTION_KEYS}
        guidedInitialSectionKey="optionalConnections"
        guidedReviewSectionKeys={GUIDED_REVIEW_SECTION_KEYS}
        guidedCompletedSectionKeys={guidedCompletedSectionKeys}
        isGuidedMode={showGuidance}
        onGuidedSectionReviewed={(sectionKey) => markSectionInteracted({ section: sectionKey })}
      >
        <QuickstartSectionToolbar showGuidance={showGuidance} />

        <PageContentBody>
          <DndProvider options={HTML5toTouch}>
            <QuickstartSection
              sectionKey="optionalConnections"
              title={translate('QuickstartOptionalConnectionsTitle')}
              isComplete={!!interactions.optionalConnections}
            >
              <div className={styles.optionalConnectionsRow}>
                <div className={styles.optionalConnectionItem}>
                  <QuickstartAudioBookShelfSection
                    hasActiveAudioBookShelf={hasActiveAudioBookShelf}
                    audioBookShelfNotification={audioBookShelfNotification}
                    notificationsState={notificationsState}
                    fetchNotificationSchema={fetchNotificationSchema}
                    selectNotificationSchema={selectNotificationSchema}
                    markSectionInteracted={markSectionInteracted}
                    fetchNotifications={fetchNotifications}
                    compact={true}
                  />
                </div>

                <div className={styles.optionalConnectionItem}>
                  <QuickstartHardcoverSection
                    compact={true}
                    isHardcoverConfigured={isHardcoverConfigured}
                    hardcoverUsername={hardcoverUsername}
                    hardcoverAvatarUrl={hardcoverAvatarUrl}
                    markSectionInteracted={markSectionInteracted}
                  />
                </div>

                <div className={styles.optionalConnectionItem}>
                  <QuickstartProxySection proxies={proxies} />
                </div>
              </div>
            </QuickstartSection>

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

              <QuickstartQualityProfilesSection markSectionInteracted={markSectionInteracted} />
            </QuickstartSection>

            <QuickstartSection
              sectionKey="metadataProfiles"
              title={translate('QuickstartMetadataProfilesTitle')}
              isComplete={!!interactions.metadataProfiles}
            >
              <div className={styles.sectionDescription}>
                {translate('QuickstartMetadataProfilesDescription')}
              </div>

              <QuickstartMetadataProfilesSection markSectionInteracted={markSectionInteracted} />
            </QuickstartSection>

            <QuickstartMatchingSection
              markSectionInteracted={markSectionInteracted}
              quickstartState={quickstartState}
            />

            <QuickstartRootFoldersSection
              markSectionInteracted={markSectionInteracted}
              quickstartState={quickstartState}
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
  mamIndexer: PropTypes.object,
  indexersState: PropTypes.object.isRequired,
  notificationsState: PropTypes.object.isRequired,
  downloadClientsState: PropTypes.object.isRequired,
  rootFoldersState: PropTypes.object.isRequired,
  quickstartState: PropTypes.object,
  proxies: PropTypes.arrayOf(PropTypes.object),
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
