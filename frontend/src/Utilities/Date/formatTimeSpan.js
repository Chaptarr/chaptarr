import dayjs from 'Utilities/Date/dayjsSetup';
import intlFormatters from 'Utilities/Localization/formatters.cjs';
import { getLocale } from 'Utilities/Localization/locale';
import translate from 'Utilities/String/translate';

const { formatNumber } = intlFormatters;

function formatTimeSpan(timeSpan) {
  if (!timeSpan) {
    return '';
  }

  const duration = dayjs.duration(timeSpan);
  const days = Math.floor(duration.asDays());
  const formatPart = (value) => formatNumber(value, {
    minimumIntegerDigits: 2,
    useGrouping: false
  }, getLocale());
  const hours = formatPart(duration.get('hours'));
  const minutes = formatPart(duration.get('minutes'));
  const seconds = formatPart(duration.get('seconds'));
  const time = `${hours}:${minutes}:${seconds}`;

  if (days > 0) {
    return translate('FormatTimeSpanDays', { days, time });
  }

  return time;
}

export default formatTimeSpan;
