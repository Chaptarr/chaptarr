/* eslint max-params: 0 */
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import createAuthorSelector from 'Store/Selectors/createAuthorSelector';
import BookRow from './BookRow';

const selectBookFiles = createSelector(
  (state) => state.bookFiles,
  (bookFiles) => {
    const { items } = bookFiles;

    return items.reduce((acc, file) => {
      const bookId = file.bookId;
      if (!acc.hasOwnProperty(bookId)) {
        acc[bookId] = [];
      }

      acc[bookId].push(file);

      return acc;
    }, {});
  }
);

function createMapStateToProps() {
  return createSelector(
    createAuthorSelector(),
    selectBookFiles,
    (state, { id }) => (state.queue.details.items || []).find((item) => item.book && item.book.id === id),
    (state, { id }) => id,
    (state, { id }) => state.books.items.find((book) => book.id === id),
    (state, { selectedMediaType }) => selectedMediaType,
    (author = {}, bookFiles, queueItem, bookId, book, selectedMediaTypeProp) => {
      const allFiles = bookFiles[bookId] ?? [];
      let selectedMediaType = selectedMediaTypeProp;

      if (!selectedMediaType) {
        if (process.env.NODE_ENV !== 'production') {
          console.warn('[BookRowConnector] Missing selectedMediaType prop; defaulting to audiobook', {
            authorId: author.id,
            bookId
          });
        }

        selectedMediaType = 'audiobook';
      }

      // Filter files by selected media type
      const files = allFiles.filter((file) => file.mediaType === selectedMediaType);
      const bookFile = files[0];

      return {
        authorName: author.authorName,
        audiobookMonitored: !!book?.audiobookMonitored,
        ebookMonitored: !!book?.ebookMonitored,
        bookFiles: files,
        grabbed: !!book?.grabbed,
        indexerFlags: bookFile ? bookFile.indexerFlags : 0,
        narrator: book?.narrator,
        queueItem,
        selectedMediaType
      };
    }
  );
}

const mapDispatchToProps = undefined;

function mergeProps(stateProps, dispatchProps, ownProps) {
  return {
    ...ownProps,
    ...stateProps
  };
}

export default connect(createMapStateToProps, mapDispatchToProps, mergeProps)(BookRow);
