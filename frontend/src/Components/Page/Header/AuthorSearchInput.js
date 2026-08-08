import classNames from 'classnames';
import _ from 'lodash';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Autosuggest from 'react-autosuggest';
import Icon from 'Components/Icon';
import keyboardShortcuts, { shortcuts } from 'Components/keyboardShortcuts';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import { icons } from 'Helpers/Props';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import translate from 'Utilities/String/translate';
import AuthorSearchResult from './AuthorSearchResult';
import BookSearchResult from './BookSearchResult';
import styles from './AuthorSearchInput.css';

const ADD_NEW_TYPE = 'addNew';

class AuthorSearchInput extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this._autosuggest = null;
    this._abortRequest = null;
    this._requestSeq = 0;

    this.state = {
      value: '',
      suggestions: [],
      loading: false
    };
  }

  componentDidMount() {
    this.props.bindShortcut(shortcuts.AUTHOR_SEARCH_INPUT.key, this.focusInput);
  }

  componentWillUnmount() {
    if (this._abortRequest) {
      this._abortRequest();
      this._abortRequest = null;
    }
  }

  //
  // Control

  setAutosuggestRef = (ref) => {
    this._autosuggest = ref;
  };

  focusInput = (event) => {
    event.preventDefault();
    this._autosuggest.input.focus();
  };

  getSectionSuggestions(section) {
    return section.suggestions;
  }

  renderSectionTitle(section) {
    return (
      <div className={styles.sectionTitle}>
        {section.title}

        {
          section.loading &&
            <LoadingIndicator
              className={styles.loading}
              rippleClassName={styles.ripple}
              size={20}
            />
        }
      </div>
    );
  }

  getSuggestionValue(suggestion) {
    if (!suggestion) {
      return '';
    }

    if (suggestion.type === ADD_NEW_TYPE) {
      return suggestion.title || '';
    }

    return suggestion.item?.name || suggestion.item?.title || '';
  }

  renderSuggestion(item, { query }) {
    if (item.type === ADD_NEW_TYPE) {
      return (
        <div
          className={classNames(
            styles.addNewAuthorSuggestion,
            item.isDisabled && styles.addNewAuthorSuggestionDisabled
          )}
        >
          <div>
            {translate('AuthorSearchInputSearchProviderForQuery', { providerLabel: item.providerLabel, query })}
          </div>
          {
            item.isDisabled &&
              <div className={styles.addNewAuthorSuggestionHint}>
                {translate('AuthorSearchInputConnectHardcoverHint')}
              </div>
          }
        </div>
      );
    }

    if (item.item.type === 'author') {
      return (
        <AuthorSearchResult
          {...item.item}
          match={item.matches[0]}
        />
      );
    }

    if (item.item.type === 'book') {
      return (
        <BookSearchResult
          {...item.item}
          match={item.matches[0]}
        />
      );
    }

  }

  goToItem(item) {
    const {
      onGoToAuthor,
      onGoToBook
    } = this.props;

    this.setState({ value: '' });

    const {
      type,
      id
    } = item.item;

    if (type === 'author') {
      onGoToAuthor(id);
    } else if (type === 'book') {
      onGoToBook(id);
    }
  }

  getAddNewSuggestions(query) {
    const trimmedQuery = (query || '').trim();

    if (!trimmedQuery) {
      return [];
    }

    const isHardcoverConfigured = this.props.isHardcoverConfigured !== false;

    return [
      { type: ADD_NEW_TYPE, title: trimmedQuery, provider: 'hardcover', providerLabel: 'Hardcover', isDisabled: !isHardcoverConfigured },
      { type: ADD_NEW_TYPE, title: trimmedQuery, provider: 'goodreads', providerLabel: 'Goodreads' },
      { type: ADD_NEW_TYPE, title: trimmedQuery, provider: 'audible', providerLabel: 'Audiobooks' }
    ];
  }

  reset() {
    this.setState({
      value: '',
      suggestions: [],
      loading: false
    });
  }

  //
  // Listeners

  onChange = (event, { newValue, method }) => {
    if (method === 'up' || method === 'down') {
      return;
    }

    this.setState({ value: newValue });
  };

  onKeyDown = (event) => {
    if (event.shiftKey || event.altKey || event.ctrlKey) {
      return;
    }

    if (event.key === 'Escape') {
      this.reset();
      return;
    }

    if (event.key !== 'Tab' && event.key !== 'Enter') {
      return;
    }

    const {
      suggestions,
      value
    } = this.state;

    const trimmedValue = value.trim();
    if (!trimmedValue) {
      return;
    }

    const {
      highlightedSectionIndex,
      highlightedSuggestionIndex
    } = this._autosuggest.state;

    const addNewSuggestions = this.getAddNewSuggestions(trimmedValue);
    const defaultAddNewProvider = addNewSuggestions.find((x) => !x.isDisabled)?.provider || 'goodreads';

    const hasExistingItemsSection = suggestions.length || this.state.loading;
    const addNewSectionIndex = hasExistingItemsSection ? 1 : 0;

    if (!suggestions.length || highlightedSectionIndex === addNewSectionIndex) {
      let provider = defaultAddNewProvider;

      if (highlightedSectionIndex === addNewSectionIndex && highlightedSuggestionIndex != null) {
        const highlighted = addNewSuggestions[highlightedSuggestionIndex];
        if (highlighted?.isDisabled) {
          if (this.props.onHardcoverSetupRequired) {
            this.props.onHardcoverSetupRequired();
          }
          return;
        }

        provider = highlighted?.provider || provider;
      }

      this.props.onGoToAddNewAuthor(trimmedValue, provider);
      this._autosuggest.input.blur();
      this.reset();

      return;
    }

    // If an suggestion is not selected go to the first author,
    // otherwise go to the selected author.

    if (highlightedSuggestionIndex == null) {
      this.goToItem(suggestions[0]);
    } else {
      this.goToItem(suggestions[highlightedSuggestionIndex]);
    }

    this._autosuggest.input.blur();
    this.reset();
  };

  onBlur = () => {
    this.reset();
  };

  onSuggestionsFetchRequested = ({ value }) => {
    if (!this.state.loading) {
      this.setState({
        loading: true
      });
    }

    this.requestSuggestions(value);
  };

  requestSuggestions = _.debounce((value) => {
    if (!this.state.loading) {
      return;
    }

    const trimmedValue = (value || '').trim();

    // Avoid expensive searches for empty/very short terms.
    if (trimmedValue.length < 2) {
      if (this._abortRequest) {
        this._abortRequest();
        this._abortRequest = null;
      }

      this.setState({
        suggestions: [],
        loading: false
      });

      return;
    }

    const seq = ++this._requestSeq;

    if (this._abortRequest) {
      this._abortRequest();
      this._abortRequest = null;
    }

    const { request, abortRequest } = createAjaxRequest({
      url: '/library/search',
      dataType: 'json',
      data: {
        term: trimmedValue,
        limit: 10
      }
    });

    this._abortRequest = abortRequest;

    request.done((data) => {
      if (seq !== this._requestSeq) {
        return;
      }

      const authors = data?.authors || [];
      const books = data?.books || [];

      const suggestions = [
        ...authors.map((a) => ({
          item: {
            type: 'author',
            id: a.id,
            name: a.name,
            images: a.images || [],
            tags: []
          },
          matches: [{ key: 'name', value: a.name }],
          arrayIndex: 0
        })),
        ...books.map((b) => ({
          item: {
            type: 'book',
            id: b.id,
            title: b.title,
            name: b.title,
            authorId: b.authorId,
            authorName: b.authorName,
            mediaType: b.mediaType,
            monitored: b.monitored,
            images: b.images || [],
            foreignBookId: b.foreignBookId,
            foreignAuthorId: b.foreignAuthorId,
            hardcoverBookId: b.hardcoverBookId,
            goodreadsBookId: b.goodreadsBookId,
            goodreadsWorkId: b.goodreadsWorkId,
            openLibraryWorkId: b.openLibraryWorkId,
            googleBooksId: b.googleBooksId,
            asin: b.asin,
            audibleASIN: b.audibleASIN,
            hardcoverAuthorId: b.hardcoverAuthorId,
            goodreadsAuthorId: b.goodreadsAuthorId,
            openLibraryAuthorId: b.openLibraryAuthorId,
            googleBooksAuthorId: b.googleBooksAuthorId,
            audnexusAuthorId: b.audnexusAuthorId,
            localAudiobookBooks: b.localAudiobookBooks || [],
            localEbookBooks: b.localEbookBooks || [],
            tags: []
          },
          matches: [{ key: 'name', value: b.title }],
          arrayIndex: 0
        }))
      ];

      this.setState({
        suggestions,
        loading: false
      });
    });

    request.fail((xhr) => {
      if (seq !== this._requestSeq) {
        return;
      }

      if (xhr && xhr.aborted) {
        return;
      }

      this.setState({
        suggestions: [],
        loading: false
      });
    });
  }, 250);

  onSuggestionsClearRequested = () => {
    this.setState({
      suggestions: [],
      loading: false
    });
  };

  onSuggestionSelected = (event, { suggestion }) => {
    if (suggestion.type === ADD_NEW_TYPE) {
      if (suggestion.isDisabled) {
        if (this.props.onHardcoverSetupRequired) {
          this.props.onHardcoverSetupRequired();
        }
        return;
      }

      this.props.onGoToAddNewAuthor(this.state.value.trim(), suggestion.provider);
    } else {
      this.goToItem(suggestion);
    }
  };

  //
  // Render

  render() {
    const {
      value,
      loading,
      suggestions
    } = this.state;

    const suggestionGroups = [];

    if (suggestions.length || loading) {
      suggestionGroups.push({
        title: translate('ExistingItems'),
        loading,
        suggestions
      });
    }

    const addNewSuggestions = this.getAddNewSuggestions(value);
    if (addNewSuggestions.length) {
      suggestionGroups.push({
        title: translate('AddNewItem'),
        suggestions: addNewSuggestions
      });
    }

    const inputProps = {
      className: styles.input,
      name: 'authorSearch',
      value,
      placeholder: translate('Search'),
      autoComplete: 'off',
      spellCheck: false,
      onChange: this.onChange,
      onKeyDown: this.onKeyDown,
      onBlur: this.onBlur
    };

    const theme = {
      container: styles.container,
      containerOpen: styles.containerOpen,
      suggestionsContainer: styles.authorContainer,
      suggestionsList: styles.list,
      suggestion: styles.listItem,
      suggestionHighlighted: styles.highlighted
    };

    return (
      <div className={styles.wrapper}>
        <Icon name={icons.SEARCH} />

        <Autosuggest
          ref={this.setAutosuggestRef}
          id="authorSearch"
          inputProps={inputProps}
          theme={theme}
          focusInputOnSuggestionClick={false}
          multiSection={true}
          suggestions={suggestionGroups}
          getSectionSuggestions={this.getSectionSuggestions}
          renderSectionTitle={this.renderSectionTitle}
          getSuggestionValue={this.getSuggestionValue}
          renderSuggestion={this.renderSuggestion}
          onSuggestionSelected={this.onSuggestionSelected}
          onSuggestionsFetchRequested={this.onSuggestionsFetchRequested}
          onSuggestionsClearRequested={this.onSuggestionsClearRequested}
        />

      </div>
    );
  }
}

AuthorSearchInput.propTypes = {
  isHardcoverConfigured: PropTypes.bool,
  onGoToAuthor: PropTypes.func.isRequired,
  onGoToBook: PropTypes.func.isRequired,
  onGoToAddNewAuthor: PropTypes.func.isRequired,
  onHardcoverSetupRequired: PropTypes.func,
  bindShortcut: PropTypes.func.isRequired
};

export default keyboardShortcuts(AuthorSearchInput);
