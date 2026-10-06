import { createSelector } from 'reselect';
import createBooksClientSideCollectionSelector from './createBooksClientSideCollectionSelector';

function createBookClientSideCollectionItemsSelector(uiSection) {
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

export default createBookClientSideCollectionItemsSelector;
