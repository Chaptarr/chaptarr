import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import * as commandNames from 'Commands/commandNames';
import { executeCommand } from 'Store/Actions/commandActions';
import { fetchConvertPreview } from 'Store/Actions/convertPreviewActions';
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
  executeCommand
};

class ConvertPreviewModalContentConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    const {
      authorId,
      bookId
    } = this.props;

    this.props.fetchConvertPreview({
      authorId,
      bookId
    });
  }

  //
  // Listeners

  onConvertPress = (files) => {
    this.props.executeCommand({
      name: commandNames.CONVERT_BOOK_FILES,
      authorId: this.props.authorId,
      bookId: this.props.bookId,
      files
    });

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
  onModalClose: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(ConvertPreviewModalContentConnector);
