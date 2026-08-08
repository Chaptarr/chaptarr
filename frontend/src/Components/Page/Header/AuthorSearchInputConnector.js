import { push } from 'connected-react-router';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { showMessage } from 'Store/Actions/appActions';
import AuthorSearchInput from './AuthorSearchInput';

function createMapStateToProps() {
  return createSelector(
    (state) => state.settings.hardcoverConfig,
    (hardcoverConfig) => {
      const { isPopulated, error, item } = hardcoverConfig;
      let isHardcoverConfigured = null;

      if (isPopulated || error) {
        isHardcoverConfigured = !!(item?.enabled && item?.hasToken);
      }

      return {
        isHardcoverConfigured
      };
    }
  );
}

function createMapDispatchToProps(dispatch, props) {
  return {
    onGoToAuthor(authorId) {
      dispatch(push(`${window.Chaptarr.urlBase}/author/${authorId}`));
    },

    onGoToBook(bookId) {
      dispatch(push(`${window.Chaptarr.urlBase}/book/${bookId}`));
    },

    onGoToAddNewAuthor(query, provider = 'hardcover') {
      const encodedTerm = encodeURIComponent(query);
      const encodedProvider = encodeURIComponent(provider);
      dispatch(push(`${window.Chaptarr.urlBase}/add/search?term=${encodedTerm}&provider=${encodedProvider}`));
    },

    onHardcoverSetupRequired() {
      dispatch(showMessage({
        id: `hardcover-setup-${Date.now()}`,
        message: 'Hardcover search is disabled until you connect your Hardcover account in Quickstart.',
        type: 'warning',
        hideAfter: 10,
        clickable: true,
        onClick: () => {
          dispatch(push(`${window.Chaptarr.urlBase}/system/quickstart`));
        }
      }));
    }
  };
}

export default connect(createMapStateToProps, createMapDispatchToProps)(AuthorSearchInput);
