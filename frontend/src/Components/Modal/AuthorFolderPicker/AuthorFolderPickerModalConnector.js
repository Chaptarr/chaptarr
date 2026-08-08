import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { linkAuthorToFolder } from 'Store/Actions/authorActions';
import AuthorFolderPickerModalContent from './AuthorFolderPickerModalContent';

function createMapStateToProps() {
  return createSelector(
    (state, { authorId }) => state.authors.items.find((a) => a.id === authorId),
    (state) => state.authors.isLinking,
    (author, isLinking) => {
      return {
        authorName: author ? author.authorName : '',
        isLinking
      };
    }
  );
}

const mapDispatchToProps = {
  linkAuthorToFolder
};

export default connect(createMapStateToProps, mapDispatchToProps)(AuthorFolderPickerModalContent);