import { push } from 'connected-react-router';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { showMessage } from 'Store/Actions/appActions';
import { fetchMetadataProvider } from 'Store/Actions/settingsActions';
import translate from 'Utilities/String/translate';
import AuthorSearchInput from './AuthorSearchInput';

function createMapStateToProps() {
  return createSelector(
    (state) => state.settings.hardcoverConfig,
    (state) => state.settings.metadataProvider,
    (hardcoverConfig, metadataProviderConfig) => {
      const { isPopulated, error, item } = hardcoverConfig;
      let isHardcoverConfigured = null;

      if (isPopulated || error) {
        const metadataProviderItem = metadataProviderConfig?.item || {};
        const legacyCatalogEnabled = metadataProviderConfig?.isPopulated &&
          !metadataProviderConfig.error && metadataProviderItem.enableHardcover?.value;
        const hasHardcoverToken = item?.hasToken || metadataProviderItem.hasHardcoverAuth?.value;
        isHardcoverConfigured = !!((item?.enabled || legacyCatalogEnabled) && hasHardcoverToken);
      }

      return {
        isHardcoverConfigured,
        metadataProviderConfig
      };
    }
  );
}

function createMapDispatchToProps(dispatch, props) {
  return {
    fetchMetadataProvider() {
      dispatch(fetchMetadataProvider());
    },

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
        message: translate('HardcoverSearchRequiresQuickstart'),
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
