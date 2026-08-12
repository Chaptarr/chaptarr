import get from 'lodash/get';
import { createSelector } from 'reselect';
import filterCollection from 'Utilities/Array/filterCollection';
import sortCollection from 'Utilities/Array/sortCollection';
import createCustomFiltersSelector from './createCustomFiltersSelector';

function createBooksClientSideCollectionSelector(uiSection) {
  return createSelector(
    (state) => get(state, 'books'),
    (state) => get(state, 'authors'),
    (state) => get(state, uiSection),
    createCustomFiltersSelector('books', uiSection),
    (bookState, authorState, uiSectionState = {}, customFilters) => {
      const state = Object.assign({}, bookState, uiSectionState, { customFilters });

      const books = state.items.map((book) => ({
        ...book,
        author: authorState.items[authorState.itemMap[book.authorId]]
      }));

      const filtered = filterCollection(books, state);
      const sorted = sortCollection(filtered, state);

      return {
        ...bookState,
        ...uiSectionState,
        customFilters,
        items: sorted,
        totalItems: state.items.length
      };
    }
  );
}

export default createBooksClientSideCollectionSelector;
