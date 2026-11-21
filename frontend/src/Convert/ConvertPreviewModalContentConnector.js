import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import * as commandNames from 'Commands/commandNames';
import { messageTypes } from 'Helpers/Props';
import { showMessage } from 'Store/Actions/appActions';
import { executeCommand } from 'Store/Actions/commandActions';
import { fetchConvertPreview } from 'Store/Actions/convertPreviewActions';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';
import ConvertPreviewModalContent from './ConvertPreviewModalContent';

function createMapStateToProps() {
  return createSelector(
    (state) => state.convertPreview,
    (convertPreview) => {
      return { ...convertPreview };
    }
  );
}

const mapDispatchToProps = {
  fetchConvertPreview,
  executeCommand,
  showMessage
};

class ConvertPreviewModalContentConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    const {
      authorId,
      bookId
    } = this.props;

    this._abortPreview = this.props.fetchConvertPreview({
      authorId,
      bookId
    });
  }

  componentWillUnmount() {
    if (this._abortPreview) {
      this._abortPreview();
      this._abortPreview = null;
    }
  }

  //
  // Listeners

  onConvertPress = (files) => {
    const request = this.props.executeCommand({
      name: commandNames.CONVERT_BOOK_FILES,
      authorId: this.props.authorId,
      bookId: this.props.bookId,
      files
    });

    // The window closes straight away, so a request that fails is reported as a message
    // instead of leaving the user to assume the conversion was queued.
    const showFailure = this.props.showMessage;

    if (request && request.catch) {
      request.catch((xhr) => {
        showFailure({
          id: 'convert-book-files',
          name: 'ConvertBookFiles',
          message: getErrorMessage(xhr, translate('UnableToStartConversion')),
          type: messageTypes.ERROR,
          hideAfter: 8
        });
      });
    }

    this.props.onModalClose();
  };

  //
  // Render

  render() {
    return (
      <ConvertPreviewModalContent
        {...this.props}
        onConvertPress={this.onConvertPress}
      />
    );
  }
}

ConvertPreviewModalContentConnector.propTypes = {
  authorId: PropTypes.number.isRequired,
  bookId: PropTypes.number,
  fetchConvertPreview: PropTypes.func.isRequired,
  executeCommand: PropTypes.func.isRequired,
  showMessage: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(ConvertPreviewModalContentConnector);
