import dayjs from 'Utilities/Date/dayjsSetup';
import intlFormatters from 'Utilities/Localization/formatters.cjs';
import { getLocale } from 'Utilities/Localization/locale';

const { formatUnit } = intlFormatters;

function formatReadableDuration(timeSpan) {
  if (timeSpan === null || timeSpan === undefined || timeSpan === '') {
    return '';
  }

  const duration = dayjs.duration(timeSpan);
  const totalHours = Math.floor(duration.asHours());
  const minutes = duration.get('minutes');
  const parts = [];

  if (totalHours > 0) {
    parts.push(formatUnit(totalHours, 'hour', { unitDisplay: 'short' }, getLocale()));
  }

  if (minutes > 0) {
    parts.push(formatUnit(minutes, 'minute', { unitDisplay: 'short' }, getLocale()));
  }

  if (parts.length === 0) {
    return formatUnit(0, 'minute', { unitDisplay: 'short' }, getLocale());
  }

  return parts.join(' ');
}

export default formatReadableDuration;
