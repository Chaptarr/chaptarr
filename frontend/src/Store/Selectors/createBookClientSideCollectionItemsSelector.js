import { createSelector, createSelectorCreator, defaultMemoize } from 'reselect';
import hasDifferentItemsOrOrder from 'Utilities/Object/hasDifferentItemsOrOrder';
import createBooksClientSideCollectionSelector from './createBooksClientSideCollectionSelector';

function createUnoptimizedSelector(uiSection) {
  return createSelector(
    createBooksClientSideCollectionSelector(uiSection),
    (books) => {
      const items = books.items.map((s) => {
        const {
          id,
          title,
          authorTitle
        } = s;

        return {
          id,
          title,
          authorTitle
        };
      });

      return {
        ...books,
        items
      };
    }
  );
}

function bookListEqual(a, b) {
  // Reselect equality functions must return true when the inputs are EQUAL
  // (reuse the cached result). hasDifferentItemsOrOrder returns true when
  // they DIFFER, so it must be negated here or filtering/sorting reuses the
  // stale unfiltered collection.
  return !hasDifferentItemsOrOrder(a, b);
}

const createBookEqualSelector = createSelectorCreator(
  defaultMemoize,
  bookListEqual
);

function createBookClientSideCollectionItemsSelector(uiSection) {
  return createBookEqualSelector(
    createUnoptimizedSelector(uiSection),
    (book) => book
  );
}

export default createBookClientSideCollectionItemsSelector;
