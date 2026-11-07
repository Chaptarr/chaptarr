import dayjs from 'Utilities/Date/dayjsSetup';
import intlFormatters from 'Utilities/Localization/formatters.cjs';
import { getLocale } from 'Utilities/Localization/locale';

const { formatUnit } = intlFormatters;

function formatShortTimeSpan(timeSpan) {
  if (!timeSpan) {
    return '';
  }

  const duration = dayjs.duration(timeSpan);
  const hours = Math.floor(duration.asHours());
  const minutes = Math.floor(duration.asMinutes());
  const seconds = Math.floor(duration.asSeconds());

  if (hours > 0) {
    return formatUnit(hours, 'hour', { unitDisplay: 'long' }, getLocale());
  }

  if (minutes > 0) {
    return formatUnit(minutes, 'minute', { unitDisplay: 'long' }, getLocale());
  }

  return formatUnit(seconds, 'second', { unitDisplay: 'long' }, getLocale());
}

export default formatShortTimeSpan;
