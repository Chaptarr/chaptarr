import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { clearConvertPreview } from 'Store/Actions/convertPreviewActions';
import ConvertPreviewModal from './ConvertPreviewModal';

const mapDispatchToProps = {
  clearConvertPreview
};

class ConvertPreviewModalConnector extends Component {

  //
  // Listeners

  onModalClose = () => {
    this.props.clearConvertPreview();
    this.props.onModalClose();
  };

  //
  // Render

  render() {
    return (
      <ConvertPreviewModal
        {...this.props}
        onModalClose={this.onModalClose}
      />
    );
  }
}

ConvertPreviewModalConnector.propTypes = {
  clearConvertPreview: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default connect(undefined, mapDispatchToProps)(ConvertPreviewModalConnector);
