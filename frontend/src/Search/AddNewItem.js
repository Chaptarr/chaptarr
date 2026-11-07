import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import CheckInput from 'Components/Form/CheckInput';
import SelectInput from 'Components/Form/SelectInput';
import TextInput from 'Components/Form/TextInput';
import Icon from 'Components/Icon';
import Button from 'Components/Link/Button';
import Link from 'Components/Link/Link';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import { icons, kinds } from 'Helpers/Props';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';
import AddNewAuthorSearchResultConnector from './Author/AddNewAuthorSearchResultConnector';
import AddNewBookSearchResultConnector from './Book/AddNewBookSearchResultConnector';
import AddNewSeriesSearchResult from './Series/AddNewSeriesSearchResult';
import styles from './AddNewItem.css';

const SEARCH_AS_YOU_TYPE_KEY = 'chaptarrng.addNewItem.searchAsYouType';
const LEGACY_SEARCH_AS_YOU_TYPE_KEY = 'bookshelfng.addNewItem.searchAsYouType';

function getSearchAsYouTypePreference() {
  try {
    const preference = window.localStorage.getItem(SEARCH_AS_YOU_TYPE_KEY);

    if (preference !== null) {
      return preference === 'true';
    }

    const legacyPreference = window.localStorage.getItem(LEGACY_SEARCH_AS_YOU_TYPE_KEY);

    if (legacyPreference !== null) {
      window.localStorage.setItem(SEARCH_AS_YOU_TYPE_KEY, legacyPreference);
      return legacyPreference === 'true';
    }
  } catch (error) {
    return false;
  }

  return false;
}

const supplementalMetadataProviders = [
  { key: 'openlibrary', name: 'enableOpenLibrary', label: 'OpenLibrary' },
  { key: 'googlebooks', name: 'enableGoogleBooks', label: 'GoogleBooks', requiresGoogleBooksKey: true },
  { key: 'loc', name: 'enableLoc', label: 'LibraryOfCongress' },
  { key: 'gutendex', name: 'enableGutendex', label: 'Gutendex' },
  { key: 'internetarchive', name: 'enableInternetArchive', label: 'InternetArchive' },
  { key: 'ndl', name: 'enableNdl', label: 'NdlSearch' },
  { key: 'europeana', name: 'enableEuropeana', label: 'Europeana', requiresEuropeanaKey: true },
  { key: 'apify-goodreads', name: 'enableApifyGoodreads', label: 'ApifyGoodreadsActor', requiresApifyCredentials: true }
];

function isMetadataApiEnabled(metadataProviderConfig) {
  if (!metadataProviderConfig?.isPopulated || metadataProviderConfig.error) {
    return true;
  }

  return metadataProviderConfig.item?.enableMetadataApi?.value === true;
}

function getNonHardcoverProvider(metadataProviderConfig) {
  return isMetadataApiEnabled(metadataProviderConfig) ? 'metadata-api' : 'goodreads';
}

const supportsSeriesFilter = (provider) => provider !== 'goodreads' &&
  provider !== 'metadata' &&
  provider !== 'metadata-api' &&
  !supplementalMetadataProviders.some((source) => source.key === provider);

class AddNewItem extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    const defaultProvider = props.isHardcoverConfigured === false ? getNonHardcoverProvider(props.metadataProviderConfig) : 'hardcover';
    const providerFromProps = props.provider || defaultProvider;
    const selectedProvider = props.isHardcoverConfigured === false && providerFromProps === 'hardcover' ? defaultProvider : providerFromProps;

    const term = props.term || '';

    this.state = {
      term,
      isFetching: !!term,
      selectedProvider,
      activeFilter: 'all', // all, authors, books, series
      hasSearched: !!term,
      searchAsYouTypeEnabled: getSearchAsYouTypePreference()
    };
  }

  componentDidMount() {
    const term = this.state.term;
    const selectedProvider = this.state.selectedProvider;

    if (term) {
      this.props.onSearchChange(term, selectedProvider);
    }
  }

  componentDidUpdate(prevProps) {
    const {
      term,
      provider,
      isHardcoverConfigured,
      isFetching
    } = this.props;

    const providerChanged = !!provider && provider !== prevProps.provider;
    const termChanged = (term || '') !== (prevProps.term || '');

    if (providerChanged || termChanged) {
      const selectedProvider = providerChanged ? provider : this.state.selectedProvider;
      const nextTerm = term || '';

      this.setState((prevState) => ({
        activeFilter: !supportsSeriesFilter(selectedProvider) && prevState.activeFilter === 'series' ?
          'all' :
          prevState.activeFilter,
        term: nextTerm,
        isFetching: !!nextTerm,
        hasSearched: !!nextTerm,
        selectedProvider
      }), () => {
        if (this.state.term) {
          this.props.onSearchChange(this.state.term, this.state.selectedProvider);
        } else {
          this.props.onClearSearch();
        }
      });
    } else if (isFetching !== prevProps.isFetching) {
      this.setState({
        isFetching
      });
    }

    if (prevProps.isHardcoverConfigured !== isHardcoverConfigured &&
        isHardcoverConfigured === false &&
        this.state.selectedProvider === 'hardcover') {
      const nextProvider = getNonHardcoverProvider(this.props.metadataProviderConfig);
      this.setState({ selectedProvider: nextProvider }, () => {
        const nextTerm = this.state.term;
        if (nextTerm) {
          this.props.onSearchChange(nextTerm, this.state.selectedProvider);
        }
      });
    }

    if (prevProps.metadataProviderConfig !== this.props.metadataProviderConfig &&
        this.props.metadataProviderConfig?.isPopulated &&
        this.props.isHardcoverConfigured === false &&
        !this.props.provider &&
        (this.state.selectedProvider === 'metadata-api' || this.state.selectedProvider === 'goodreads')) {
      const nextProvider = getNonHardcoverProvider(this.props.metadataProviderConfig);
      if (nextProvider !== this.state.selectedProvider) {
        this.setState({ selectedProvider: nextProvider }, () => {
          if (this.state.term) {
            this.props.onSearchChange(this.state.term, nextProvider);
          }
        });
      }
    }
  }

  //
  // Listeners

  onSearchInputChange = ({ value }) => {
    const hasValue = !!value.trim();
    const searchAsYouTypeEnabled = this.state.searchAsYouTypeEnabled;

    this.setState({
      term: value,
      isFetching: hasValue && searchAsYouTypeEnabled,
      hasSearched: hasValue && searchAsYouTypeEnabled,
      activeFilter: 'all'
    }, () => {
      if (hasValue && searchAsYouTypeEnabled) {
        this.props.onSearchChange(value, this.state.selectedProvider);
      } else {
        this.props.onClearSearch();
      }
    });
  };

  onSearchAsYouTypeChange = ({ value }) => {
    const enabled = value === true;
    const term = this.state.term;

    try {
      window.localStorage.setItem(SEARCH_AS_YOU_TYPE_KEY, enabled ? 'true' : 'false');
    } catch (error) {
      // Keep this choice for the current page if browser storage is unavailable.
    }

    this.setState({
      searchAsYouTypeEnabled: enabled,
      isFetching: enabled && !!term.trim(),
      hasSearched: enabled && !!term.trim(),
      activeFilter: 'all'
    }, () => {
      if (enabled && term.trim()) {
        this.props.onSearchChange(term, this.state.selectedProvider);
      } else {
        this.props.onClearSearch();
      }
    });
  };

  onSearchPress = () => {
    const term = this.state.term.trim();

    if (!term) {
      this.props.onClearSearch();
      return;
    }

    this.setState({
      isFetching: true,
      hasSearched: true,
      activeFilter: 'all'
    }, () => this.props.onSearchSubmit(term, this.state.selectedProvider));
  };

  onSearchInputKeyDown = (event) => {
    if (event.target.name !== 'searchBox' || event.key !== 'Enter') {
      return;
    }

    event.preventDefault();

    if (this.state.searchAsYouTypeEnabled) {
      const term = this.state.term.trim();

      if (term) {
        this.props.onSearchSubmit(term, this.state.selectedProvider);
      }
    } else {
      this.onSearchPress();
    }
  };

  onProviderChange = ({ value }) => {
    this.setState((prevState) => ({
      selectedProvider: value,
      activeFilter: !supportsSeriesFilter(value) && prevState.activeFilter === 'series' ? 'all' : prevState.activeFilter,
      isFetching: prevState.searchAsYouTypeEnabled && !!prevState.term.trim(),
      hasSearched: prevState.searchAsYouTypeEnabled && !!prevState.term.trim()
    }), () => {
      const { term, searchAsYouTypeEnabled } = this.state;
      if (term.trim() && searchAsYouTypeEnabled) {
        this.props.onSearchChange(term, value);
      } else {
        this.props.onClearSearch();
      }
    });
  };

  onClearSearchPress = () => {
    this.setState({ term: '', activeFilter: 'all', hasSearched: false, isFetching: false }, () => {
      this.props.onSearchSubmit('', this.state.selectedProvider);
    });
  };

  onFilterChange = (filter) => {
    this.setState({ activeFilter: filter });
  };

  getFilteredResults = () => {
    const { items } = this.props;
    const { activeFilter } = this.state;

    if (activeFilter === 'all') {
      return items;
    }

    return items.filter((item) => {
      if (activeFilter === 'authors' && item.author) {
        return true;
      }
      if (activeFilter === 'books' && item.book) {
        return true;
      }
      if (activeFilter === 'series' && item.series) {
        return true;
      }
      return false;
    });
  };

  getSearchHint = () => {
    switch (this.state.selectedProvider) {
      case 'goodreads':
        return translate('SearchHintProviderGoodreads');
      case 'audible':
        return translate('SearchHintProviderAudible');
      default:
        return translate('SearchHintProviderHardcover');
    }
  };

  //
  // Render

  render() {
    const {
      error,
      items,
      hasExistingAuthors
    } = this.props;

    const term = this.state.term;
    const isFetching = this.state.isFetching;
    const selectedProvider = this.state.selectedProvider;
    const activeFilter = this.state.activeFilter;
    const hasSearched = this.state.hasSearched;
    const searchAsYouTypeEnabled = this.state.searchAsYouTypeEnabled;
    const filteredItems = this.getFilteredResults();
    const showSeriesFilter = supportsSeriesFilter(selectedProvider);
    const isHardcoverConfigured = this.props.isHardcoverConfigured !== false;
    const metadataProviderConfig = this.props.metadataProviderConfig || {};
    const metadataProviderSettings = metadataProviderConfig.item || {};

    const metadataProviders = [
      { key: 'hardcover', value: 'Hardcover', isDisabled: !isHardcoverConfigured },
      { key: 'goodreads', value: 'Goodreads' },
      { key: 'audible', value: translate('Audiobooks') }
    ];

    if (metadataProviderConfig.isPopulated && !metadataProviderConfig.error && metadataProviderSettings.enableMetadataApi?.value) {
      metadataProviders.splice(1, 0, { key: 'metadata-api', value: translate('MetadataApiCatalog') });
    }

    if (metadataProviderConfig.isPopulated && !metadataProviderConfig.error) {
      supplementalMetadataProviders.forEach((source) => {
        const enabled = metadataProviderSettings[source.name];
        const hasGoogleBooksKey = !source.requiresGoogleBooksKey || metadataProviderSettings.hasGoogleBooksApiKey;
        const hasEuropeanaKey = !source.requiresEuropeanaKey || metadataProviderSettings.hasEuropeanaApiKey;
        const hasApifyCredentials = !source.requiresApifyCredentials ||
          (metadataProviderSettings.apifyGoodreadsActor && metadataProviderSettings.hasApifyToken);

        if (enabled && hasGoogleBooksKey && hasEuropeanaKey && hasApifyCredentials) {
          metadataProviders.push({ key: source.key, value: translate(source.label) });
        }
      });
    }

    return (
      <PageContent title={translate('AddNewItem')}>
        <PageContentBody>
          <div className={styles.searchContainer} onKeyDown={this.onSearchInputKeyDown}>
            <div className={styles.searchIconContainer}>
              <Icon
                name={icons.SEARCH}
                size={20}
              />
            </div>

            <TextInput
              className={styles.searchInput}
              name="searchBox"
              value={term}
              placeholder={translate('SearchBoxPlaceHolder')}
              autoFocus={true}
              onChange={this.onSearchInputChange}
            />

            <Button
              className={styles.clearLookupButton}
              title={translate('Clear')}
              aria-label={translate('Clear')}
              onPress={this.onClearSearchPress}
            >
              <Icon
                name={icons.REMOVE}
                size={20}
              />
            </Button>

            <div className={styles.providerSelectContainer}>
              <SelectInput
                className={styles.providerSelect}
                name="provider"
                aria-label={translate('MetadataSource')}
                value={selectedProvider}
                values={metadataProviders}
                onChange={this.onProviderChange}
              />
              <Icon
                className={styles.providerSelectIcon}
                name={icons.CARET_DOWN}
              />
            </div>
          </div>

          <div className={styles.searchOptions}>
            <CheckInput
              name="searchAsYouType"
              value={searchAsYouTypeEnabled}
              helpText={translate('SearchAsYouType')}
              onChange={this.onSearchAsYouTypeChange}
            />

            {
              !searchAsYouTypeEnabled &&
                <Button
                  kind={kinds.PRIMARY}
                  onPress={this.onSearchPress}
                >
                  {translate('Search')}
                </Button>
            }
          </div>

          {
            !isHardcoverConfigured &&
              <Alert kind={kinds.INFO}>
                {translate('SearchHardcoverDisabledIntro')} <Link to="/system/quickstart">{translate('Quickstart')}</Link>.
              </Alert>
          }

          {
            isFetching &&
              <LoadingIndicator />
          }

          {
            !isFetching && hasSearched && !!error ?
              <div className={styles.message}>
                <div className={styles.helpText}>
                  {translate('FailedLoadingSearchResults')}
                </div>

                <Alert kind={kinds.WARNING} role="alert">
                  <div>{getErrorMessage(error, translate('FailedLoadingSearchResults'))}</div>
                  {
                    searchAsYouTypeEnabled &&
                      <div>
                        <Button size="small" onPress={this.onSearchPress}>
                          {translate('Retry')}
                        </Button>
                      </div>
                  }
                </Alert>

                <div>
                  <Link to="https://discord.gg/nqFGsGUug2">
                    {translate('WhySearchesCouldBeFailing')}
                  </Link>
                </div>
              </div> : null
          }

          {
            !isFetching && hasSearched && !error && !!items.length &&
              <div>
                <div className={styles.filterButtons}>
                  <Button
                    className={activeFilter === 'all' ? styles.activeFilter : ''}
                    onPress={() => this.onFilterChange('all')}
                  >
                    {translate('SearchFilterAllCount', { count: items.length })}
                  </Button>
                  <Button
                    className={activeFilter === 'authors' ? styles.activeFilter : ''}
                    onPress={() => this.onFilterChange('authors')}
                  >
                    {translate('SearchFilterAuthorsCount', { count: items.filter((item) => item.author).length })}
                  </Button>
                  <Button
                    className={activeFilter === 'books' ? styles.activeFilter : ''}
                    onPress={() => this.onFilterChange('books')}
                  >
                    {translate('SearchFilterBooksCount', { count: items.filter((item) => item.book).length })}
                  </Button>
                  {
                    showSeriesFilter ?
                      <Button
                        className={activeFilter === 'series' ? styles.activeFilter : ''}
                        onPress={() => this.onFilterChange('series')}
                      >
                        {translate('SearchFilterSeriesCount', { count: items.filter((item) => item.series).length })}
                      </Button> : null
                  }
                </div>

                <div className={styles.searchResults}>
                  {
                    filteredItems.map((item) => {
                      if (item.author) {
                        const author = item.author;
                        return (
                          <AddNewAuthorSearchResultConnector
                            key={item.id}
                            {...author}
                            metadataBookCount={item.metadataBookCount}
                            searchProvider={selectedProvider}
                          />
                        );
                      } else if (item.book) {
                        const book = item.book;
                        return (
                          <AddNewBookSearchResultConnector
                            key={item.id}
                            isExistingAuthor={'id' in book.author && book.author.id !== 0}
                            {...book}
                            searchProvider={selectedProvider}
                          />
                        );
                      } else if (item.series) {
                        const series = item.series;
                        return (
                          <AddNewSeriesSearchResult
                            key={item.id || series.foreignSeriesId}
                            {...series}
                          />
                        );
                      }
                      return null;
                    })
                  }
                </div>
              </div>
          }

          {
            !isFetching && hasSearched && !error && !items.length && !!term &&
              <div className={styles.message}>
                <div className={styles.noResults}>
                  {translate('CouldntFindAnyResultsForTerm', [term])}
                </div>
                <div>
                  {this.getSearchHint()}
                </div>
              </div>
          }

          {
            term ?
              null :
              <div className={styles.message}>
                <div className={styles.helpText}>
                  {translate('ItsEasyToAddANewAuthorOrBookJustStartTypingTheNameOfTheItemYouWantToAdd')}
                </div>
                <div>
                  {this.getSearchHint()}
                </div>
              </div>
          }

          {
            !term && !hasExistingAuthors ?
              <div className={styles.message}>
                <div className={styles.noAuthorsText}>
                  {translate('SearchNoAuthorsPrompt')}
                </div>
                <div>
                  <Button
                    to="/settings/mediamanagement"
                    kind={kinds.PRIMARY}
                  >
                    {translate('AddRootFolder')}
                  </Button>
                </div>
              </div> :
              null
          }

          <div />
        </PageContentBody>
      </PageContent>
    );
  }
}

AddNewItem.propTypes = {
  term: PropTypes.string,
  provider: PropTypes.string,
  isHardcoverConfigured: PropTypes.bool,
  metadataProviderConfig: PropTypes.object,
  isFetching: PropTypes.bool.isRequired,
  error: PropTypes.object,
  isAdding: PropTypes.bool.isRequired,
  addError: PropTypes.object,
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  hasExistingAuthors: PropTypes.bool.isRequired,
  onSearchChange: PropTypes.func.isRequired,
  onClearSearch: PropTypes.func.isRequired,
  onSearchSubmit: PropTypes.func.isRequired
};

export default AddNewItem;
