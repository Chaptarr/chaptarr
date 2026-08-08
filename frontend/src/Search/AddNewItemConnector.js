import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { push } from 'connected-react-router';
import { clearSearchResults, getSearchResults, updateSeriesEnrichment } from 'Store/Actions/searchActions';
import { fetchRootFolders } from 'Store/Actions/settingsActions';
import parseUrl from 'Utilities/String/parseUrl';
import seriesEnrichmentService from 'Services/SeriesEnrichmentService';
import AddNewItem from './AddNewItem';

function createMapStateToProps() {
  return createSelector(
    (state) => state.search,
    (state) => state.authors.items.length,
    (state) => state.router.location,
    (state) => state.settings.hardcoverConfig,
    (search, existingAuthorsCount, location, hardcoverConfig) => {
      const { params } = parseUrl(location.search);
      const { isPopulated, error, item } = hardcoverConfig;
      const isHardcoverConfigured = (!isPopulated && !error)
        ? null
        : !!(item?.enabled && item?.hasToken);

      return {
        ...search,
        term: params.term,
        provider: params.provider,
        hasExistingAuthors: existingAuthorsCount > 0,
        isHardcoverConfigured
      };
    }
  );
}

const mapDispatchToProps = {
  getSearchResults,
  clearSearchResults,
  fetchRootFolders,
  updateSeriesEnrichment,
  push
};

class AddNewItemConnector extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this._searchTimeout = null;
  }

  componentDidMount() {
    this.props.fetchRootFolders();
    
    // Set up the enrichment callback
    seriesEnrichmentService.setEnrichmentCallback((data) => {
      this.props.updateSeriesEnrichment(data);
    });
    
    // TODO: Connect to series enrichment service when search page loads
    // Temporarily disabled until SSE proxy is implemented in Chaptarr
    // seriesEnrichmentService.connect();
  }

  componentDidUpdate(prevProps) {
    // Trigger initial Hardcover search once we know Hardcover is configured.
    // This replaces the old behavior where the local /config/hardcover fetch callback started the search.
    if (prevProps.isHardcoverConfigured !== true &&
        this.props.isHardcoverConfigured === true) {
      const { term, provider } = this.props;
      if (term && (!provider || provider === 'hardcover')) {
        if (this._searchTimeout) {
          clearTimeout(this._searchTimeout);
          this._searchTimeout = null;
        }
        this.props.getSearchResults({ term, provider: 'hardcover' });
      }
    }
  }

  componentWillUnmount() {
    if (this._searchTimeout) {
      clearTimeout(this._searchTimeout);
    }
    
    this.props.clearSearchResults();
    
    // TODO: Disconnect from series enrichment service when leaving search page
    // seriesEnrichmentService.disconnect();
  }

  //
  // Listeners

  onSearchChange = (term, provider) => {
    if (this._searchTimeout) {
      clearTimeout(this._searchTimeout);
    }

    if (term.trim() === '') {
      this.props.clearSearchResults();
    } else {
      this._searchTimeout = setTimeout(() => {
        if (provider === 'hardcover' && this.props.isHardcoverConfigured !== true) {
          return;
        }
        this.props.getSearchResults({ term, provider });
      }, 300);
    }
  };

  onClearSearch = () => {
    this.props.clearSearchResults();
  };

  onSearchSubmit = (term, provider = 'hardcover') => {
    // Update the URL when Enter is pressed
    const encodedTerm = encodeURIComponent(term);
    const encodedProvider = encodeURIComponent(provider);
    this.props.push(`/add/search?term=${encodedTerm}&provider=${encodedProvider}`);
  };

  //
  // Render

  render() {
    const {
      term,
      provider,
      isHardcoverConfigured,
      ...otherProps
    } = this.props;

    return (
      <AddNewItem
        term={term}
        provider={provider}
        isHardcoverConfigured={isHardcoverConfigured}
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
  getSearchResults: PropTypes.func.isRequired,
  clearSearchResults: PropTypes.func.isRequired,
  fetchRootFolders: PropTypes.func.isRequired,
  updateSeriesEnrichment: PropTypes.func.isRequired,
  push: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(AddNewItemConnector);
