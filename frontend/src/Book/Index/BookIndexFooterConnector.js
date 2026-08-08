import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import createClientSideCollectionSelector from 'Store/Selectors/createClientSideCollectionSelector';
import createDeepEqualSelector from 'Store/Selectors/createDeepEqualSelector';
import BookIndexFooter from './BookIndexFooter';
import getBookIndexQuery from './getBookIndexQuery';

function createUnoptimizedSelector() {
  return createSelector(
    createClientSideCollectionSelector('books', 'bookIndex'),
    (books) => {
      return books.items.map((s) => {
        const {
          authorId,
          monitored,
          status,
          statistics
        } = s;

        return {
          authorId,
          monitored,
          status,
          statistics
        };
      });
    }
  );
}

function createBookSelector() {
  return createDeepEqualSelector(
    createUnoptimizedSelector(),
    (book) => book
  );
}

function buildClientSideFooterStatistics(book) {
  const authorIds = new Set();
  let monitoredBooks = 0;
  let fileCount = 0;
  let totalFileSize = 0;

  book.forEach((item) => {
    if (item.authorId > 0) {
      authorIds.add(item.authorId);
    }

    if (item.monitored) {
      monitoredBooks += 1;
    }

    const statistics = item.statistics || {};
    fileCount += statistics.bookFileCount || 0;
    totalFileSize += statistics.sizeOnDisk || 0;
  });

  return {
    totalBooks: book.length,
    monitoredBooks,
    fileCount,
    totalFileSize,
    authorCount: authorIds.size
  };
}

function createMapStateToProps() {
  return createSelector(
    createBookSelector(),
    (state) => state.bookIndex,
    (state) => state.bookInfiniteScroll,
    (state) => state.app.selectedMediaType,
    (book, bookIndex, bookInfiniteScroll, selectedMediaType) => {
      const view = bookIndex.view || 'posters';

      if (view === 'posters') {
        const { queryKey, useClientSidePosters } = getBookIndexQuery(bookIndex, selectedMediaType);

        if (useClientSidePosters) {
          return {
            statistics: buildClientSideFooterStatistics(book),
            isFetchingStatistics: false
          };
        }

        const query = bookInfiniteScroll?.queries?.[queryKey];
        const hasLoadingPage = Object.values(query?.pages || {}).some((page) => page.status === 'loading');
        const isFetchingStatistics = !query?.footerStatistics &&
          (!query || query?.buckets?.status === 'loading' || hasLoadingPage || query?.totalCount == null);

        return {
          statistics: query?.footerStatistics || {},
          isFetchingStatistics
        };
      }

      return {
        statistics: buildClientSideFooterStatistics(book),
        isFetchingStatistics: false
      };
    }
  );
}

export default connect(createMapStateToProps)(BookIndexFooter);
