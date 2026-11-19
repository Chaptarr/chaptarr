import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { addNarratorVariant, clearNarratorDiscovery, fetchNarratorDiscovery } from 'Store/Actions/narratorActions';
import NarratorSearchModalContent from './NarratorSearchModalContent';

function createMapStateToProps() {
  return createSelector(
    (state, { bookId }) => state.narrator.discovery[bookId],
    (state) => state.narrator.isSearching,
    (state) => state.narrator.additions,
    (state) => state.narrator.error,
    (discovery, isSearching, additions, error) => {
      return {
        discovery: discovery || {},
        isSearching,
        additions,
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
    // Add a wanted copy or return the existing copy of this edition.
    this.props.addNarratorVariant({ bookId, narrator, searchForNewBook: !!searchForNewBook });
  };

  //
  // Render

  render() {
    return (
      <NarratorSearchModalContent
        {...this.props}
        onNarratorSelect={this.onNarratorSelect}
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
