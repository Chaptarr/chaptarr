import { createSelector, createSelectorCreator, lruMemoize } from 'reselect';
import clientSideCollectionEqual from 'Utilities/Object/clientSideCollectionEqual.cjs';
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

const createAuthorEqualSelector = createSelectorCreator(
  lruMemoize,
  clientSideCollectionEqual
);

function createAuthorClientSideCollectionItemsSelector(uiSection) {
  return createAuthorEqualSelector(
    createUnoptimizedSelector(uiSection),
    (author) => author
  );
}

export default createAuthorClientSideCollectionItemsSelector;
