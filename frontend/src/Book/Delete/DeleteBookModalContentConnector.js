import { push } from 'connected-react-router';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { deleteBook } from 'Store/Actions/bookActions';
import createBookSelector from 'Store/Selectors/createBookSelector';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import DeleteBookModalContent from './DeleteBookModalContent';

function createMapStateToProps() {
  return createSelector(
    createBookSelector(),
    (book) => book || {}
  );
}

const mapDispatchToProps = {
  push,
  deleteBook
};

class DeleteBookModalContentConnector extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      siblingBook: null,
      currentBookDeleteInfo: null
    };

    this._abortSiblingRequest = null;
  }

  componentDidMount() {
    this._fetchSiblingDeleteInfo();
  }

  componentDidUpdate(prevProps) {
    if (prevProps.bookId !== this.props.bookId) {
      this._fetchSiblingDeleteInfo();
    }
  }

  componentWillUnmount() {
    if (this._abortSiblingRequest) {
      this._abortSiblingRequest();
      this._abortSiblingRequest = null;
    }
  }

  _fetchSiblingDeleteInfo = () => {
    const { bookId } = this.props;

    if (this._abortSiblingRequest) {
      this._abortSiblingRequest();
      this._abortSiblingRequest = null;
    }

    this.setState({ siblingBook: null, currentBookDeleteInfo: null });

    if (!bookId) {
      return;
    }

    const { request, abortRequest } = createAjaxRequest({
      url: `/book/${bookId}/siblings`
    });

    this._abortSiblingRequest = abortRequest;

    request.done((data) => {
      const siblings = Array.isArray(data?.siblings) ? data.siblings : [];
      const hasSibling = siblings.length > 0;

      this.setState({
        currentBookDeleteInfo: data?.currentBook ?? null,
        siblingBook: hasSibling ? {
          mediaType: data?.siblingMediaType,
          currentBook: data?.currentBook ?? null,
          siblings,
          statistics: data?.statistics ?? { bookFileCount: 0, sizeOnDisk: 0 },
          audiobookCount: data?.audiobookCount ?? 0,
          ebookCount: data?.ebookCount ?? 0
        } : null
      });
    });

    request.fail((xhr) => {
      if (xhr?.aborted) {
        return;
      }

      // Best-effort only: modal still works without sibling info.
      this.setState({ siblingBook: null, currentBookDeleteInfo: null });
    });
  };

  //
  // Listeners

  onDeletePress = (deleteFiles, addImportListExclusion, applyToBothFormats) => {
    this.props.deleteBook({
      id: this.props.bookId,
      deleteFiles,
      addImportListExclusion,
      applyToBothFormats
    });

    this.props.onModalClose(true);

    this.props.push(`${window.Chaptarr.urlBase}/author/${this.props.authorId}`);
  };

  //
  // Render

  render() {
    return (
      <DeleteBookModalContent
        {...this.props}
        siblingBook={this.state.siblingBook}
        currentBookDeleteInfo={this.state.currentBookDeleteInfo}
        onDeletePress={this.onDeletePress}
      />
    );
  }
}

DeleteBookModalContentConnector.propTypes = {
  bookId: PropTypes.number.isRequired,
  authorId: PropTypes.number.isRequired,
  push: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired,
  deleteBook: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(DeleteBookModalContentConnector);
