import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import createDimensionsSelector from 'Store/Selectors/createDimensionsSelector';
import createExistingAuthorSelector from 'Store/Selectors/createExistingAuthorSelector';
import AddNewAuthorSearchResult from './AddNewAuthorSearchResult';

function createMapStateToProps() {
  return createSelector(
    createExistingAuthorSelector(),
    createDimensionsSelector(),
    (state, ownProps) => ownProps.id,
    (existingAuthor, dimensions, authorResourceId) => {
      // existingAuthor: found via foreignAuthorId match in Redux store (same-provider)
      // authorResourceId > 0: backend found match via provider ID cross-reference (cross-provider)
      const isExisting = !!existingAuthor || (authorResourceId > 0);
      return {
        isExistingAuthor: isExisting,
        authorId: existingAuthor ? existingAuthor.id : (authorResourceId > 0 ? authorResourceId : null),
        isSmallScreen: dimensions.isSmallScreen
      };
    }
  );
}

export default connect(createMapStateToProps)(AddNewAuthorSearchResult);
