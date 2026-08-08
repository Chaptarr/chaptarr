import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { deleteQualityProfile, fetchImportLists, fetchQualityProfiles, fetchRootFolders } from 'Store/Actions/settingsActions';
import createSortedSectionSelector from 'Store/Selectors/createSortedSectionSelector';
import sortByName from 'Utilities/Array/sortByName';
import QualityProfiles from './QualityProfiles';

function createMapStateToProps() {
  return createSelector(
    createSortedSectionSelector('settings.qualityProfiles', sortByName),
    (qualityProfiles) => qualityProfiles
  );
}

const mapDispatchToProps = {
  dispatchFetchQualityProfiles: fetchQualityProfiles,
  dispatchFetchImportLists: fetchImportLists,
  dispatchFetchRootFolders: fetchRootFolders,
  dispatchDeleteQualityProfile: deleteQualityProfile
};

class QualityProfilesConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    this.props.dispatchFetchQualityProfiles();
    this.props.dispatchFetchImportLists();
    this.props.dispatchFetchRootFolders();
  }

  //
  // Listeners

  onConfirmDeleteQualityProfile = (id) => {
    this.props.dispatchDeleteQualityProfile({ id });
  };

  //
  // Render

  render() {
    return (
      <QualityProfiles
        onConfirmDeleteQualityProfile={this.onConfirmDeleteQualityProfile}
        {...this.props}
      />
    );
  }
}

QualityProfilesConnector.propTypes = {
  dispatchFetchQualityProfiles: PropTypes.func.isRequired,
  dispatchFetchImportLists: PropTypes.func.isRequired,
  dispatchFetchRootFolders: PropTypes.func.isRequired,
  dispatchDeleteQualityProfile: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(QualityProfilesConnector);
