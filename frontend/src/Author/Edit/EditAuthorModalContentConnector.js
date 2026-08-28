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
import EditAuthorModalContent from './EditAuthorModalContent';

function createIsPathChangingSelector() {
  return createSelector(
    (state) => state.authors.pendingChanges,
    createAuthorSelector(),
    (pendingChanges, author) => {
      const path = pendingChanges.path;

      if (path == null) {
        return false;
      }

      return author.path !== path;
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
    createIsPathChangingSelector(),
    (authorsState, metadataProfiles, rootFolders, systemStatus, author, isPathChanging) => {
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
        'audiobookPath',
        'ebookPath',
        'rootFolderPath',
        'audiobookRootFolderPath',
        'ebookRootFolderPath',
        'tags',
        'audiobookTags',
        'ebookTags'
      ]);

      const settings = selectSettings(authorSettings, pendingChanges, saveError);

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
        isPathChanging,
        originalPath: author.path,
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
    this.props.dispatchSetAuthorValue({ name, value });
  };

  onSavePress = (moveFiles) => {
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
  dispatchSetAuthorValue: PropTypes.func.isRequired,
  dispatchSaveAuthor: PropTypes.func.isRequired,
  dispatchFetchRootFolders: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(EditAuthorModalContentConnector);
