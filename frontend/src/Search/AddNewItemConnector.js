import { push } from 'connected-react-router';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import seriesEnrichmentService from 'Services/SeriesEnrichmentService';
import { clearSearchResults, getSearchResults, updateSeriesEnrichment } from 'Store/Actions/searchActions';
import { fetchMetadataProvider, fetchRootFolders } from 'Store/Actions/settingsActions';
import parseUrl from 'Utilities/String/parseUrl';
import AddNewItem from './AddNewItem';

function createMapStateToProps() {
  return createSelector(
    (state) => state.search,
    (state) => state.authors.items.length,
    (state) => state.router.location,
    (state) => state.settings.hardcoverConfig,
    (state) => state.settings.metadataProvider,
    (search, existingAuthorsCount, location, hardcoverConfig, metadataProviderConfig) => {
      const { params } = parseUrl(location.search);
      const { isPopulated, error, item } = hardcoverConfig;
      const metadataProviderItem = metadataProviderConfig?.item || {};
      const isLegacyHardcoverCatalogEnabled = metadataProviderConfig?.isPopulated &&
        !metadataProviderConfig.error && metadataProviderItem.enableHardcover?.value;
      const hasHardcoverToken = item?.hasToken || metadataProviderItem.hasHardcoverAuth?.value;
      const isHardcoverConfigured = (!isPopulated && !error) ?
        null :
        !!((item?.enabled || isLegacyHardcoverCatalogEnabled) && hasHardcoverToken);

      return {
        ...search,
        term: params.term,
        provider: params.provider,
        hasExistingAuthors: existingAuthorsCount > 0,
        isHardcoverConfigured,
        metadataProviderConfig
      };
    }
  );
}

const mapDispatchToProps = {
  getSearchResults,
  clearSearchResults,
  fetchRootFolders,
  fetchMetadataProvider,
  updateSeriesEnrichment,
  push
};

class AddNewItemConnector extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this._searchTimeout = null;
    this._pendingSearch = null;
  }

  componentDidMount() {
    this.props.fetchRootFolders();
    this.props.fetchMetadataProvider();

    // Set up the enrichment callback
    seriesEnrichmentService.setEnrichmentCallback((data) => {
      this.props.updateSeriesEnrichment(data);
    });

    // TODO: Connect to series enrichment service when search page loads
    // Temporarily disabled until SSE proxy is implemented in Chaptarr
    // seriesEnrichmentService.connect();
  }

  componentDidUpdate(prevProps) {
    if (prevProps.isHardcoverConfigured !== true &&
        this.props.isHardcoverConfigured === true &&
        this._pendingSearch) {
      const pendingSearch = this._pendingSearch;
      this._pendingSearch = null;
      this.props.getSearchResults(pendingSearch);
    }
  }

  componentWillUnmount() {
    this.clearSearchTimeout();

    this.props.clearSearchResults();

    // TODO: Disconnect from series enrichment service when leaving search page
    // seriesEnrichmentService.disconnect();
  }

  //
  // Listeners

  clearSearchTimeout = () => {
    if (this._searchTimeout) {
      clearTimeout(this._searchTimeout);
      this._searchTimeout = null;
    }
  };

  onSearchChange = (term, provider) => {
    this.clearSearchTimeout();
    this._pendingSearch = null;

    if (term.trim() === '') {
      this.props.clearSearchResults();
    } else if (provider === 'hardcover' && this.props.isHardcoverConfigured !== true) {
      this._pendingSearch = { term, provider };
    } else {
      this._searchTimeout = setTimeout(() => {
        this._searchTimeout = null;
        if (provider === 'hardcover' && this.props.isHardcoverConfigured !== true) {
          return;
        }
        this.props.getSearchResults({ term, provider });
      }, 300);
    }
  };

  onClearSearch = () => {
    this.clearSearchTimeout();
    this._pendingSearch = null;
    this.props.clearSearchResults();
  };

  onSearchSubmit = (term, provider = 'hardcover') => {
    this.clearSearchTimeout();
    this._pendingSearch = null;

    const normalizedTerm = term.trim();
    const encodedProvider = encodeURIComponent(provider);

    if (!normalizedTerm) {
      this.props.clearSearchResults();
      this.props.push(`/add/search?provider=${encodedProvider}`);
      return;
    }

    const currentProvider = this.props.provider ||
      (this.props.isHardcoverConfigured === false ? 'goodreads' : 'hardcover');

    if (this.props.term === normalizedTerm && currentProvider === provider) {
      this.onSearchChange(normalizedTerm, provider);
      return;
    }

    // Update the URL when a search is submitted. The route change starts the search.
    const encodedTerm = encodeURIComponent(normalizedTerm);
    this.props.push(`/add/search?term=${encodedTerm}&provider=${encodedProvider}`);
  };

  //
  // Render

  render() {
    const {
      term,
      provider,
      isHardcoverConfigured,
      metadataProviderConfig,
      ...otherProps
    } = this.props;

    return (
      <AddNewItem
        term={term}
        provider={provider}
        isHardcoverConfigured={isHardcoverConfigured}
        metadataProviderConfig={metadataProviderConfig}
        {...otherProps}
        onSearchChange={this.onSearchChange}
        onClearSearch={this.onClearSearch}
        onSearchSubmit={this.onSearchSubmit}
      />
    );
  }
}

AddNewItemConnector.propTypes = {
  term: PropTypes.string,
  provider: PropTypes.string,
  isHardcoverConfigured: PropTypes.bool,
  metadataProviderConfig: PropTypes.object,
  getSearchResults: PropTypes.func.isRequired,
  clearSearchResults: PropTypes.func.isRequired,
  fetchRootFolders: PropTypes.func.isRequired,
  fetchMetadataProvider: PropTypes.func.isRequired,
  updateSeriesEnrichment: PropTypes.func.isRequired,
  push: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(AddNewItemConnector);
