import dayjs from 'Utilities/Date/dayjsSetup';
import formatTime from 'Utilities/Date/formatTime';
import isInNextWeek from 'Utilities/Date/isInNextWeek';
import isToday from 'Utilities/Date/isToday';
import isTomorrow from 'Utilities/Date/isTomorrow';
import isYesterday from 'Utilities/Date/isYesterday';
import translate from 'Utilities/String/translate';

function getRelativeDate(date, shortDateFormat, showRelativeDates, { timeFormat, includeSeconds = false, timeForToday = false } = {}) {
  if (!date) {
    return null;
  }

  const isTodayDate = isToday(date);

  if (isTodayDate && timeForToday && timeFormat) {
    return formatTime(date, timeFormat, { includeMinuteZero: true, includeSeconds });
  }

  if (!showRelativeDates) {
    return dayjs(date).format(shortDateFormat);
  }

  if (isYesterday(date)) {
    return translate('Yesterday');
  }

  if (isTodayDate) {
    return translate('Today');
  }

  if (isTomorrow(date)) {
    return translate('Tomorrow');
  }

  if (isInNextWeek(date)) {
    return dayjs(date).format('dddd');
  }

  return dayjs(date).format(shortDateFormat);
}

export default getRelativeDate;
