import PropTypes from 'prop-types';
import React from 'react';
import QueueDetails from 'Activity/Queue/QueueDetails';
import BookQuality from 'Book/BookQuality';
import Icon from 'Components/Icon';
import Label from 'Components/Label';
import ProgressBar from 'Components/ProgressBar';
import { icons, kinds, sizes } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './BookStatus.css';

function BookStatus(props) {
  const {
    grabbed,
    isAvailable,
    monitored,
    bookFile,
    queueItem
  } = props;

  const hasBookFile = !!bookFile;

  if (queueItem) {
    const {
      size,
      sizeleft
    } = queueItem;

    const progress = size ? 100 - (sizeleft / size) * 100 : 0;

    return (
      <div className={styles.center}>
        <QueueDetails
          {...queueItem}
          progressBar={
            <ProgressBar
              progress={progress}
              kind={kinds.PRIMARY}
              size={sizes.MEDIUM}
            />
          }
        />
      </div>
    );
  }

  if (grabbed) {
    return (
      <div className={styles.center}>
        <Icon
          name={icons.DOWNLOADING}
          title={translate('BookIsDownloading')}
        />
      </div>
    );
  }

  if (hasBookFile) {
    const quality = bookFile.quality;

    return (
      <div className={styles.center}>
        <BookQuality
          title={quality.quality.name}
          size={bookFile.size}
          quality={quality}
          isMonitored={monitored}
          isCutoffNotMet={bookFile.qualityCutoffNotMet}
        />
      </div>
    );
  }

  if (!monitored) {
    return (
      <div className={styles.center}>
        <Label
          title={translate('NotMonitored')}
          kind={kinds.WARNING}
        >
          {translate('NotMonitored')}
        </Label>
      </div>
    );
  }

  if (isAvailable) {
    return (
      <div className={styles.center}>
        <Label
          title={translate('BookAvailableButMissing')}
          kind={kinds.DANGER}
        >
          {translate('Missing')}
        </Label>
      </div>
    );
  }

  return (
    <div className={styles.center}>
      <Label
        title={translate('FutureRelease')}
        kind={kinds.INFO}
      >
        {translate('FutureRelease')}
      </Label>
    </div>
  );
}

BookStatus.propTypes = {
  grabbed: PropTypes.bool,
  isAvailable: PropTypes.bool,
  monitored: PropTypes.bool,
  bookFile: PropTypes.object,
  queueItem: PropTypes.object
};

export default BookStatus;
