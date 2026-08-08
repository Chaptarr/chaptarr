import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { saveRootFolder } from 'Store/Actions/Settings/rootFolders';
import AddRootFolderInline from './AddRootFolderInline';

function createMapStateToProps() {
  return createSelector(
    (state) => state.settings.qualityProfiles,
    (qualityProfiles) => {
      return {
        isAddingRootFolder: false // Will be updated when we add the action
      };
    }
  );
}

const mapDispatchToProps = {
  dispatchSaveRootFolder: saveRootFolder
};

class AddRootFolderInlineConnector extends Component {
  //
  // Listeners

  onAddRootFolder = (rootFolder) => {
    const { dispatchSaveRootFolder, onRootFolderAdded } = this.props;

    dispatchSaveRootFolder(rootFolder).then((result) => {
      if (result && result.value && result.value.id) {
        // Notify parent that root folder was added
        if (onRootFolderAdded) {
          onRootFolderAdded(result.value);
        }
      }
    });
  };

  //
  // Render

  render() {
    return (
      <AddRootFolderInline
        {...this.props}
        onAddRootFolder={this.onAddRootFolder}
      />
    );
  }
}

AddRootFolderInlineConnector.propTypes = {
  folderType: PropTypes.number.isRequired,
  dispatchSaveRootFolder: PropTypes.func.isRequired,
  onRootFolderAdded: PropTypes.func
};

export default connect(createMapStateToProps, mapDispatchToProps)(AddRootFolderInlineConnector);
