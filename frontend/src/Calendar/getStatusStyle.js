/* eslint max-params: 0 */
import dayjs from 'Utilities/Date/dayjsSetup';

function getStatusStyle(episodeNumber, downloading, startTime, isMonitored, percentOfBooks) {
  const currentTime = dayjs();

  if (percentOfBooks === 100) {
    return 'downloaded';
  }

  if (percentOfBooks > 0) {
    return 'partial';
  }

  if (downloading) {
    return 'downloading';
  }

  if (!isMonitored) {
    return 'unmonitored';
  }

  if (currentTime.isAfter(startTime)) {
    return 'missing';
  }

  return 'unreleased';
}

export default getStatusStyle;
