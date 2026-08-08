import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { clearNarratorDiscovery, fetchNarratorDiscovery, addNarratorVariant } from 'Store/Actions/narratorActions';
import NarratorSearchModalContent from './NarratorSearchModalContent';

function createMapStateToProps() {
  return createSelector(
    (state, { bookId }) => state.narrator.discovery[bookId],
    (state) => state.narrator.isSearching,
    (state) => state.narrator.isSettingPreferred,
    (state) => state.narrator.error,
    (discovery, isSearching, isSettingPreferred, error) => {
      return {
        discovery: discovery || {},
        isSearching,
        isSettingPreferred,
        error
      };
    }
  );
}

const mapDispatchToProps = {
  fetchNarratorDiscovery,
  clearNarratorDiscovery,
  addNarratorVariant
};

class NarratorSearchModalContentConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    const { bookId } = this.props;
    this.props.fetchNarratorDiscovery({ bookId });
  }

  componentWillUnmount() {
    const { bookId } = this.props;
    this.props.clearNarratorDiscovery({ bookId });
  }

  //
  // Listeners

  onNarratorSelect = (narrator, searchForNewBook) => {
    const { bookId } = this.props;
    // Use addNarratorVariant to create a new missing book variant
    this.props.addNarratorVariant({ bookId, narrator, searchForNewBook: !!searchForNewBook });
  };

  onRefreshPress = () => {
    const { bookId } = this.props;
    this.props.fetchNarratorDiscovery({ bookId, refresh: true });
  };

  //
  // Render

  render() {
    return (
      <NarratorSearchModalContent
        {...this.props}
        onNarratorSelect={this.onNarratorSelect}
        onRefreshPress={this.onRefreshPress}
      />
    );
  }
}

NarratorSearchModalContentConnector.propTypes = {
  bookId: PropTypes.number.isRequired,
  fetchNarratorDiscovery: PropTypes.func.isRequired,
  clearNarratorDiscovery: PropTypes.func.isRequired,
  addNarratorVariant: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(NarratorSearchModalContentConnector);
