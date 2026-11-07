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
      currentBookDeleteInfo: null,
      siblingInfoBookId: props.bookId
    };

    this._siblingRequestGeneration = 0;
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
    this._siblingRequestGeneration++;

    if (this._abortSiblingRequest) {
      this._abortSiblingRequest();
      this._abortSiblingRequest = null;
    }
  }

  _fetchSiblingDeleteInfo = () => {
    const { bookId } = this.props;
    const generation = ++this._siblingRequestGeneration;

    if (this._abortSiblingRequest) {
      this._abortSiblingRequest();
      this._abortSiblingRequest = null;
    }

    this.setState({
      siblingBook: null,
      currentBookDeleteInfo: null,
      siblingInfoBookId: bookId
    });

    if (!bookId) {
      return;
    }

    const { request, abortRequest } = createAjaxRequest({
      url: `/book/${bookId}/siblings`
    });

    this._abortSiblingRequest = abortRequest;

    request.done((data) => {
      if (generation !== this._siblingRequestGeneration) {
        return;
      }

      this._abortSiblingRequest = null;
      const siblings = Array.isArray(data?.siblings) ? data.siblings : [];
      const hasSibling = siblings.length > 0;

      this.setState({
        siblingInfoBookId: bookId,
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
      if (generation !== this._siblingRequestGeneration || xhr?.aborted) {
        return;
      }

      this._abortSiblingRequest = null;
      // Best-effort only: modal still works without sibling info.
      this.setState({
        siblingBook: null,
        currentBookDeleteInfo: null,
        siblingInfoBookId: bookId
      });
    });
  };

  //
  // Listeners

  onDeletePress = (deleteFiles, addImportListExclusion, applyToBothFormats) => {
    this.props.deleteBook({
      id: this.props.bookId,
      mediaType: this.props.mediaType,
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
    const hasCurrentSiblingInfo = this.state.siblingInfoBookId === this.props.bookId;

    return (
      <DeleteBookModalContent
        {...this.props}
        siblingBook={hasCurrentSiblingInfo ? this.state.siblingBook : null}
        currentBookDeleteInfo={hasCurrentSiblingInfo ? this.state.currentBookDeleteInfo : null}
        onDeletePress={this.onDeletePress}
      />
    );
  }
}

DeleteBookModalContentConnector.propTypes = {
  bookId: PropTypes.number.isRequired,
  mediaType: PropTypes.oneOf(['audiobook', 'ebook']),
  authorId: PropTypes.number.isRequired,
  push: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired,
  deleteBook: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(DeleteBookModalContentConnector);
