import find from 'lodash/find';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import {
  clearInteractiveImportBooks,
  fetchInteractiveImportBooks,
  saveInteractiveImportItem,
  setInteractiveImportBooksSort,
  updateInteractiveImportItem } from 'Store/Actions/interactiveImportActions';
import createClientSideCollectionSelector from 'Store/Selectors/createClientSideCollectionSelector';
import SelectBookModalContent from './SelectBookModalContent';

function createMapStateToProps() {
  return createSelector(
    createClientSideCollectionSelector('interactiveImport.books'),
    (books) => {
      return books;
    }
  );
}

const mapDispatchToProps = {
  fetchInteractiveImportBooks,
  setInteractiveImportBooksSort,
  clearInteractiveImportBooks,
  updateInteractiveImportItem,
  saveInteractiveImportItem
};

class SelectBookModalContentConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    const {
      authorId,
      mediaType
    } = this.props;

    this.props.fetchInteractiveImportBooks({ authorId, mediaType });
  }

  componentDidUpdate(prevProps) {
    const {
      authorId,
      mediaType
    } = this.props;

    if (prevProps.authorId !== authorId || prevProps.mediaType !== mediaType) {
      this.props.fetchInteractiveImportBooks({ authorId, mediaType });
    }
  }

  componentWillUnmount() {
    // This clears the books for the queue and hides the queue
    // We'll need another place to store books for manual import
    this.props.clearInteractiveImportBooks();
  }

  //
  // Listeners

  onSortPress = (sortKey, sortDirection) => {
    this.props.setInteractiveImportBooksSort({ sortKey, sortDirection });
  };

  onBookSelect = (bookId) => {
    const book = find(this.props.items, { id: bookId });

    const ids = this.props.ids;

    ids.forEach((id) => {
      this.props.updateInteractiveImportItem({
        id,
        book,
        editionId: undefined,
        foreignEditionId: undefined,
        suggestedForeignBookId: undefined,
        suggestedBookTitle: undefined,
        suggestedForeignEditionId: undefined,
        suggestedEditionTitle: undefined,
        rejections: []
      });
    });

    this.props.saveInteractiveImportItem({ ids });

    this.props.onModalClose(true);
  };

  //
  // Render

  render() {
    return (
      <SelectBookModalContent
        {...this.props}
        onSortPress={this.onSortPress}
        onBookSelect={this.onBookSelect}
      />
    );
  }
}

SelectBookModalContentConnector.propTypes = {
  ids: PropTypes.arrayOf(PropTypes.number).isRequired,
  authorId: PropTypes.number.isRequired,
  mediaType: PropTypes.string,
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  fetchInteractiveImportBooks: PropTypes.func.isRequired,
  setInteractiveImportBooksSort: PropTypes.func.isRequired,
  clearInteractiveImportBooks: PropTypes.func.isRequired,
  saveInteractiveImportItem: PropTypes.func.isRequired,
  updateInteractiveImportItem: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(SelectBookModalContentConnector);
