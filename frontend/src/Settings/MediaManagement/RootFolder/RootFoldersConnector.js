import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import * as commandNames from 'Commands/commandNames';
import { executeCommand } from 'Store/Actions/commandActions';
import { deleteRootFolder, fetchRootFolders, setRootFolderValue } from 'Store/Actions/settingsActions';
import createCommandExecutingSelector from 'Store/Selectors/createCommandExecutingSelector';
import RootFolders from './RootFolders';

function createMapStateToProps() {
  return createSelector(
    (state) => state.settings.rootFolders,
    (state) => state.settings.qualityProfiles,
    (state) => state.settings.metadataProfiles,
    createCommandExecutingSelector(commandNames.BACKFILL_AUTHOR_ROOT_FOLDERS),
    (rootFolders, quality, metadata, isBackfillingAuthorRootFolders) => {
      return {
        qualityProfiles: quality.items,
        metadataProfiles: metadata.items,
        isBackfillingAuthorRootFolders,
        ...rootFolders
      };
    }
  );
}

const mapDispatchToProps = {
  dispatchFetchRootFolders: fetchRootFolders,
  dispatchDeleteRootFolder: deleteRootFolder,
  executeCommand,
  setRootFolderValue
};

class RootFoldersConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    this.props.dispatchFetchRootFolders();
  }

  //
  // Listeners

  onConfirmDeleteRootFolder = (id) => {
    this.props.dispatchDeleteRootFolder({ id });
  };

  onBackfillAuthorRootFolders = () => {
    return this.props.executeCommand({
      name: commandNames.BACKFILL_AUTHOR_ROOT_FOLDERS,
      suppressStartErrorMessage: true
    });
  };

  //
  // Render

  render() {
    return (
      <RootFolders
        {...this.props}
        onConfirmDeleteRootFolder={this.onConfirmDeleteRootFolder}
        onBackfillAuthorRootFolders={this.onBackfillAuthorRootFolders}
      />
    );
  }
}

RootFoldersConnector.propTypes = {
  dispatchFetchRootFolders: PropTypes.func.isRequired,
  dispatchDeleteRootFolder: PropTypes.func.isRequired,
  executeCommand: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(RootFoldersConnector);
