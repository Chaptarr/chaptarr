import intersectionWith from 'lodash/intersectionWith';
import map from 'lodash/map';
import orderBy from 'lodash/orderBy';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { bulkDeleteAuthor } from 'Store/Actions/authorIndexActions';
import createAllAuthorSelector from 'Store/Selectors/createAllAuthorsSelector';
import DeleteAuthorModalContent from './DeleteAuthorModalContent';

function createMapStateToProps() {
  return createSelector(
    (state, { authorIds }) => authorIds,
    createAllAuthorSelector(),
    (authorIds, allAuthors) => {
      const selectedAuthor = intersectionWith(allAuthors, authorIds, (s, id) => {
        return s.id === id;
      });

      const sortedAuthor = orderBy(selectedAuthor, 'sortName');
      const author = map(sortedAuthor, (s) => {
        return {
          authorName: s.authorName,
          path: s.path
        };
      });

      return {
        author
      };
    }
  );
}

function createMapDispatchToProps(dispatch, props) {
  return {
    onDeleteSelectedPress(deleteFiles) {
      dispatch(bulkDeleteAuthor({
        authorIds: props.authorIds,
        deleteFiles
      }));

      props.onModalClose();
    }
  };
}

export default connect(createMapStateToProps, createMapDispatchToProps)(DeleteAuthorModalContent);
