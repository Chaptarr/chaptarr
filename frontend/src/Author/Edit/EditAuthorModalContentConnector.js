import _ from 'lodash';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { coerceFolderType, FolderType } from 'Helpers/Props/folderTypes';
import { saveAuthor, setAuthorValue } from 'Store/Actions/authorActions';
import { fetchRootFolders } from 'Store/Actions/Settings/rootFolders';
import createAuthorSelector from 'Store/Selectors/createAuthorSelector';
import createSystemStatusSelector from 'Store/Selectors/createSystemStatusSelector';
import selectSettings from 'Store/Selectors/selectSettings';
import getLinkedAuthorMonitoringChanges from 'Utilities/Author/getLinkedAuthorMonitoringChanges';
import { normalizeMonitorNewItemsOption } from 'Utilities/Author/monitorNewItemsOptions';
import EditAuthorModalContent from './EditAuthorModalContent';

function createPathChangesSelector() {
  return createSelector(
    (state) => state.authors.pendingChanges,
    createAuthorSelector(),
    (pendingChanges, author) => {
      return ['audiobook', 'ebook'].filter((mediaType) => {
        const field = `${mediaType}Folder`;
        return pendingChanges[field] != null && pendingChanges[field] !== author[field];
      }).map((mediaType) => ({
        mediaType,
        originalPath: author[`${mediaType}Folder`],
        destinationPath: pendingChanges[`${mediaType}Folder`]
      }));
    }
  );
}

function createMapStateToProps() {
  return createSelector(
    (state) => state.authors,
    (state) => state.settings.metadataProfiles,
    (state) => state.settings.rootFolders,
    createSystemStatusSelector(),
    createAuthorSelector(),
    createPathChangesSelector(),
    (authorsState, metadataProfiles, rootFolders, systemStatus, author, pathChanges) => {
      const {
        isSaving,
        saveError,
        pendingChanges
      } = authorsState;

      const authorSettings = _.pick(author, [
        'monitored',
        'audiobookMonitored',
        'audiobookMonitorNewItems',
        'ebookMonitored',
        'ebookMonitorNewItems',
        'syncMonitoredAcrossFormats',
        'audiobookQualityProfileId',
        'ebookQualityProfileId',
        'audiobookMetadataProfileId',
        'ebookMetadataProfileId',
        'path',
        'audiobookFolder',
        'ebookFolder',
        'rootFolderPath',
        'audiobookRootFolderPath',
        'ebookRootFolderPath',
        'tags',
        'audiobookTags',
        'ebookTags'
      ]);

      const settings = selectSettings(authorSettings, pendingChanges, saveError);

      const item = settings.settings;
      const hasMonitoringPolicyChange = ['syncMonitoredAcrossFormats', 'audiobookMonitorNewItems', 'ebookMonitorNewItems']
        .some((key) => Object.prototype.hasOwnProperty.call(pendingChanges, key));
      const hasConflictingMonitoringPolicies = hasMonitoringPolicyChange && item.syncMonitoredAcrossFormats?.value === true &&
        normalizeMonitorNewItemsOption(item.audiobookMonitorNewItems?.value) !== normalizeMonitorNewItemsOption(item.ebookMonitorNewItems?.value);
      const rootFolderItems = rootFolders.items || [];

      // Global availability: do compatible root folders exist in the system?
      const hasAudiobookRootFolder = rootFolderItems.some((folder) => {
        const folderType = coerceFolderType(folder.folderType);
        return folderType === FolderType.Audiobook || folderType === FolderType.Mixed;
      });

      const hasEbookRootFolder = rootFolderItems.some((folder) => {
        const folderType = coerceFolderType(folder.folderType);
        return folderType === FolderType.Ebook || folderType === FolderType.Mixed;
      });

      return {
        authorName: author.authorName,
        isSaving,
        saveError,
        isPathChanging: pathChanges.length > 0,
        pathChanges,
        sharedFolder: Boolean(author.audiobookFolder && author.audiobookFolder === author.ebookFolder),
        originalPath: pathChanges[0]?.originalPath || author.path,
        hasConflictingMonitoringPolicies,
        item: settings.settings,
        showMetadataProfile: metadataProfiles.items.length > 1,
        isWindows: systemStatus.isWindows,
        hasAudiobookRootFolder,
        hasEbookRootFolder,
        rootFolders: rootFolders.items,
        ...settings
      };
    }
  );
}

const mapDispatchToProps = {
  dispatchSetAuthorValue: setAuthorValue,
  dispatchSaveAuthor: saveAuthor,
  dispatchFetchRootFolders: fetchRootFolders
};

class EditAuthorModalContentConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    // Ensure root folders are loaded
    this.props.dispatchFetchRootFolders();
  }

  componentDidUpdate(prevProps, prevState) {
    if (prevProps.isSaving && !this.props.isSaving && !this.props.saveError) {
      this.props.onModalClose();
    }
  }

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    const changes = getLinkedAuthorMonitoringChanges(name, value, this.props.item.syncMonitoredAcrossFormats?.value === true);
    Object.entries(changes).forEach(([field, fieldValue]) => this.props.dispatchSetAuthorValue({ name: field, value: fieldValue }));
  };

  onSavePress = (moveFiles) => {
    if (this.props.hasConflictingMonitoringPolicies) {
      return;
    }
    this.props.dispatchSaveAuthor({
      id: this.props.authorId,
      moveFiles
    });
  };

  //
  // Render

  render() {
    return (
      <EditAuthorModalContent
        {...this.props}
        onInputChange={this.onInputChange}
        onSavePress={this.onSavePress}
        onMoveAuthorPress={this.onMoveAuthorPress}
      />
    );
  }
}

EditAuthorModalContentConnector.propTypes = {
  authorId: PropTypes.number,
  isSaving: PropTypes.bool.isRequired,
  saveError: PropTypes.object,
  item: PropTypes.object.isRequired,
  hasConflictingMonitoringPolicies: PropTypes.bool.isRequired,
  dispatchSetAuthorValue: PropTypes.func.isRequired,
  dispatchSaveAuthor: PropTypes.func.isRequired,
  dispatchFetchRootFolders: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(EditAuthorModalContentConnector);
