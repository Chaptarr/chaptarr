import { createSelector } from 'reselect';

function createBookSelector() {
  return createSelector(
    (state, { bookId }) => bookId == null ? undefined : String(bookId),
    (state) => state.books?.itemMap,
    (state) => state.books?.items,
    (bookId, itemMap, allBooks) => {
      // Null-safe lookup with proper validation
      if (bookId == null || !itemMap || !Array.isArray(allBooks)) {
        return undefined;
      }
      const idx = itemMap[bookId];
      return Number.isInteger(idx) && idx >= 0 && idx < allBooks.length ?
        allBooks[idx] :
        undefined;
    }
  );
}

export default createBookSelector;
