import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { 
  fetchIndexers, 
  fetchNotifications,
  fetchDownloadClients,
  fetchRootFolders,
  fetchQualityProfiles,
  fetchMetadataProfiles,
  fetchIndexerSchema,
  selectIndexerSchema,
  fetchNotificationSchema,
  selectNotificationSchema,
  fetchDownloadClientSchema,
  selectDownloadClientSchema,
  fetchGeneralSettings,
  fetchProxies
} from 'Store/Actions/settingsActions';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import queryClient from 'Store/queryClient';
import { loadQuickstartState, markSectionInteracted } from 'Store/Actions/quickstartActions';
import Quickstart from './Quickstart';

function createMapStateToProps() {
  return createSelector(
    (state) => state.settings.indexers,
    (state) => state.settings.notifications,
    (state) => state.settings.downloadClients,
    (state) => state.settings.rootFolders,
    (state) => state.settings.qualityProfiles,
    (state) => state.settings.metadataProfiles,
    (state) => state.quickstart,
    (state) => state.settings.general,
    (state) => state.settings.proxies,
    (state) => state.settings.hardcoverConfig,
    (indexersState, notificationsState, downloadClientsState, rootFoldersState, qualityProfilesState, metadataProfilesState, quickstartState, generalSettings, proxiesState, hardcoverConfig) => {
      const { items: indexers = [] } = indexersState || {};
      const { items: notifications = [] } = notificationsState || {};
      const { items: downloadClients = [] } = downloadClientsState || {};
      const { rootFolders = [] } = rootFoldersState || {};
      const { items: qualityProfiles = [] } = qualityProfilesState || {};
      const { items: metadataProfiles = [] } = metadataProfilesState || {};
      
      // Find first MAM indexer
      const mamIndexer = indexers.find((indexer) => 
        indexer.implementationName && 
        indexer.implementationName.toLowerCase().includes('myanona')
      );

      // Find first AudioBookShelf notification
      const audioBookShelfNotification = notifications.find((notification) => 
        notification.implementationName === 'AudioBookShelf'
      );

      // Check if proxy is configured
      const proxyMode = generalSettings.item?.proxyMode?.value || 'disabled';
      const globalProxyId = generalSettings.item?.globalProxyId?.value;
      const proxyHostname = generalSettings.item?.proxyHostname?.value; // legacy fallback
      const proxyPort = generalSettings.item?.proxyPort?.value; // legacy fallback

      // Proxy is considered configured if proxy mode is enabled AND either:
      // - a GlobalProxyId is selected (new proxy definitions), OR
      // - legacy hostname/port is set (fallback)
      const isProxyConfigured = (proxyMode === 'indexerOnly' || proxyMode === 'proxyEverything') &&
        (!!globalProxyId || (proxyHostname && proxyPort));

      const { isPopulated, error, item } = hardcoverConfig;
      const isHardcoverConfigured = (!isPopulated && !error)
        ? null
        : !!(item?.enabled && item?.hasToken);

      return {
        hasActiveMAMIndexer: !!(mamIndexer && mamIndexer.enable),
        hasActiveAudioBookShelf: !!(audioBookShelfNotification && audioBookShelfNotification.enable),
        mamIndexer,
        audioBookShelfNotification,
        indexersState,
        notificationsState,
        downloadClientsState,
        rootFoldersState,
        qualityProfilesState,
        metadataProfilesState,
        quickstartState,
        isProxyConfigured,
        proxies: proxiesState?.items || [],
        isHardcoverConfigured,
        hardcoverUsername: item?.username || '',
        hardcoverAvatarUrl: item?.avatarUrl || ''
      };
    }
  );
}

const mapDispatchToProps = {
  fetchIndexers,
  fetchNotifications,
  fetchDownloadClients,
  fetchRootFolders,
  fetchQualityProfiles,
  fetchMetadataProfiles,
  fetchIndexerSchema,
  selectIndexerSchema,
  fetchNotificationSchema,
  selectNotificationSchema,
  fetchDownloadClientSchema,
  selectDownloadClientSchema,
  loadQuickstartState,
  markSectionInteracted,
  fetchGeneralSettings,
  fetchProxies
};

class QuickstartConnector extends Component {
  //
  // Lifecycle

  componentDidMount() {
    this.props.fetchIndexers();
    this.props.fetchNotifications();
    this.props.fetchDownloadClients();
    this.props.fetchRootFolders();
    this.props.fetchQualityProfiles();
    this.props.fetchMetadataProfiles();
    this.props.fetchGeneralSettings();
    this.props.fetchProxies();
    this.fetchInstallationId();
  }

  fetchInstallationId = () => {
    // Use react-query's fetchQuery for caching: if /system/status was already
    // fetched elsewhere (e.g., SystemStatus page), we get the cached response
    // instantly instead of hitting the API again.
    queryClient.fetchQuery(
      ['systemStatus'],
      () => createAjaxRequest({
        url: '/system/status',
        method: 'GET'
      }).request
    ).then((data) => {
      const installationId = data?.installationId;

      if (installationId) {
        this.props.loadQuickstartState({ installationId });
      } else {
        this.props.loadQuickstartState();
      }
    }).catch(() => {
      this.props.loadQuickstartState();
    });
  };

  //
  // Render

  render() {
    return (
      <Quickstart
        {...this.props}
      />
    );
  }
}

QuickstartConnector.propTypes = {
  fetchIndexers: PropTypes.func.isRequired,
  fetchNotifications: PropTypes.func.isRequired,
  fetchDownloadClients: PropTypes.func.isRequired,
  fetchRootFolders: PropTypes.func.isRequired,
  fetchQualityProfiles: PropTypes.func.isRequired,
  fetchMetadataProfiles: PropTypes.func.isRequired,
  fetchIndexerSchema: PropTypes.func.isRequired,
  selectIndexerSchema: PropTypes.func.isRequired,
  fetchNotificationSchema: PropTypes.func.isRequired,
  selectNotificationSchema: PropTypes.func.isRequired,
  fetchDownloadClientSchema: PropTypes.func.isRequired,
  selectDownloadClientSchema: PropTypes.func.isRequired,
  loadQuickstartState: PropTypes.func.isRequired,
  markSectionInteracted: PropTypes.func.isRequired,
  fetchGeneralSettings: PropTypes.func.isRequired,
  fetchProxies: PropTypes.func.isRequired,
  hasActiveAudioBookShelf: PropTypes.bool,
  indexersState: PropTypes.object.isRequired,
  downloadClientsState: PropTypes.object.isRequired,
  rootFoldersState: PropTypes.object.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(QuickstartConnector);
