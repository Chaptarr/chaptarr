import { createSelector, createSelectorCreator, defaultMemoize } from 'reselect';
import hasDifferentItemsOrOrder from 'Utilities/Object/hasDifferentItemsOrOrder';
import createClientSideCollectionSelector from './createClientSideCollectionSelector';

function createUnoptimizedSelector(uiSection) {
  return createSelector(
    createClientSideCollectionSelector('authors', uiSection),
    (authors) => {
      // Return all author properties, not just a subset
      // This is needed for filtering by audiobookRootFolderPath and ebookRootFolderPath
      return authors;
    }
  );
}

function authorListEqual(a, b) {
  // Reselect equality functions must return true when the inputs are EQUAL
  // (reuse the cached result). hasDifferentItemsOrOrder returns true when
  // they DIFFER, so it must be negated here or filtering/sorting reuses the
  // stale unfiltered collection.
  return !hasDifferentItemsOrOrder(a, b);
}

const createAuthorEqualSelector = createSelectorCreator(
  defaultMemoize,
  authorListEqual
);

function createAuthorClientSideCollectionItemsSelector(uiSection) {
  return createAuthorEqualSelector(
    createUnoptimizedSelector(uiSection),
    (author) => author
  );
}

export default createAuthorClientSideCollectionItemsSelector;
