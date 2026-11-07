import intlFormatters from 'Utilities/Localization/formatters.cjs';
import { getLocale } from 'Utilities/Localization/locale';

const { formatPattern } = intlFormatters;

function formatTime(date, timeFormat, { includeMinuteZero = false, includeSeconds = false } = {}) {
  if (!date) {
    return '';
  }

  const time = date instanceof Date ? date : new Date(date);

  if (!Number.isFinite(time.getTime())) {
    return '';
  }

  if (includeSeconds) {
    timeFormat = timeFormat.replace(/\(?:mm\)?/, ':mm:ss');
  } else if (includeMinuteZero || time.getMinutes() !== 0) {
    timeFormat = timeFormat.replace('(:mm)', ':mm');
  } else {
    timeFormat = timeFormat.replace('(:mm)', '');
  }

  return formatPattern(time, timeFormat, getLocale());
}

export default formatTime;
