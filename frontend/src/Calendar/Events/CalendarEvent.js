import classNames from 'classnames';
import dayjs from 'Utilities/Date/dayjsSetup';
import PropTypes from 'prop-types';
import React, { memo, useState, useCallback } from 'react';
import getStatusStyle from 'Calendar/getStatusStyle';
import Icon from 'Components/Icon';
import Link from 'Components/Link/Link';
import { icons } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import CalendarEventQueueDetails from './CalendarEventQueueDetails';
import styles from './CalendarEvent.css';

function CalendarEvent(props) {
  const {
    id,
    author,
    title,
    titleSlug,
    releaseDate,
    monitored,
    statistics,
    grabbed,
    queueItem,
    colorImpairedMode,
    onEventModalOpenToggle
  } = props;

  const [isDetailsModalOpen] = useState(false);

  const onPress = useCallback(() => {
    onEventModalOpenToggle(true);
  }, [onEventModalOpenToggle]);

  const onDetailsModalClose = useCallback(() => {
    onEventModalOpenToggle(false);
  }, [onEventModalOpenToggle]);

  if (!author) {
    return null;
  }

  const startTime = dayjs(releaseDate);
  const downloading = !!(queueItem || grabbed);
  const isMonitored = author.monitored && monitored;
  const statusStyle = getStatusStyle(id, downloading, startTime, isMonitored, statistics.percentOfBooks);

  return (
    <div>
      <Link
        className={classNames(
          styles.event,
          styles[statusStyle],
          colorImpairedMode && 'colorImpaired'
        )}
        component="div"
        onPress={onPress}
      >
        <div className={styles.info}>
          <div className={styles.authorName}>
            <Link to={`/author/${author.id}`}>
              {author.authorName}
            </Link>
          </div>

          {
            !!queueItem &&
              <span className={styles.statusIcon}>
                <CalendarEventQueueDetails
                  {...queueItem}
                />
              </span>
          }

          {
            !queueItem && grabbed &&
              <Icon
                className={styles.statusIcon}
                name={icons.DOWNLOADING}
                title={translate('BookIsDownloading')}
              />
          }
        </div>

        <div className={styles.bookInfo}>
          <div className={styles.bookTitle}>
            <Link to={`/book/${id}`}>
              {title}
            </Link>
          </div>
        </div>
      </Link>
    </div>
  );
}

CalendarEvent.propTypes = {
  id: PropTypes.number.isRequired,
  author: PropTypes.object.isRequired,
  title: PropTypes.string.isRequired,
  titleSlug: PropTypes.string.isRequired,
  statistics: PropTypes.object.isRequired,
  releaseDate: PropTypes.string.isRequired,
  monitored: PropTypes.bool.isRequired,
  grabbed: PropTypes.bool,
  queueItem: PropTypes.object,
  // timeFormat: PropTypes.string.isRequired,
  colorImpairedMode: PropTypes.bool.isRequired,
  onEventModalOpenToggle: PropTypes.func.isRequired
};

CalendarEvent.defaultProps = {
  statistics: {
    percentOfBooks: 0
  }
};

export default memo(CalendarEvent);
