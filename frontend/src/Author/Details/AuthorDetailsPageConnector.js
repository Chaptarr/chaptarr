import { push } from 'connected-react-router';
import _ from 'lodash';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import NotFound from 'Components/NotFound';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import { setSelectedMediaType } from 'Store/Actions/appActions';
import { fetchAuthor } from 'Store/Actions/authorActions';
import { setAuthorDetailsFilterValue } from 'Store/Actions/authorDetailsActions';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';
import AuthorDetailsConnector from './AuthorDetailsConnector';
import styles from './AuthorDetails.css';

function createMapStateToProps() {
  return createSelector(
    (state, { match }) => match,
    (state) => state.authors,
    (match, authors) => {
      const authorId = parseInt(match.params.id, 10);
      const {
        isFetching,
        isPopulated,
        error,
        items
      } = authors;

      const authorIndex = _.findIndex(items, { id: authorId });

      if (authorIndex > -1) {
        return {
          isFetching,
          isPopulated,
          authorId
        };
      }

      return {
        isFetching,
        isPopulated,
        error
      };
    }
  );
}

const mapDispatchToProps = {
  push,
  setSelectedMediaType,
  fetchAuthor,
  setAuthorDetailsFilterValue
};

class AuthorDetailsPageConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    this.applySearchLink();
    const { authorId, isPopulated } = this.props;

    this.props.setAuthorDetailsFilterValue('');

    // If we have an authorId but authors aren't populated, fetch this specific author.
    // This supports deep-linking directly to the author details page.
    if (authorId && !isPopulated) {
      this.props.fetchAuthor({ id: authorId });
    }
  }

  componentDidUpdate(prevProps) {
    if (prevProps.location !== this.props.location) {
      this.applySearchLink();
    }
    const { authorId } = this.props;

    if (!authorId) {
      this.props.push(`${window.Chaptarr.urlBase}/`);
      return;
    }

    // If the author ID changed, fetch the new author data
    if (prevProps.authorId !== authorId && authorId) {
      this.props.setAuthorDetailsFilterValue('');
      this.props.fetchAuthor({ id: authorId });
    }
  }

  applySearchLink = () => {
    const query = new URLSearchParams(this.props.location?.search);
    const mediaType = query.get('mediaType');
    if (query.get('tab') === 'search' && ['audiobook', 'ebook'].includes(mediaType)) {
      this.props.setSelectedMediaType({ mediaType });
    }
  };

  //
  // Render

  render() {
    const {
      authorId,
      isFetching,
      isPopulated,
      error
    } = this.props;

    if (isFetching && !isPopulated) {
      return (
        <PageContent title={translate('Loading')}>
          <PageContentBody>
            <LoadingIndicator />
          </PageContentBody>
        </PageContent>
      );
    }

    if (!isFetching && !!error) {
      return (
        <div className={styles.errorMessage}>
          {getErrorMessage(error, 'Failed to load author from API')}
        </div>
      );
    }

    if (!authorId) {
      return (
        <NotFound
          message={translate('SorryThatAuthorCannotBeFound')}
        />
      );
    }

    return (
      <AuthorDetailsConnector
        key={authorId}
        id={authorId}
        location={this.props.location}
      />
    );
  }
}

AuthorDetailsPageConnector.propTypes = {
  authorId: PropTypes.number,
  location: PropTypes.object,
  setSelectedMediaType: PropTypes.func.isRequired,
  isFetching: PropTypes.bool.isRequired,
  isPopulated: PropTypes.bool.isRequired,
  error: PropTypes.object,
  match: PropTypes.shape({ params: PropTypes.shape({ id: PropTypes.string.isRequired }).isRequired }).isRequired,
  push: PropTypes.func.isRequired,
  fetchAuthor: PropTypes.func.isRequired,
  setAuthorDetailsFilterValue: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(AuthorDetailsPageConnector);
