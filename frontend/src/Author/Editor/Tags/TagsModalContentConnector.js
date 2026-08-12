import concat from 'lodash/concat';
import intersectionWith from 'lodash/intersectionWith';
import map from 'lodash/map';
import uniq from 'lodash/uniq';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import createAllAuthorSelector from 'Store/Selectors/createAllAuthorsSelector';
import createTagsSelector from 'Store/Selectors/createTagsSelector';
import TagsModalContent from './TagsModalContent';

function createMapStateToProps() {
  return createSelector(
    (state, { authorIds }) => authorIds,
    createAllAuthorSelector(),
    createTagsSelector(),
    (authorIds, allAuthors, tagList) => {
      const author = intersectionWith(allAuthors, authorIds, (s, id) => {
        return s.id === id;
      });

      const authorTags = uniq(concat(...map(author, 'tags')));

      return {
        authorTags,
        tagList
      };
    }
  );
}

function createMapDispatchToProps(dispatch, props) {
  return {
    onAction() {
      // Do something
    }
  };
}

export default connect(createMapStateToProps, createMapDispatchToProps)(TagsModalContent);
