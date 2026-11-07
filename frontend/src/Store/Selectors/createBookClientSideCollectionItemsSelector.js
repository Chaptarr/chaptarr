import { createSelector, createSelectorCreator, lruMemoize } from 'reselect';
import clientSideCollectionEqual from 'Utilities/Object/clientSideCollectionEqual.cjs';
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

const createBookEqualSelector = createSelectorCreator(
  lruMemoize,
  clientSideCollectionEqual
);

function createBookClientSideCollectionItemsSelector(uiSection) {
  return createBookEqualSelector(
    createUnoptimizedSelector(uiSection),
    (book) => book
  );
}

export default createBookClientSideCollectionItemsSelector;
