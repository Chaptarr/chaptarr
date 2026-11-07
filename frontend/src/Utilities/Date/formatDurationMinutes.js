import intlFormatters from 'Utilities/Localization/formatters.cjs';
import { getLocale } from 'Utilities/Localization/locale';

const { formatUnit } = intlFormatters;

function formatDurationMinutes(minutes) {
  if (minutes > 0) {
    const totalMinutes = Math.round(minutes);
    const hours = Math.floor(totalMinutes / 60);
    const mins = totalMinutes % 60;
    const format = (value, unit) => {
      return formatUnit(value, unit, {
        unitDisplay: 'narrow'
      }, getLocale());
    };

    if (hours > 0 && mins > 0) {
      return `${format(hours, 'hour')} ${format(mins, 'minute')}`;
    }

    if (hours > 0) {
      return format(hours, 'hour');
    }

    return format(mins, 'minute');
  }

  return '';
}

export default formatDurationMinutes;
