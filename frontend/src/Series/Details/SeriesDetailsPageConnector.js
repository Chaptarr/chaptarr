import findIndex from 'lodash/findIndex';
import { push } from 'connected-react-router';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import NotFound from 'Components/NotFound';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import { fetchSeriesById } from 'Store/Actions/seriesActions';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';
import SeriesDetailsConnector from './SeriesDetailsConnector';
import styles from './SeriesDetails.css';

function createMapStateToProps() {
  return createSelector(
    (state, { match }) => match,
    (state) => state.series,
    (match, series) => {
      const requestedLocalSeriesId = match.params.localSeriesId;
      const {
        isFetching,
        isPopulated,
        error,
        items
      } = series;

      const seriesIndex = findIndex(items, { localSeriesId: requestedLocalSeriesId });

      if (seriesIndex > -1) {
        return {
          isFetching,
          isPopulated,
          requestedLocalSeriesId,
          localSeriesId: requestedLocalSeriesId
        };
      }

      return {
        requestedLocalSeriesId,
        isFetching,
        isPopulated,
        error
      };
    }
  );
}

const mapDispatchToProps = {
  fetchSeriesById,
  push
};

class SeriesDetailsPageConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    this.fetchSeriesIfMissing();
  }

  componentDidUpdate(prevProps) {
    if (prevProps.requestedLocalSeriesId !== this.props.requestedLocalSeriesId) {
      this.requestedSeriesId = null;
      this.fetchSeriesIfMissing();
    } else if (prevProps.isFetching && !this.props.isFetching) {
      this.fetchSeriesIfMissing();
    }
  }

  requestedSeriesId = null;

  //
  // Control

  fetchSeriesIfMissing = () => {
    const {
      requestedLocalSeriesId,
      localSeriesId,
      isFetching,
      fetchSeriesById: dispatchFetchSeriesById
    } = this.props;

    if (requestedLocalSeriesId &&
        !localSeriesId &&
        !isFetching &&
        this.requestedSeriesId !== requestedLocalSeriesId) {
      this.requestedSeriesId = requestedLocalSeriesId;
      dispatchFetchSeriesById({ id: requestedLocalSeriesId });
    }
  };

  //
  // Render

  render() {
    const {
      requestedLocalSeriesId,
      localSeriesId,
      isFetching,
      isPopulated,
      error
    } = this.props;

    if (!requestedLocalSeriesId) {
      return (
        <NotFound
          message={translate('SorryThatSeriesCannotBeFound')}
        />
      );
    }

    if (!localSeriesId && !error) {
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
          {getErrorMessage(error, 'Failed to load series from API')}
        </div>
      );
    }

    if (!isFetching && isPopulated && localSeriesId) {
      return (
        <SeriesDetailsConnector
          localSeriesId={localSeriesId}
        />
      );
    }

    return null;
  }
}

SeriesDetailsPageConnector.propTypes = {
  requestedLocalSeriesId: PropTypes.string,
  localSeriesId: PropTypes.string,
  match: PropTypes.shape({ params: PropTypes.shape({ localSeriesId: PropTypes.string.isRequired }).isRequired }).isRequired,
  fetchSeriesById: PropTypes.func.isRequired,
  push: PropTypes.func.isRequired,
  isFetching: PropTypes.bool.isRequired,
  isPopulated: PropTypes.bool.isRequired,
  error: PropTypes.object
};

export default connect(createMapStateToProps, mapDispatchToProps)(SeriesDetailsPageConnector);
