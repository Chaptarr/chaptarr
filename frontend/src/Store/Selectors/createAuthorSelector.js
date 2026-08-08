import { createSelector } from 'reselect';

function createAuthorSelector() {
  return createSelector(
    (state, { authorId }) => authorId,
    (state) => state.authors.itemMap,
    (state) => state.authors.items,
    (authorId, itemMap, allAuthors) => {
      if (!itemMap || itemMap[authorId] === undefined) {
        return undefined;
      }
      return allAuthors[itemMap[authorId]];
    }
  );
}

export default createAuthorSelector;
