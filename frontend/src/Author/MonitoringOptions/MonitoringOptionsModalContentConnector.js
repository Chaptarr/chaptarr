import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { updateBookMonitor } from 'Store/Actions/authorActions';
import MonitoringOptionsModalContent from './MonitoringOptionsModalContent';

function createMapStateToProps() {
  return createSelector(
    (state) => state.authors,
    (authorState) => {
      const {
        isSaving,
        saveError
      } = authorState;

      return {
        isSaving,
        saveError
      };
    }
  );
}

const mapDispatchToProps = {
  dispatchUpdateMonitoringOptions: updateBookMonitor
};

class MonitoringOptionsModalContentConnector extends Component {

  //
  // Lifecycle

  componentDidUpdate(prevProps, prevState) {
    if (prevProps.isSaving && !this.props.isSaving && !this.props.saveError) {
      this.props.onModalClose(true);
    }
  }

  onSavePress = ({ monitor }) => {
    this.props.dispatchUpdateMonitoringOptions({
      id: this.props.authorId,
      monitor,
      mediaType: this.props.mediaType
    });
  };

  //
  // Render

  render() {
    return (
      <MonitoringOptionsModalContent
        {...this.props}
        onSavePress={this.onSavePress}
      />
    );
  }
}

MonitoringOptionsModalContentConnector.propTypes = {
  authorId: PropTypes.number.isRequired,
  mediaType: PropTypes.string,
  isSaving: PropTypes.bool.isRequired,
  saveError: PropTypes.object,
  dispatchUpdateMonitoringOptions: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired,
  onSavePress: PropTypes.func
};

export default connect(createMapStateToProps, mapDispatchToProps)(MonitoringOptionsModalContentConnector);
