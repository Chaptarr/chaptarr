import _ from 'lodash';
import { createSelector } from 'reselect';
import createAllAuthorsSelector from './createAllAuthorsSelector';

function createExistingAuthorSelector() {
  return createSelector(
    (state, { authorId, id }) => authorId || id,
    (state, { foreignAuthorId }) => foreignAuthorId,
    createAllAuthorsSelector(),
    (authorId, foreignAuthorId, authors) => {
      if (authorId > 0) {
        return _.find(authors, (author) => author.id === authorId) || null;
      }

      return foreignAuthorId ? _.find(authors, (author) => author.foreignAuthorId === foreignAuthorId) || null : null;
    }
  );
}

export default createExistingAuthorSelector;
