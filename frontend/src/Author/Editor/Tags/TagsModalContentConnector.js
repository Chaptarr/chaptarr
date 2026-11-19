import _ from 'lodash';
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
    (state, { selectedMediaType }) => selectedMediaType,
    (authorIds, allAuthors, tagList, selectedMediaType) => {
      const author = _.intersectionWith(allAuthors, authorIds, (s, id) => {
        return s.id === id;
      });

      const field = ({ audiobook: 'audiobookTags', ebook: 'ebookTags' })[selectedMediaType] || 'tags';
      const authorTags = _.uniq(author.flatMap((item) => item[field] ?? item.tags ?? []));

      return {
        authorTags,
        tagList
      };
    }
  );
}

export default connect(createMapStateToProps)(TagsModalContent);
