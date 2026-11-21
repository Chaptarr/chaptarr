import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { deleteAuthor } from 'Store/Actions/authorActions';
import createAuthorSelector from 'Store/Selectors/createAuthorSelector';
import DeleteAuthorModalContent from './DeleteAuthorModalContent';

function createMapStateToProps() {
  return createSelector(
    createAuthorSelector(),
    (state) => state.authors,
    (author, { isDeleting, deleteError }) => ({ authors: author ? [author] : [], isDeleting, deleteError })
  );
}

const mapDispatchToProps = {
  deleteAuthor
};

class DeleteAuthorModalContentConnector extends Component {

  //
  // Listeners

  onDeletePress = (options) => {
    this.props.deleteAuthor({ id: this.props.authorId, ...options })
      .done(() => this.props.onModalClose(true));
  };

  //
  // Render

  render() {
    return (
      <DeleteAuthorModalContent
        {...this.props}
        allowReadd={true}
        onDeletePress={this.onDeletePress}
      />
    );
  }
}

DeleteAuthorModalContentConnector.propTypes = {
  authorId: PropTypes.number.isRequired,
  onModalClose: PropTypes.func.isRequired,
  deleteAuthor: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(DeleteAuthorModalContentConnector);
