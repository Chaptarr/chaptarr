import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { Redirect } from 'react-router-dom';
import { createSelector } from 'reselect';
import authorStyles from 'Author/Details/AuthorDetails.css';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import NotFound from 'Components/NotFound';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import { fetchAuthor } from 'Store/Actions/authorActions';
import { fetchBooks } from 'Store/Actions/bookActions';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';
import BookDetailsConnector from './BookDetailsConnector';

function isNumericBookRoute(value) {
  return (/^\d+$/).test((value || '').toString());
}

function getScopedMediaType(mediaScope) {
  const normalized = (mediaScope || '').toString().toLowerCase();

  if (normalized === 'ebook' || normalized === 'ebooks') {
    return 'ebook';
  }

  if (normalized === 'audiobook' || normalized === 'audiobooks') {
    return 'audiobook';
  }

  return null;
}

function createMapStateToProps() {
  return createSelector(
    (state, { match }) => match,
    (state) => state.books,
    (state) => state.authors,
    (match, books, author) => {
      const routeBookKey = match.params.bookId;
      const scopedMediaType = getScopedMediaType(match.params.mediaScope);
      const isNumericRoute = isNumericBookRoute(routeBookKey);
      const numericBookId = isNumericRoute ? parseInt(routeBookKey) : null;
      const isFetching = books.isFetching || author.isFetching;

      // Find the book if it exists
      const book = isNumericRoute ?
        books.items.find((b) => b.id === numericBookId) :
        null;
      const hasBook = !!book;
      const bookId = book?.id ?? numericBookId;
      const authorId = book?.authorId;
      const bookMediaType = (book?.mediaType || '').toString().toLowerCase();
      const siblingCount = (authorId && bookMediaType) ?
        books.items.filter((b) => b.authorId === authorId && (b.mediaType || '').toString().toLowerCase() === bookMediaType).length :
        0;

      // Check if we need to fetch data
      const needsBookFetch = !isFetching && !hasBook && !!routeBookKey;
      const needsAuthorFetch = authorId && !author.items.find((a) => a.id === authorId);
      return {
        match,
        routeBookKey,
        routeUsesScopedPrefix: !!scopedMediaType,
        scopedMediaType,
        bookId,
        authorId,
        book,
        bookMediaType,
        siblingCount,
        needsBookFetch,
        needsAuthorFetch,
        authorError: needsAuthorFetch ? author.error : null,
        isFetching,
        isPopulated: hasBook && !needsAuthorFetch
      };
    }
  );
}

const mapDispatchToProps = {
  fetchBooks,
  fetchAuthor
};

class BookDetailsPageConnector extends Component {

  constructor(props) {
    super(props);
    this.state = { hasMounted: false };
    this._bookRequest = null;
    this._lastAuthorFetchId = null;
    this._lastSiblingFetchKey = null;
  }
  //
  // Lifecycle

  componentDidMount() {
    this._isMounted = true;
    this.populate();
  }

  componentDidUpdate(prevProps) {
    if (this.props.routeBookKey !== prevProps.routeBookKey ||
        this.props.scopedMediaType !== prevProps.scopedMediaType) {
      this._bookRequest = null;
      this._lastAuthorFetchId = null;
      this._lastSiblingFetchKey = null;
    }

    this.populate();
  }

  componentWillUnmount() {
    this._isMounted = false;
  }

  //
  // Control

  populate = () => {
    const {
      routeBookKey,
      scopedMediaType,
      needsBookFetch,
      needsAuthorFetch,
      authorId,
      book,
      bookMediaType,
      siblingCount,
      isFetching
    } = this.props;

    if (needsBookFetch && routeBookKey && !this._bookRequest) {
      const pending = {};
      this._bookRequest = pending;
      // Fetch the specific book data. bookId may be either the local numeric id
      // or a Readarr-compatible titleSlug from an external service link.
      const fetchParams = { bookId: routeBookKey.toString() };

      if (scopedMediaType) {
        fetchParams.mediaType = scopedMediaType;
      }

      this.props.fetchBooks({
        ...fetchParams,
        onComplete: (error, data) => {
          if (!this._isMounted || this._bookRequest !== pending) {
            return;
          }

          // A superseded request did not establish whether this book exists.
          this._bookRequest = error?.aborted ? null : {
            error,
            routeBookKey,
            scopedMediaType,
            resolvedBookId: !error && !isNumericBookRoute(routeBookKey) ? data?.[0]?.id : null
          };
          this.setState({ hasMounted: true });
        }
      });
    }

    if (needsAuthorFetch && authorId && this._lastAuthorFetchId !== authorId && !isFetching) {
      this._lastAuthorFetchId = authorId;
      // Fetch the author data
      this.props.fetchAuthor({ id: authorId });
    }

    // Deep-link safety net: the book details connector computes previous/next from
    // books in the Redux store. When a book page is loaded directly, we only have
    // a single book in-state; fetch siblings for navigation.
    if (book && authorId && bookMediaType && siblingCount < 2 && !isFetching) {
      const mediaType = bookMediaType;
      const key = `${authorId}_${mediaType}`;

      if (this._lastSiblingFetchKey !== key) {
        this._lastSiblingFetchKey = key;
        this.props.fetchBooks({ authorId, mediaType });
      }
    }

    if (!this.state.hasMounted) {
      this.setState({ hasMounted: true });
    }
  };

  //
  // Render

  render() {
    const {
      bookId,
      routeUsesScopedPrefix,
      isFetching,
      isPopulated
    } = this.props;

    if (!this.props.routeBookKey) {
      return (
        <NotFound
          message={translate('SorryThatBookCannotBeFound')}
        />
      );
    }

    const resolvedBookId = this._bookRequest?.routeBookKey === this.props.routeBookKey &&
      this._bookRequest?.scopedMediaType === this.props.scopedMediaType ? this._bookRequest.resolvedBookId : null;

    if (resolvedBookId || (routeUsesScopedPrefix && bookId)) {
      return (
        <Redirect
          to={`/book/${resolvedBookId || bookId}`}
        />
      );
    }

    if (isFetching || !this.state.hasMounted) {
      return (
        <PageContent title={translate('Loading')}>
          <PageContentBody>
            <LoadingIndicator />
          </PageContentBody>
        </PageContent>
      );
    }

    if (isPopulated && bookId) {
      return (
        <BookDetailsConnector
          bookId={bookId}
        />
      );
    }

    const error = this._bookRequest?.error || this.props.authorError;
    if (error && error.status !== 404) {
      return (
        <div className={authorStyles.errorMessage}>
          {getErrorMessage(error, translate('NarratorDetailsUnableToLoadBooks'))}
        </div>
      );
    }

    return (
      <NotFound
        message={translate('SorryThatBookCannotBeFound')}
      />
    );
  }
}

BookDetailsPageConnector.propTypes = {
  routeBookKey: PropTypes.string,
  routeUsesScopedPrefix: PropTypes.bool.isRequired,
  scopedMediaType: PropTypes.string,
  bookId: PropTypes.number,
  authorId: PropTypes.number,
  book: PropTypes.object,
  bookMediaType: PropTypes.string,
  siblingCount: PropTypes.number,
  needsBookFetch: PropTypes.bool,
  needsAuthorFetch: PropTypes.bool,
  authorError: PropTypes.object,
  match: PropTypes.shape({ params: PropTypes.shape({ bookId: PropTypes.string.isRequired }).isRequired }).isRequired,
  fetchBooks: PropTypes.func.isRequired,
  fetchAuthor: PropTypes.func.isRequired,
  isFetching: PropTypes.bool.isRequired,
  isPopulated: PropTypes.bool.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(BookDetailsPageConnector);
