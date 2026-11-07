import intlFormatters from 'Utilities/Localization/formatters.cjs';
import { getLocale } from 'Utilities/Localization/locale';

const { formatPattern } = intlFormatters;

function formatDate(date, dateFormat) {
  if (!date) {
    return '';
  }

  return formatPattern(date, dateFormat, getLocale());
}

export default formatDate;
