import createClientSideCollectionSelector from './createClientSideCollectionSelector';

function createAuthorClientSideCollectionItemsSelector(uiSection) {
  return createClientSideCollectionSelector('authors', uiSection);
}

export default createAuthorClientSideCollectionItemsSelector;
