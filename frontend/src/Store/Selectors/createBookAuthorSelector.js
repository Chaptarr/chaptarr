import { createSelector } from 'reselect';
import createBookSelector from './createBookSelector';

function createBookAuthorSelector() {
  return createSelector(
    createBookSelector(),
    (state) => state.authors.itemMap,
    (state) => state.authors.items,
    (book, authorMap, allAuthors) => {
      if (!book || book.authorId == null) return undefined;
      const idx = authorMap?.[book.authorId];
      return idx != null ? allAuthors[idx] : undefined;
    }
  );
}

export default createBookAuthorSelector;
