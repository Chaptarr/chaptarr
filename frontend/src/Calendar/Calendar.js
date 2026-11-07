import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import { kinds } from 'Helpers/Props';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';
import AgendaConnector from './Agenda/AgendaConnector';
import * as calendarViews from './calendarViews';
import CalendarDaysConnector from './Day/CalendarDaysConnector';
import DaysOfWeekConnector from './Day/DaysOfWeekConnector';
import CalendarHeaderConnector from './Header/CalendarHeaderConnector';
import styles from './Calendar.css';

class Calendar extends Component {

  //
  // Render

  render() {
    const {
      isFetching,
      isPopulated,
      error,
      view,
      onRetryPress
    } = this.props;

    return (
      <div className={styles.calendar}>
        {
          isFetching && !isPopulated &&
            <LoadingIndicator />
        }

        {
          !isFetching && !!error &&
            <Alert kind={kinds.DANGER} role="alert">
              <div>{getErrorMessage(error, translate('UnableToLoadTheCalendar'))}</div>
              <Button size="small" onPress={onRetryPress}>
                {translate('Retry')}
              </Button>
            </Alert>
        }

        {
          !error && isPopulated && view === calendarViews.AGENDA &&
            <div className={styles.calendarContent}>
              <CalendarHeaderConnector />
              <AgendaConnector />
            </div>
        }

        {
          !error && isPopulated && view !== calendarViews.AGENDA &&
            <div className={styles.calendarContent}>
              <CalendarHeaderConnector />
              <DaysOfWeekConnector />
              <CalendarDaysConnector />
            </div>
        }
      </div>
    );
  }
}

Calendar.propTypes = {
  isFetching: PropTypes.bool.isRequired,
  isPopulated: PropTypes.bool.isRequired,
  error: PropTypes.object,
  onRetryPress: PropTypes.func.isRequired,
  view: PropTypes.string.isRequired
};

export default Calendar;
