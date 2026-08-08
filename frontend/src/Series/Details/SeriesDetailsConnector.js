import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import SeriesDetails from './SeriesDetails';

function createMapStateToProps() {
  return createSelector(
    (state, { localSeriesId }) => localSeriesId,
    (state) => state.series,
    (localSeriesId, series) => {
      const seriesItem = series.items.find((s) => s.localSeriesId === localSeriesId);
      
      if (!seriesItem) {
        return {};
      }

      return {
        ...seriesItem
      };
    }
  );
}

class SeriesDetailsConnector extends Component {
  render() {
    return (
      <SeriesDetails
        {...this.props}
      />
    );
  }
}

SeriesDetailsConnector.propTypes = {
  localSeriesId: PropTypes.string.isRequired
};

export default connect(createMapStateToProps)(SeriesDetailsConnector);