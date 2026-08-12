import find from 'lodash/find';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { clearOptions, defaultState, fetchOptions } from 'Store/Actions/providerOptionActions';
import BookshelfInput from './BookshelfInput';

function createMapStateToProps() {
  return createSelector(
    (state) => state.providerOptions.bookshelves || defaultState,
    (state, props) => props.name,
    (bookshelves, name) => {
      const {
        items,
        ...otherState
      } = bookshelves;
      return ({
        helptext: items && items.helptext && items.helptext[name] ? items.helptext[name] : '',
        user: items && items.user ? items.user : '',
        items: items && items.shelves ? items.shelves : [],
        ...otherState
      });
    }
  );
}

const mapDispatchToProps = {
  dispatchFetchOptions: fetchOptions,
  dispatchClearOptions: clearOptions
};

class BookshelfInputConnector extends Component {

  //
  // Lifecycle

  componentDidMount = () => {
    if (this._getPopulateKey(this.props)) {
      this._populate();
    }
  };

  componentDidUpdate(prevProps, prevState) {
    const newKey = this._getPopulateKey(this.props);
    const oldKey = this._getPopulateKey(prevProps);
    if (newKey && newKey !== oldKey) {
      this._populate();
    }
  }

  componentWillUnmount = () => {
    this.props.dispatchClearOptions({ section: 'bookshelves' });
  };

  //
  // Control

  _populate() {
    const {
      provider,
      providerData,
      dispatchFetchOptions,
      name
    } = this.props;

    dispatchFetchOptions({
      section: 'bookshelves',
      action: 'getBookshelves',
      queryParams: { name },
      provider,
      providerData
    });
  }

  _getPopulateKey(props) {
    const fields = (props.providerData && props.providerData.fields) || [];

    // AudioBookShelf uses 'apiKey' instead of 'accessToken'
    const apiKeyField = find(fields, { name: 'apiKey' });
    const accessTokenField = find(fields, { name: 'accessToken' });

    // Goodreads Bookshelves (public) uses 'userId' (no OAuth)
    const userIdField = find(fields, { name: 'userId' });

    return (apiKeyField && apiKeyField.value) ||
      (accessTokenField && accessTokenField.value) ||
      (userIdField && userIdField.value);
  }

  //
  // Render

  render() {
    return (
      <BookshelfInput
        {...this.props}
        onRefreshPress={this.onRefreshPress}
      />
    );
  }
}

BookshelfInputConnector.propTypes = {
  provider: PropTypes.string.isRequired,
  providerData: PropTypes.object.isRequired,
  name: PropTypes.string.isRequired,
  onChange: PropTypes.func.isRequired,
  dispatchFetchOptions: PropTypes.func.isRequired,
  dispatchClearOptions: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(BookshelfInputConnector);
