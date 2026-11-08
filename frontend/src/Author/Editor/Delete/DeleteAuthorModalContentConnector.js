import _ from 'lodash';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import DeleteAuthorModalContent from 'Author/Delete/DeleteAuthorModalContent';
import { bulkDeleteAuthor } from 'Store/Actions/authorIndexActions';
import createAllAuthorSelector from 'Store/Selectors/createAllAuthorsSelector';

function createMapStateToProps() {
  return createSelector(
    (state, { authorIds }) => authorIds,
    createAllAuthorSelector(),
    (state) => state.authorIndex,
    (authorIds, allAuthors, { isDeleting, deleteError }) => ({
      authors: _.orderBy(allAuthors.filter((author) => authorIds.includes(author.id)), 'sortName'),
      isDeleting,
      deleteError
    })
  );
}

function createMapDispatchToProps(dispatch, props) {
  return {
    onDeletePress({ readdAuthor, ...options }) {
      dispatch(bulkDeleteAuthor({ authorIds: props.authorIds, ...options }))
        .done(() => props.onModalClose());
    }
  };
}

export default connect(createMapStateToProps, createMapDispatchToProps)(DeleteAuthorModalContent);
