import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import withCurrentPage from 'Components/withCurrentPage';
import * as ignoredActions from 'Store/Actions/ignoredActions';
import { registerPagePopulator, unregisterPagePopulator } from 'Utilities/pagePopulator';
import IgnoredDownloads from './IgnoredDownloads';

const mapDispatchToProps = {
  ...ignoredActions
};

class IgnoredDownloadsConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    const {
      useCurrentPage,
      fetchIgnored,
      gotoIgnoredFirstPage
    } = this.props;

    registerPagePopulator(this.repopulate);

    if (useCurrentPage) {
      fetchIgnored();
    } else {
      gotoIgnoredFirstPage();
    }
  }

  componentWillUnmount() {
    this.props.clearIgnored();
    unregisterPagePopulator(this.repopulate);
  }

  //
  // Control

  repopulate = () => {
    this.props.fetchIgnored();
  };

  //
  // Listeners

  onFirstPagePress = () => {
    this.props.gotoIgnoredFirstPage();
  };

  onPreviousPagePress = () => {
    this.props.gotoIgnoredPreviousPage();
  };

  onNextPagePress = () => {
    this.props.gotoIgnoredNextPage();
  };

  onLastPagePress = () => {
    this.props.gotoIgnoredLastPage();
  };

  onPageSelect = (page) => {
    this.props.gotoIgnoredPage({ page });
  };

  onRemoveSelected = (ids) => {
    this.props.removeIgnoredItems({ ids });
  };

  onSortPress = (sortKey) => {
    this.props.setIgnoredSort({ sortKey });
  };

  onTableOptionChange = (payload) => {
    this.props.setIgnoredTableOption(payload);

    if (payload.pageSize) {
      this.props.gotoIgnoredFirstPage();
    }
  };

  //
  // Render

  render() {
    return (
      <IgnoredDownloads
        onFirstPagePress={this.onFirstPagePress}
        onPreviousPagePress={this.onPreviousPagePress}
        onNextPagePress={this.onNextPagePress}
        onLastPagePress={this.onLastPagePress}
        onPageSelect={this.onPageSelect}
        onRemoveSelected={this.onRemoveSelected}
        onSortPress={this.onSortPress}
        onTableOptionChange={this.onTableOptionChange}
        {...this.props}
      />
    );
  }
}

IgnoredDownloadsConnector.propTypes = {
  useCurrentPage: PropTypes.bool.isRequired,
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  fetchIgnored: PropTypes.func.isRequired,
  gotoIgnoredFirstPage: PropTypes.func.isRequired,
  gotoIgnoredPreviousPage: PropTypes.func.isRequired,
  gotoIgnoredNextPage: PropTypes.func.isRequired,
  gotoIgnoredLastPage: PropTypes.func.isRequired,
  gotoIgnoredPage: PropTypes.func.isRequired,
  removeIgnoredItems: PropTypes.func.isRequired,
  setIgnoredSort: PropTypes.func.isRequired,
  setIgnoredTableOption: PropTypes.func.isRequired,
  clearIgnored: PropTypes.func.isRequired
};

export default withCurrentPage(
  connect((state) => state.ignored, mapDispatchToProps)(IgnoredDownloadsConnector)
);
