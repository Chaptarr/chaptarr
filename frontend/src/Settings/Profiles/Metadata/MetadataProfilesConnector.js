import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { cloneMetadataProfile, deleteMetadataProfile, fetchMetadataProfiles, fetchRootFolders } from 'Store/Actions/settingsActions';
import MetadataProfiles from './MetadataProfiles';

function createMapStateToProps() {
  return createSelector(
    (state) => state.settings.advancedSettings,
    (state) => state.settings.metadataProfiles,
    (advancedSettings, metadataProfiles) => {
      return {
        advancedSettings,
        ...metadataProfiles
      };
    }
  );
}

const mapDispatchToProps = {
  dispatchFetchMetadataProfiles: fetchMetadataProfiles,
  dispatchFetchRootFolders: fetchRootFolders,
  dispatchDeleteMetadataProfile: deleteMetadataProfile,
  dispatchCloneMetadataProfile: cloneMetadataProfile
};

class MetadataProfilesConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    // Settings screen needs ALL profiles for management - fetch without mediaType filter
    this.props.dispatchFetchMetadataProfiles();
    this.props.dispatchFetchRootFolders();
  }

  //
  // Listeners

  onConfirmDeleteMetadataProfile = (id) => {
    this.props.dispatchDeleteMetadataProfile({ id });
  };

  onCloneMetadataProfilePress = (id) => {
    this.props.dispatchCloneMetadataProfile({ id });
  };

  //
  // Render

  render() {
    return (
      <MetadataProfiles
        onConfirmDeleteMetadataProfile={this.onConfirmDeleteMetadataProfile}
        onCloneMetadataProfilePress={this.onCloneMetadataProfilePress}
        {...this.props}
      />
    );
  }
}

MetadataProfilesConnector.propTypes = {
  dispatchFetchMetadataProfiles: PropTypes.func.isRequired,
  dispatchFetchRootFolders: PropTypes.func.isRequired,
  dispatchDeleteMetadataProfile: PropTypes.func.isRequired,
  dispatchCloneMetadataProfile: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(MetadataProfilesConnector);
