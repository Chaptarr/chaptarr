import find from 'lodash/find';
import { createSelector } from 'reselect';
import createAllAuthorsSelector from './createAllAuthorsSelector';

function createExistingAuthorSelector() {
  return createSelector(
    (state, { foreignAuthorId }) => foreignAuthorId,
    createAllAuthorsSelector(),
    (foreignAuthorId, authors) => {
      if (!foreignAuthorId) {
        return null;
      }

      return find(authors, (a) => a.foreignAuthorId === foreignAuthorId) || null;
    }
  );
}

export default createExistingAuthorSelector;
