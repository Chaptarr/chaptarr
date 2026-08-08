import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import {
  bulkDeleteImportListExclusions,
  deleteImportListExclusion,
  fetchImportListExclusions
} from 'Store/Actions/settingsActions';
import ImportListExclusions from './ImportListExclusions';

function createMapStateToProps() {
  return createSelector(
    (state) => state.settings.importListExclusions,
    (importListExclusions) => {
      return {
        ...importListExclusions
      };
    }
  );
}

const mapDispatchToProps = {
  fetchImportListExclusions,
  deleteImportListExclusion,
  bulkDeleteImportListExclusions
};

class ImportListExclusionsConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    this.props.fetchImportListExclusions();
  }

  //
  // Listeners

  onConfirmDeleteImportListExclusion = (id) => {
    this.props.deleteImportListExclusion({ id });
  };

  onConfirmDeleteSelectedImportListExclusions = (ids) => {
    this.props.bulkDeleteImportListExclusions({ ids });
  };

  //
  // Render

  render() {
    return (
      <ImportListExclusions
        {...this.state}
        {...this.props}
        onConfirmDeleteImportListExclusion={this.onConfirmDeleteImportListExclusion}
        onConfirmDeleteSelectedImportListExclusions={this.onConfirmDeleteSelectedImportListExclusions}
      />
    );
  }
}

ImportListExclusionsConnector.propTypes = {
  fetchImportListExclusions: PropTypes.func.isRequired,
  deleteImportListExclusion: PropTypes.func.isRequired,
  bulkDeleteImportListExclusions: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(ImportListExclusionsConnector);
