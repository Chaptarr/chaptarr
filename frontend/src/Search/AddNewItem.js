import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
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

class AddNewItem extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    const defaultProvider = props.isHardcoverConfigured === false ? 'goodreads' : 'hardcover';
    const providerFromProps = props.provider || defaultProvider;
    const selectedProvider = props.isHardcoverConfigured === false && providerFromProps === 'hardcover' ?
      defaultProvider :
      providerFromProps;

    this.state = {
      term: props.term || '',
      isFetching: false,
      selectedProvider,
      activeFilter: 'all' // all, authors, books, series
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
    const termChanged = !!term && term !== prevProps.term;

    if (providerChanged || termChanged) {
      this.setState((prevState) => ({
        activeFilter: (providerChanged ? provider : prevState.selectedProvider) === 'goodreads' && prevState.activeFilter === 'series' ?
          'all' :
          prevState.activeFilter,
        term: termChanged ? term : prevState.term,
        isFetching: true,
        selectedProvider: providerChanged ? provider : prevState.selectedProvider
      }), () => {
        const nextTerm = this.state.term;
        if (nextTerm) {
          this.props.onSearchChange(nextTerm, this.state.selectedProvider);
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
      this.setState({ selectedProvider: 'goodreads' }, () => {
        const nextTerm = this.state.term;
        if (nextTerm) {
          this.props.onSearchChange(nextTerm, this.state.selectedProvider);
        }
      });
    }
  }

  //
  // Listeners

  onSearchInputChange = ({ value }) => {
    const hasValue = !!value.trim();

    this.setState({ term: value, isFetching: hasValue }, () => {
      if (hasValue) {
        this.props.onSearchChange(value, this.state.selectedProvider);
      } else {
        this.props.onClearSearch();
      }
    });
  };

  onSearchKeyPress = (event) => {
    if (event.key === 'Enter') {
      const { term } = this.state;
      const { selectedProvider } = this.state;
      if (term.trim()) {
        // Update the URL when Enter is pressed
        this.props.onSearchSubmit(term, selectedProvider);
      }
    }
  };

  onProviderChange = ({ value }) => {
    this.setState((prevState) => ({
      selectedProvider: value,
      activeFilter: value === 'goodreads' && prevState.activeFilter === 'series' ? 'all' : prevState.activeFilter
    }), () => {
      const { term } = this.state;
      if (term) {
        this.props.onSearchChange(term, value);
      }
    });
  };

  onClearSearchPress = () => {
    this.setState({ term: '', activeFilter: 'all' });
    this.props.onClearSearch();
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
    const filteredItems = this.getFilteredResults();
    const showSeriesFilter = selectedProvider !== 'goodreads';
    const isHardcoverConfigured = this.props.isHardcoverConfigured !== false;

    // Provider options - structured for future expansion
    const metadataProviders = [
      { key: 'hardcover', value: 'Hardcover', isDisabled: !isHardcoverConfigured },
      { key: 'goodreads', value: 'Goodreads' },
      { key: 'audible', value: 'Audiobooks' }
      // Future providers can be added here:
      // { key: 'openlibrary', value: 'OpenLibrary' },
      // { key: 'googlebooks', value: 'Google Books' }
    ];

    return (
      <PageContent title={translate('AddNewItem')}>
        <PageContentBody>
          <div className={styles.searchContainer}>
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
              onKeyPress={this.onSearchKeyPress}
            />

            <Button
              className={styles.clearLookupButton}
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
            !isFetching && !!error ?
              <div className={styles.message}>
                <div className={styles.helpText}>
                  {translate('FailedLoadingSearchResults')}
                </div>

                <Alert kind={kinds.WARNING}>{getErrorMessage(error)}</Alert>

                <div>
                  <Link to="https://discord.gg/nqFGsGUug2">
                    {translate('WhySearchesCouldBeFailing')}
                  </Link>
                </div>
              </div> : null
          }

          {
            !isFetching && !error && !!items.length &&
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
            !isFetching && !error && !items.length && !!term &&
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
