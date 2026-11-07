import translate from 'Utilities/String/translate';
import formatDate from './formatDate';
import formatTime from './formatTime';
import isToday from './isToday';
import isTomorrow from './isTomorrow';
import isYesterday from './isYesterday';

function getRelativeDay(date, includeRelativeDate) {
  if (!includeRelativeDate) {
    return '';
  }

  if (isYesterday(date)) {
    return translate('Yesterday');
  }

  if (isToday(date)) {
    return translate('Today');
  }

  if (isTomorrow(date)) {
    return translate('Tomorrow');
  }

  return '';
}

function formatDateTime(date, dateFormat, timeFormat, { includeSeconds = false, includeRelativeDay = false } = {}) {
  if (!date) {
    return '';
  }

  const relativeDay = getRelativeDay(date, includeRelativeDay);
  const formattedDate = formatDate(date, dateFormat);
  const formattedTime = formatTime(date, timeFormat, { includeMinuteZero: true, includeSeconds });

  return `${relativeDay}${formattedDate} ${formattedTime}`;
}

export default formatDateTime;
