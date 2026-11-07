function currentLocale(locale) {
  if (locale) {
    return String(locale).replace(/_/g, '-');
  }

  if (typeof document !== 'undefined' && document.documentElement && document.documentElement.lang) {
    return document.documentElement.lang.replace(/_/g, '-');
  }

  if (typeof navigator !== 'undefined' && navigator.language) {
    return navigator.language;
  }

  return 'en';
}

const dateFormatterCache = new Map();

function getDatePartsForOptions(date, options, locale) {
  const language = currentLocale(locale);
  const cacheKey = language + ':' + JSON.stringify(options);
  let formatter = dateFormatterCache.get(cacheKey);

  if (!formatter) {
    formatter = new Intl.DateTimeFormat(language, options);
    dateFormatterCache.set(cacheKey, formatter);
  }

  return formatter.formatToParts(date).reduce((parts, part) => {
    parts[part.type] = part.value;
    return parts;
  }, {});
}

function getDatePart(date, options, part, locale) {
  return getDatePartsForOptions(date, options, locale)[part] || '';
}

function formatNumber(value, options, locale) {
  if (value == null || !Number.isFinite(Number(value))) {
    return '';
  }

  return new Intl.NumberFormat(currentLocale(locale), options || {}).format(Number(value));
}

function formatDate(value, options, locale) {
  const date = value instanceof Date ? value : new Date(value);

  if (!Number.isFinite(date.getTime())) {
    return '';
  }

  return new Intl.DateTimeFormat(currentLocale(locale), options || {}).format(date);
}

function formatDateParts(value, options, locale) {
  const date = value instanceof Date ? value : new Date(value);

  if (!Number.isFinite(date.getTime())) {
    return {};
  }

  return getDatePartsForOptions(date, options || {}, locale);
}

function formatUnit(value, unit, options, locale) {
  if (value == null || !Number.isFinite(Number(value))) {
    return '';
  }

  const numberOptions = {
    ...(options || {}),
    style: 'unit',
    unit,
    unitDisplay: (options && options.unitDisplay) || 'short'
  };

  try {
    return new Intl.NumberFormat(currentLocale(locale), numberOptions).format(Number(value));
  } catch {
    return formatNumber(value, options, locale) + ' ' + unit;
  }
}

function formatPattern(value, pattern, locale) {
  const date = value instanceof Date ? value : new Date(value);

  if (!Number.isFinite(date.getTime())) {
    return '';
  }

  const language = currentLocale(locale);
  const tokens = /\[([^\]]*)\]|YYYY|dddd|MMMM|ddd|MMM|YY|MM|DD|HH|hh|mm|ss|M|D|H|h|m|s|A|a|ZZ|Z/g;
  const formatters = {
    YYYY: () => getDatePart(date, { year: 'numeric' }, 'year', language),
    YY: () => getDatePart(date, { year: '2-digit' }, 'year', language),
    MMMM: () => getDatePart(date, { month: 'long' }, 'month', language),
    MMM: () => getDatePart(date, { month: 'short' }, 'month', language),
    MM: () => getDatePart(date, { month: '2-digit' }, 'month', language),
    M: () => getDatePart(date, { month: 'numeric' }, 'month', language),
    dddd: () => getDatePart(date, { weekday: 'long' }, 'weekday', language),
    ddd: () => getDatePart(date, { weekday: 'short' }, 'weekday', language),
    DD: () => getDatePart(date, { day: '2-digit' }, 'day', language),
    D: () => getDatePart(date, { day: 'numeric' }, 'day', language),
    HH: () => getDatePart(date, { hour: '2-digit', hourCycle: 'h23' }, 'hour', language),
    H: () => getDatePart(date, { hour: 'numeric', hourCycle: 'h23' }, 'hour', language),
    hh: () => getDatePart(date, { hour: '2-digit', hourCycle: 'h12' }, 'hour', language),
    h: () => getDatePart(date, { hour: 'numeric', hourCycle: 'h12' }, 'hour', language),
    mm: () => getDatePart(date, { hour: 'numeric', hourCycle: 'h23', minute: '2-digit' }, 'minute', language),
    m: () => getDatePart(date, { hour: 'numeric', hourCycle: 'h23', minute: 'numeric' }, 'minute', language),
    ss: () => getDatePart(
      date,
      { hour: 'numeric', minute: '2-digit', second: '2-digit', hourCycle: 'h23' },
      'second',
      language
    ),
    s: () => getDatePart(
      date,
      { hour: 'numeric', minute: 'numeric', second: 'numeric', hourCycle: 'h23' },
      'second',
      language
    ),
    A: () => getDatePart(date, { hour: 'numeric', hourCycle: 'h12' }, 'dayPeriod', language),
    a: () => getDatePart(date, { hour: 'numeric', hourCycle: 'h12' }, 'dayPeriod', language)
      .toLocaleLowerCase(language),
    Z: () => formatTimeZoneOffset(date, false),
    ZZ: () => formatTimeZoneOffset(date, true)
  };

  return String(pattern || '').replace(tokens, (match, literal) => {
    if (literal !== undefined) {
      return literal;
    }

    return formatters[match] ? formatters[match]() : match;
  });
}

function formatTimeZoneOffset(date, compact) {
  const offset = -date.getTimezoneOffset();
  const sign = offset >= 0 ? '+' : '-';
  const absolute = Math.abs(offset);
  const hours = String(Math.floor(absolute / 60)).padStart(2, '0');
  const minutes = String(absolute % 60).padStart(2, '0');

  return compact ? sign + hours + minutes : sign + hours + ':' + minutes;
}

function stepNumber(value, direction, options) {
  const settings = options || {};
  const step = Number(settings.step == null ? 1 : settings.step);

  if (!Number.isFinite(step) || step <= 0 || (direction !== 1 && direction !== -1)) {
    return null;
  }

  const current = Number.isFinite(Number(value)) ? Number(value) : 0;
  const precision = Math.min(10, Math.max(
    decimalPlaces(current),
    decimalPlaces(step)
  ));
  let next = Number((current + (direction * step)).toFixed(precision));

  if (settings.min != null && next < settings.min) {
    next = settings.min;
  } else if (settings.max != null && next > settings.max) {
    next = settings.max;
  }

  return Number.isFinite(next) ? next : null;
}

function decimalPlaces(value) {
  const source = String(value).toLowerCase();
  const exponentIndex = source.indexOf('e');
  const coefficient = exponentIndex === -1 ? source : source.slice(0, exponentIndex);
  const exponent = exponentIndex === -1 ? 0 : Number(source.slice(exponentIndex + 1));
  const decimalIndex = coefficient.indexOf('.');
  const fractionLength = decimalIndex === -1 ? 0 : coefficient.length - decimalIndex - 1;

  return Math.max(0, fractionLength - exponent);
}

function pluralCategory(value, locale) {
  const count = Number(value);

  return new Intl.PluralRules(currentLocale(locale)).select(
    Number.isFinite(count) ? count : 0
  );
}

function parseLocalizedNumber(input, options) {
  if (input == null || String(input).trim() === '') {
    return null;
  }

  const settings = options || {};
  const locale = currentLocale(settings.locale);
  const isFloat = settings.isFloat !== false;
  const numberParts = new Intl.NumberFormat(locale).formatToParts(1234567.89);
  const groupSymbols = new Set(
    numberParts.filter((part) => part.type === 'group').map((part) => part.value)
  );
  const decimalPart = numberParts.find((part) => part.type === 'decimal');
  const decimalSymbol = decimalPart ? decimalPart.value : '.';
  const minusPart = new Intl.NumberFormat(locale).formatToParts(-1).find((part) => part.type === 'minusSign');
  const minusSymbol = minusPart ? minusPart.value : '-';
  const digits = new Map();

  for (let digit = 0; digit <= 9; digit++) {
    digits.set(
      new Intl.NumberFormat(locale, { useGrouping: false }).format(digit),
      String(digit)
    );
  }

  const digitZeroes = [
    0x0660, 0x06f0, 0x0966, 0x09e6, 0x0a66, 0x0ae6, 0x0b66,
    0x0be6, 0x0c66, 0x0ce6, 0x0d66, 0x0e50, 0x0ed0, 0x0f20,
    0x1040, 0x17e0, 0x1810
  ];
  for (const zero of digitZeroes) {
    for (let digit = 0; digit <= 9; digit++) {
      digits.set(String.fromCodePoint(zero + digit), String(digit));
    }
  }

  let source = String(input)
    .normalize('NFKC')
    .replace(/[\u061c\u200e\u200f\u202a-\u202e\u2066-\u2069]/g, '')
    .trim();

  for (const [localizedDigit, latinDigit] of digits) {
    source = source.split(localizedDigit).join(latinDigit);
  }

  source = source.split(minusSymbol).join('-').replace(/\u2212/g, '-');

  let effectiveDecimal = source.includes('٫') ? '٫' : decimalSymbol;
  if (
    effectiveDecimal === decimalSymbol &&
    decimalSymbol !== '.' &&
    source.includes('.') &&
    !source.includes(decimalSymbol)
  ) {
    const dotIsGrouping = groupSymbols.has('.') && /\.\d{3}$/.test(source);
    if (!dotIsGrouping) {
      effectiveDecimal = '.';
    }
  }

  const separators = new Set([...groupSymbols, '٬', ' ', '\u00a0', '\u202f', '\u2009']);
  if (effectiveDecimal === '.') {
    separators.delete('.');
  }

  let normalized = '';
  for (const character of source) {
    if (character === effectiveDecimal) {
      normalized += '.';
    } else if (separators.has(character)) {
      continue;
    } else if (/^[0-9+\-.]$/.test(character)) {
      normalized += character;
    } else {
      return null;
    }
  }

  const validNumber = isFloat ?
    /^[+-]?(?:\d+(?:\.\d*)?|\.\d+)$/ :
    /^[+-]?\d+$/;

  if (!validNumber.test(normalized)) {
    return null;
  }

  const parsed = Number(normalized);

  return Number.isFinite(parsed) ? parsed : null;
}

module.exports = {
  currentLocale,
  formatDate,
  formatDateParts,
  formatNumber,
  formatPattern,
  formatUnit,
  parseLocalizedNumber,
  pluralCategory,
  stepNumber
};
