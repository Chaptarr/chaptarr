import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import * as commandNames from 'Commands/commandNames';
import withCurrentPage from 'Components/withCurrentPage';
import { executeCommand } from 'Store/Actions/commandActions';
import * as queueActions from 'Store/Actions/queueActions';
import { fetchMediaManagementSettings, saveMediaManagementSettings, setMediaManagementSettingsValue } from 'Store/Actions/settingsActions';
import createCommandExecutingSelector from 'Store/Selectors/createCommandExecutingSelector';
import createSettingsSectionSelector from 'Store/Selectors/createSettingsSectionSelector';
import { registerPagePopulator, unregisterPagePopulator } from 'Utilities/pagePopulator';
import Queue from './Queue';

function createMapStateToProps() {
  return createSelector(
    (state) => state.authors,
    (state) => state.books,
    (state) => state.queue.options,
    (state) => state.queue.paged,
    createCommandExecutingSelector(commandNames.REFRESH_MONITORED_DOWNLOADS),
    createCommandExecutingSelector(commandNames.RETRY_FAILED_IMPORT),
    createSettingsSectionSelector('mediaManagement'),
    (authors, books, options, queue, isRefreshMonitoredDownloadsExecuting, isRetryingImport, mediaManagementSettings) => {
      return {
        isAuthorFetching: authors.isFetching,
        isAuthorPopulated: authors.isPopulated,
        isBooksFetching: books.isFetching,
        isBooksPopulated: books.isPopulated,
        booksError: books.error,
        isRefreshMonitoredDownloadsExecuting,
        isRetryingImport,
        autoAddMissingAuthorsFromCompletedDownloads: !!mediaManagementSettings.settings?.autoAddMissingAuthorsFromCompletedDownloads?.value,
        isAutoAddMissingAuthorsPopulated: mediaManagementSettings.isPopulated,
        isAutoAddMissingAuthorsSaving: mediaManagementSettings.isSaving,
        ...options,
        ...queue
      };
    }
  );
}

const mapDispatchToProps = {
  ...queueActions,
  executeCommand,
  fetchMediaManagementSettings,
  setMediaManagementSettingsValue,
  saveMediaManagementSettings
};

class QueueConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    const {
      useCurrentPage,
      fetchQueue,
      fetchQueueStatus,
      gotoQueueFirstPage,
      fetchMediaManagementSettings: fetchMediaManagementSettingsProp
    } = this.props;

    registerPagePopulator(this.repopulate);

    if (useCurrentPage) {
      fetchQueue();
    } else {
      gotoQueueFirstPage();
    }

    fetchQueueStatus();
    fetchMediaManagementSettingsProp();
  }

  componentDidUpdate(prevProps) {
    if (
      this.props.includeUnknownAuthorItems !==
      prevProps.includeUnknownAuthorItems
    ) {
      this.repopulate();
    }
  }

  componentWillUnmount() {
    unregisterPagePopulator(this.repopulate);
  }

  //
  // Control

  repopulate = () => {
    this.props.fetchQueue();
  };

  //
  // Listeners

  onFirstPagePress = () => {
    this.props.gotoQueueFirstPage();
  };

  onPreviousPagePress = () => {
    this.props.gotoQueuePreviousPage();
  };

  onNextPagePress = () => {
    this.props.gotoQueueNextPage();
  };

  onLastPagePress = () => {
    this.props.gotoQueueLastPage();
  };

  onPageSelect = (page) => {
    this.props.gotoQueuePage({ page });
  };

  onSortPress = (sortKey) => {
    this.props.setQueueSort({ sortKey });
  };

  onTableOptionChange = (payload) => {
    this.props.setQueueTableOption(payload);

    if (payload.pageSize) {
      this.props.gotoQueueFirstPage();
    }
  };

  onRefreshPress = () => {
    this.props.executeCommand({
      name: commandNames.REFRESH_MONITORED_DOWNLOADS
    });
  };

  onRetryImportSelectedPress = (downloadIds) => {
    this.props.executeCommand({
      name: commandNames.RETRY_FAILED_IMPORT,
      downloadIds
    });
  };

  onGrabSelectedPress = (ids) => {
    this.props.grabQueueItems({ ids });
  };

  onRemoveSelectedPress = (payload) => {
    this.props.removeQueueItems(payload);
  };

  onAutoAddAuthorsPress = (value) => {
    const nextValue = typeof value === 'boolean' ?
      value :
      !this.props.autoAddMissingAuthorsFromCompletedDownloads;

    this.props.setMediaManagementSettingsValue({
      name: 'autoAddMissingAuthorsFromCompletedDownloads',
      value: nextValue
    });

    this.props.saveMediaManagementSettings({
      autoAddMissingAuthorsFromCompletedDownloads: nextValue
    });
  };

  //
  // Render

  render() {
    return (
      <Queue
        onFirstPagePress={this.onFirstPagePress}
        onPreviousPagePress={this.onPreviousPagePress}
        onNextPagePress={this.onNextPagePress}
        onLastPagePress={this.onLastPagePress}
        onPageSelect={this.onPageSelect}
        onSortPress={this.onSortPress}
        onTableOptionChange={this.onTableOptionChange}
        onRefreshPress={this.onRefreshPress}
        onRetryImportSelectedPress={this.onRetryImportSelectedPress}
        onGrabSelectedPress={this.onGrabSelectedPress}
        onRemoveSelectedPress={this.onRemoveSelectedPress}
        onAutoAddAuthorsPress={this.onAutoAddAuthorsPress}
        {...this.props}
      />
    );
  }
}

QueueConnector.propTypes = {
  useCurrentPage: PropTypes.bool.isRequired,
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  includeUnknownAuthorItems: PropTypes.bool.isRequired,
  autoAddMissingAuthorsFromCompletedDownloads: PropTypes.bool.isRequired,
  fetchQueue: PropTypes.func.isRequired,
  fetchQueueStatus: PropTypes.func.isRequired,
  gotoQueueFirstPage: PropTypes.func.isRequired,
  gotoQueuePreviousPage: PropTypes.func.isRequired,
  gotoQueueNextPage: PropTypes.func.isRequired,
  gotoQueueLastPage: PropTypes.func.isRequired,
  gotoQueuePage: PropTypes.func.isRequired,
  setQueueSort: PropTypes.func.isRequired,
  setQueueTableOption: PropTypes.func.isRequired,
  clearQueue: PropTypes.func.isRequired,
  fetchMediaManagementSettings: PropTypes.func.isRequired,
  setMediaManagementSettingsValue: PropTypes.func.isRequired,
  saveMediaManagementSettings: PropTypes.func.isRequired,
  grabQueueItems: PropTypes.func.isRequired,
  removeQueueItems: PropTypes.func.isRequired,
  executeCommand: PropTypes.func.isRequired
};

export default withCurrentPage(
  connect(createMapStateToProps, mapDispatchToProps)(QueueConnector)
);
