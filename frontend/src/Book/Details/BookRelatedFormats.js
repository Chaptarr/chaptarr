import PropTypes from 'prop-types';
import React, { Component } from 'react';
import BookFormatActions from 'Components/Book/BookFormatActions';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import AddNewBookModal from 'Search/Book/AddNewBookModal';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import translate from 'Utilities/String/translate';
import {
  buildAddFormatModalProps,
  canAddFormat,
  getBookFormatInstances,
  haveRelatedFormatInputsChanged,
  shouldShowFormatIdentityHelp
} from './BookRelatedFormats.cjs';
import styles from './BookRelatedFormats.css';

class BookRelatedFormats extends Component {
  constructor(props, context) {
    super(props, context);

    this.state = {
      formats: [],
      hasLoaded: false,
      hasError: false,
      addModalProps: null
    };
    this._abortRequest = null;
    this._requestGeneration = 0;
  }

  componentDidMount() {
    this.fetchRelatedFormats();
  }

  componentDidUpdate(prevProps) {
    if (haveRelatedFormatInputsChanged(prevProps, this.props)) {
      this.fetchRelatedFormats();
    }
  }

  componentWillUnmount() {
    this._requestGeneration++;

    if (this._abortRequest) {
      this._abortRequest();
      this._abortRequest = null;
    }
  }

  fetchRelatedFormats = () => {
    const { bookId } = this.props;
    const generation = ++this._requestGeneration;

    if (this._abortRequest) {
      this._abortRequest();
      this._abortRequest = null;
    }

    this.setState({ formats: [], hasLoaded: false, hasError: false });

    if (!bookId) {
      return;
    }

    const { request, abortRequest } = createAjaxRequest({
      url: `/book/${bookId}/related-formats`
    });

    this._abortRequest = abortRequest;

    request.done((data) => {
      if (generation !== this._requestGeneration) {
        return;
      }

      this._abortRequest = null;
      this.setState({
        formats: Array.isArray(data?.formats) ? data.formats : [],
        hasLoaded: true,
        hasError: false
      });
    });

    request.fail((xhr) => {
      if (generation !== this._requestGeneration) {
        return;
      }

      this._abortRequest = null;
      if (!xhr?.aborted) {
        this.setState({ formats: [], hasLoaded: false, hasError: true });
      }
    });
  };

  onAddFormatPress = (mediaType) => {
    const { book, author } = this.props;
    const instances = getBookFormatInstances(this.state.formats, {
      ...book,
      author,
      mediaType: book?.mediaType || this.props.mediaType
    });

    this.setState({
      addModalProps: buildAddFormatModalProps(book, author, instances, mediaType)
    });
  };

  onAddModalClose = () => {
    this.setState({ addModalProps: null }, this.fetchRelatedFormats);
  };

  onRetryPress = () => {
    this.fetchRelatedFormats();
  };

  render() {
    const { book, author, mediaType, title } = this.props;
    const { addModalProps, hasError, hasLoaded } = this.state;
    const currentBook = { ...book, author, mediaType: book?.mediaType || mediaType };
    const formatInstances = getBookFormatInstances(this.state.formats, currentBook);
    const canAdd = canAddFormat(book, author);
    const showFormatIdentityHelp = shouldShowFormatIdentityHelp(book, author, formatInstances, mediaType);

    if (hasError) {
      return (
        <div className={styles.relatedFormats}>
          <span className={styles.label}>{translate('Formats')}</span>
          <span role="alert">{translate('LoadingRelatedFormatsFailed')}</span>
          <Button onPress={this.onRetryPress}>
            {translate('Retry')}
          </Button>
        </div>
      );
    }

    // Wait for the server's work-level match check before showing an add action.
    // This prevents a temporary empty response from suggesting a duplicate format.
    if (!hasLoaded) {
      return (
        <div
          className={styles.relatedFormats}
          aria-busy="true"
        >
          <span className={styles.label}>{translate('Formats')}</span>
          <LoadingIndicator size={20} />
          <span className={styles.status} role="status">
            {translate('CheckingRelatedFormats')}
          </span>
        </div>
      );
    }

    return (
      <div className={styles.relatedFormats}>
        <span className={styles.label}>{translate('Formats')}</span>
        <BookFormatActions
          title={title}
          localAudiobookBooks={formatInstances.localAudiobookBooks}
          localEbookBooks={formatInstances.localEbookBooks}
          canAdd={canAdd}
          showAddActions={true}
          showLabels={true}
          onAdd={this.onAddFormatPress}
        />
        {
          showFormatIdentityHelp ?
            <span className={styles.formatIdentityHelp} role="status">
              {translate('FormatAddRequiresCatalogIdsHelpText')}
            </span> :
            null
        }
        {
          addModalProps ?
            <AddNewBookModal
              isOpen={true}
              {...addModalProps}
              onModalClose={this.onAddModalClose}
            /> :
            null
        }
      </div>
    );
  }
}

BookRelatedFormats.propTypes = {
  bookId: PropTypes.number.isRequired,
  mediaType: PropTypes.oneOf(['audiobook', 'ebook']).isRequired,
  title: PropTypes.string.isRequired,
  book: PropTypes.object.isRequired,
  author: PropTypes.object.isRequired
};

export default BookRelatedFormats;
