import intlFormatters from 'Utilities/Localization/formatters.cjs';
import { getLocale } from 'Utilities/Localization/locale';

const { formatUnit } = intlFormatters;

function formatAge(age, ageHours, ageMinutes) {
  age = Math.round(age);
  ageHours = parseFloat(ageHours);
  ageMinutes = ageMinutes && parseFloat(ageMinutes);

  if (age < 2 && ageHours) {
    if (ageHours < 2 && !!ageMinutes) {
      const minutes = Number(ageMinutes.toFixed(0));

      return formatUnit(minutes, 'minute', {
        unitDisplay: 'long',
        maximumFractionDigits: 0
      }, getLocale());
    }

    const hours = Number(ageHours.toFixed(1));

    return formatUnit(hours, 'hour', {
      unitDisplay: 'long',
      minimumFractionDigits: 1,
      maximumFractionDigits: 1
    }, getLocale());
  }

  return formatUnit(age, 'day', {
    unitDisplay: 'long',
    maximumFractionDigits: 0
  }, getLocale());
}

export default formatAge;
