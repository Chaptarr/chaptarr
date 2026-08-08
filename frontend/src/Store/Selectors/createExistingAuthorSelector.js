import _ from 'lodash';
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

      return _.find(authors, (a) => a.foreignAuthorId === foreignAuthorId) || null;
    }
  );
}

export default createExistingAuthorSelector;
