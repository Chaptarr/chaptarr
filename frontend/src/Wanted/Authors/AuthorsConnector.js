import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import withCurrentPage from 'Components/withCurrentPage';
import {
  cancelWantedAuthor,
  clearWantedAuthors,
  deleteWantedAuthor,
  fetchWantedAuthors,
  gotoWantedAuthorsFirstPage,
  gotoWantedAuthorsLastPage,
  gotoWantedAuthorsNextPage,
  gotoWantedAuthorsPage,
  gotoWantedAuthorsPreviousPage,
  retryWantedAuthor,
  setWantedAuthorsSort,
  setWantedAuthorsTableOption
} from 'Store/Actions/wantedActions';
import Authors from './Authors';

class AuthorsConnector extends Component {
  componentDidMount() {
    if (this.props.useCurrentPage) {
      this.props.fetchWantedAuthors();
    } else {
      this.props.gotoWantedAuthorsFirstPage();
    }
  }

  componentDidUpdate() {
    const { isPopulated, isFetching, error, page, totalPages } = this.props;
    if (isPopulated && !isFetching && !error && totalPages > 0 && page > totalPages) {
      this.props.gotoWantedAuthorsLastPage();
    }
  }

  componentWillUnmount() {
    this.props.clearWantedAuthors();
  }

  onTableOptionChange = (payload) => {
    this.props.setWantedAuthorsTableOption(payload);
    if (payload.pageSize) {
      this.props.gotoWantedAuthorsFirstPage();
    }
  };

  render() {
    return (
      <Authors
        {...this.props}
        onReloadPress={() => this.props.fetchWantedAuthors()}
        onFirstPagePress={() => this.props.gotoWantedAuthorsFirstPage()}
        onPreviousPagePress={() => this.props.gotoWantedAuthorsPreviousPage()}
        onNextPagePress={() => this.props.gotoWantedAuthorsNextPage()}
        onLastPagePress={() => this.props.gotoWantedAuthorsLastPage()}
        onPageSelect={(page) => this.props.gotoWantedAuthorsPage({ page })}
        onSortPress={(sortKey) => this.props.setWantedAuthorsSort({ sortKey })}
        onTableOptionChange={this.onTableOptionChange}
        onRetryPress={(id) => this.props.retryWantedAuthor({ id })}
        onCancelConfirmed={(id) => this.props.cancelWantedAuthor({ id })}
        onDeleteConfirmed={(id) => this.props.deleteWantedAuthor({ id })}
      />
    );
  }
}

AuthorsConnector.propTypes = {
  useCurrentPage: PropTypes.bool.isRequired,
  isPopulated: PropTypes.bool.isRequired,
  isFetching: PropTypes.bool.isRequired,
  error: PropTypes.object,
  page: PropTypes.number,
  totalPages: PropTypes.number,
  fetchWantedAuthors: PropTypes.func.isRequired,
  gotoWantedAuthorsFirstPage: PropTypes.func.isRequired,
  gotoWantedAuthorsPreviousPage: PropTypes.func.isRequired,
  gotoWantedAuthorsNextPage: PropTypes.func.isRequired,
  gotoWantedAuthorsLastPage: PropTypes.func.isRequired,
  gotoWantedAuthorsPage: PropTypes.func.isRequired,
  setWantedAuthorsSort: PropTypes.func.isRequired,
  setWantedAuthorsTableOption: PropTypes.func.isRequired,
  clearWantedAuthors: PropTypes.func.isRequired,
  retryWantedAuthor: PropTypes.func.isRequired,
  cancelWantedAuthor: PropTypes.func.isRequired,
  deleteWantedAuthor: PropTypes.func.isRequired
};

const mapDispatchToProps = {
  fetchWantedAuthors,
  gotoWantedAuthorsFirstPage,
  gotoWantedAuthorsPreviousPage,
  gotoWantedAuthorsNextPage,
  gotoWantedAuthorsLastPage,
  gotoWantedAuthorsPage,
  setWantedAuthorsSort,
  setWantedAuthorsTableOption,
  clearWantedAuthors,
  retryWantedAuthor,
  cancelWantedAuthor,
  deleteWantedAuthor
};

const selectAuthors = createSelector(
  (state) => state.wanted.authors,
  (state) => state.authors.items,
  (wanted, authors) => {
    const setups = new Map();
    for (const author of authors) {
      if (author.initialScanPaths?.length) {
        for (const id of [author.addOptions?.audiobookSetupPendingImportId, author.addOptions?.ebookSetupPendingImportId]) {
          if (id) {
            setups.set(id, { authorId: author.id, paths: author.initialScanPaths });
          }
        }
      }
    }
    return { ...wanted, items: wanted.items.map((item) => ({ ...item, initialScan: setups.get(item.id) })) };
  }
);

export default withCurrentPage(connect(selectAuthors, mapDispatchToProps)(AuthorsConnector));
