import _ from 'lodash';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import * as bookInfiniteScrollActions from 'Store/Actions/bookInfiniteScrollActions';
import getBookIndexQuery from './getBookIndexQuery';

// Wait for a burst of book changes (a bulk edit, an author refresh) to settle before re-fetching.
const REFRESH_DELAY_MS = 500;

// Gives a book index view (Posters, Table, Overview) server-paged books, so a large library is
// loaded a page at a time instead of all at once. The wrapped view reports which item indexes it
// rendered and gets back the row count, a lookup for loaded books, and a jump target.
function createMapStateToProps() {
  return createSelector(
    (state) => state.bookIndex,
    (state) => state.bookInfiniteScroll,
    (state) => state.app.selectedMediaType,
    (bookIndex, bookInfiniteScroll, selectedMediaType) => {
      const { queryKey, queryParams } = getBookIndexQuery(bookIndex, selectedMediaType);
      const query = bookInfiniteScroll.queries[queryKey] || {};

      return {
        queryKey,
        queryParams,
        totalCount: query.totalCount,
        pages: query.pages || {},
        entities: bookInfiniteScroll.entities || {},
        pageSize: bookInfiniteScroll.pageSize,
        changeVersion: bookInfiniteScroll.changeVersion
      };
    }
  );
}

const mapDispatchToProps = {
  dispatchSetActiveQuery: bookInfiniteScrollActions.setActiveQuery,
  dispatchInvalidateQuery: bookInfiniteScrollActions.invalidateQuery,
  dispatchFetchBuckets: bookInfiniteScrollActions.fetchBookBuckets,
  dispatchFetchBooksForIndexRange: bookInfiniteScrollActions.fetchBooksForIndexRange,
  dispatchRefreshBooksQuery: bookInfiniteScrollActions.refreshBooksQuery,
  dispatchJumpToLetter: bookInfiniteScrollActions.jumpToLetter,
  dispatchAbortAllRequests: bookInfiniteScrollActions.abortAllRequests
};

function withInfiniteBooks(WrappedComponent) {
  class InfiniteBooks extends Component {

    //
    // Lifecycle

    constructor(props, context) {
      super(props, context);

      this.state = {
        scrollToIndex: null,
        dataVersion: 0
      };

      this._renderedRange = null;
      this._refreshAfterChanges = _.debounce(this.refreshRenderedRange, REFRESH_DELAY_MS);
    }

    componentDidMount() {
      this.startQuery();
    }

    componentDidUpdate(prevProps) {
      const {
        queryKey,
        jumpToCharacter,
        pages,
        entities,
        changeVersion
      } = this.props;

      if (prevProps.queryKey !== queryKey) {
        this._refreshAfterChanges.cancel();
        this._renderedRange = null;
        this.props.dispatchInvalidateQuery(prevProps.queryKey);
        this.startQuery();
      } else if (changeVersion !== prevProps.changeVersion) {
        this._refreshAfterChanges();
      }

      // Virtualized grids only redraw when their props change, so give the view a value that
      // changes whenever loaded books do.
      if (pages !== prevProps.pages || entities !== prevProps.entities) {
        this.setState((state) => ({ dataVersion: state.dataVersion + 1 }));
      }

      if (jumpToCharacter != null && jumpToCharacter !== prevProps.jumpToCharacter) {
        this.jumpToCharacter(jumpToCharacter);
      }
    }

    componentWillUnmount() {
      this._refreshAfterChanges.cancel();
      this.props.dispatchAbortAllRequests();
    }

    //
    // Control

    startQuery() {
      const {
        queryKey,
        queryParams,
        pageSize
      } = this.props;

      this.props.dispatchSetActiveQuery({ queryKey, queryParams });
      this.props.dispatchFetchBuckets({ queryKey });
      this.props.dispatchFetchBooksForIndexRange({
        queryKey,
        startIndex: 0,
        stopIndex: pageSize * 2 - 1
      });
    }

    refreshRenderedRange = () => {
      const {
        queryKey,
        pageSize
      } = this.props;

      const range = this._renderedRange || { startIndex: 0, stopIndex: pageSize - 1 };

      this.props.dispatchRefreshBooksQuery({
        queryKey,
        startIndex: range.startIndex,
        stopIndex: range.stopIndex
      });
    };

    jumpToCharacter(letter) {
      const { queryKey } = this.props;

      this.props.dispatchJumpToLetter({ queryKey, letter }).then(({ targetIndex }) => {
        // Ignore the result if the query changed while the jump was loading.
        if (queryKey === this.props.queryKey) {
          // Clear first so jumping to the same letter again still scrolls.
          this.setState({ scrollToIndex: null }, () => {
            this.setState({ scrollToIndex: targetIndex });
          });
        }
      });
    }

    getBookAtIndex = (index) => {
      const {
        pages,
        entities,
        pageSize
      } = this.props;

      const page = pages[Math.floor(index / pageSize)];
      const id = page?.ids?.[index % pageSize];

      return id ? entities[id] : undefined;
    };

    //
    // Listeners

    onRowsRendered = ({ startIndex, stopIndex }) => {
      const { queryKey } = this.props;

      this._renderedRange = { startIndex, stopIndex };
      this.props.dispatchFetchBooksForIndexRange({ queryKey, startIndex, stopIndex });
    };

    //
    // Render

    render() {
      const {
        totalCount,
        jumpToCharacter,
        queryKey,
        queryParams,
        pages,
        entities,
        pageSize,
        changeVersion,
        dispatchSetActiveQuery,
        dispatchInvalidateQuery,
        dispatchFetchBuckets,
        dispatchFetchBooksForIndexRange,
        dispatchRefreshBooksQuery,
        dispatchJumpToLetter,
        dispatchAbortAllRequests,
        ...otherProps
      } = this.props;

      return (
        <WrappedComponent
          {...otherProps}
          rowCount={totalCount ?? 0}
          getBookAtIndex={this.getBookAtIndex}
          dataVersion={this.state.dataVersion}
          scrollToIndex={this.state.scrollToIndex}
          onRowsRendered={this.onRowsRendered}
        />
      );
    }
  }

  InfiniteBooks.propTypes = {
    jumpToCharacter: PropTypes.string,
    queryKey: PropTypes.string.isRequired,
    queryParams: PropTypes.object.isRequired,
    totalCount: PropTypes.number,
    pages: PropTypes.object.isRequired,
    entities: PropTypes.object.isRequired,
    pageSize: PropTypes.number.isRequired,
    changeVersion: PropTypes.number.isRequired,
    dispatchSetActiveQuery: PropTypes.func.isRequired,
    dispatchInvalidateQuery: PropTypes.func.isRequired,
    dispatchFetchBuckets: PropTypes.func.isRequired,
    dispatchFetchBooksForIndexRange: PropTypes.func.isRequired,
    dispatchRefreshBooksQuery: PropTypes.func.isRequired,
    dispatchJumpToLetter: PropTypes.func.isRequired,
    dispatchAbortAllRequests: PropTypes.func.isRequired
  };

  return connect(createMapStateToProps, mapDispatchToProps)(InfiniteBooks);
}

export default withInfiniteBooks;
