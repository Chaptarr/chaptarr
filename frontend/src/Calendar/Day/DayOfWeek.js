import classNames from 'classnames';
import dayjs from 'Utilities/Date/dayjsSetup';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import * as calendarViews from 'Calendar/calendarViews';
import getRelativeDate from 'Utilities/Date/getRelativeDate';
import styles from './DayOfWeek.css';

class DayOfWeek extends Component {

  //
  // Render

  render() {
    const {
      date,
      view,
      isTodaysDate,
      calendarWeekColumnHeader,
      shortDateFormat,
      showRelativeDates
    } = this.props;

    const highlightToday = view !== calendarViews.MONTH && isTodaysDate;
    const dayjsDate = dayjs(date);
    let formatedDate = dayjsDate.format('dddd');

    if (view === calendarViews.WEEK) {
      formatedDate = dayjsDate.format(calendarWeekColumnHeader);
    } else if (view === calendarViews.FORECAST) {
      formatedDate = getRelativeDate(date, shortDateFormat, showRelativeDates);
    }

    return (
      <div className={classNames(
        styles.dayOfWeek,
        view === calendarViews.DAY && styles.isSingleDay,
        highlightToday && styles.isToday
      )}
      >
        {formatedDate}
      </div>
    );
  }
}

DayOfWeek.propTypes = {
  date: PropTypes.string.isRequired,
  view: PropTypes.string.isRequired,
  isTodaysDate: PropTypes.bool.isRequired,
  calendarWeekColumnHeader: PropTypes.string.isRequired,
  shortDateFormat: PropTypes.string.isRequired,
  showRelativeDates: PropTypes.bool.isRequired
};

export default DayOfWeek;
