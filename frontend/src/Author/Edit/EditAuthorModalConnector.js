import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { clearPendingChanges } from 'Store/Actions/baseActions';
import EditAuthorModal from './EditAuthorModal';

const mapDispatchToProps = {
  clearPendingChanges
};

class EditAuthorModalConnector extends Component {

  componentDidUpdate(prevProps) {
    if (prevProps.isOpen && !this.props.isOpen) {
      this.props.clearPendingChanges({ section: 'authors' });
    }
  }

  componentWillUnmount() {
    if (this.props.isOpen) {
      this.props.clearPendingChanges({ section: 'authors' });
    }
  }

  //
  // Render

  render() {
    return (
      <EditAuthorModal
        {...this.props}
      />
    );
  }
}

EditAuthorModalConnector.propTypes = {
  isOpen: PropTypes.bool.isRequired,
  onModalClose: PropTypes.func.isRequired,
  clearPendingChanges: PropTypes.func.isRequired
};

export default connect(undefined, mapDispatchToProps)(EditAuthorModalConnector);
