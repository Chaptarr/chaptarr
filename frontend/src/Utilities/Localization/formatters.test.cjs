const assert = require('node:assert/strict');
const test = require('node:test');
const {
  formatDate,
  formatDateParts,
  formatNumber,
  formatPattern,
  formatUnit,
  parseLocalizedNumber,
  pluralCategory,
  stepNumber
} = require('./formatters.cjs');

test('formats numbers and dates with the requested locale', () => {
  assert.equal(formatNumber(1234.5, { maximumFractionDigits: 1 }, 'de-DE'), '1.234,5');
  assert.equal(formatDate(new Date('2025-01-15T12:00:00Z'), {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    timeZone: 'UTC'
  }, 'de-DE'), '15.01.2025');
  assert.equal(formatDateParts(new Date('2025-01-15T12:00:00Z'), {
    month: '2-digit',
    timeZone: 'UTC'
  }, 'en-US').month, '01');
});

test('formats saved date and time patterns with localized names and digits', () => {
  const date = new Date(2024, 0, 2, 13, 5);
  const frenchMonth = new Intl.DateTimeFormat('fr', { month: 'long' }).format(date);

  assert.equal(formatPattern(date, 'YYYY-MM-DD', 'en'), '2024-01-02');
  assert.equal(formatPattern(date, 'MMMM D YYYY', 'fr'), `${frenchMonth} 2 2024`);
  assert.equal(formatPattern(date, '[on] ddd, MMM D', 'en'), 'on Tue, Jan 2');
  assert.equal(formatPattern(date, 'HH:mm', 'en'), '13:05');
  assert.equal(formatPattern(date, 'h:mm A', 'en'), '1:05 PM');
});

test('formats units using locale-aware names, digits, and plural forms', () => {
  assert.match(formatUnit(2, 'kilometer', { unitDisplay: 'short' }, 'fr-FR'), /km/);
  assert.equal(formatUnit(1, 'hour', { unitDisplay: 'long' }, 'en'), '1 hour');
  assert.equal(formatUnit(2, 'hour', { unitDisplay: 'long' }, 'en'), '2 hours');
  assert.equal(
    formatUnit(2, 'hour', { unitDisplay: 'long' }, 'fr-FR'),
    new Intl.NumberFormat('fr-FR', { style: 'unit', unit: 'hour', unitDisplay: 'long' }).format(2)
  );
});

test('parses localized grouping, decimals, and native digits to canonical numbers', () => {
  assert.equal(parseLocalizedNumber('1.234,5', { locale: 'de-DE' }), 1234.5);
  assert.equal(parseLocalizedNumber('١٬٢٣٤٫٥', { locale: 'ar', isFloat: true }), 1234.5);
  assert.equal(parseLocalizedNumber('1 234,5', { locale: 'fr-FR' }), 1234.5);
  assert.equal(parseLocalizedNumber('1,234', { locale: 'en-US', isFloat: false }), 1234);
  assert.equal(parseLocalizedNumber('1.5', { locale: 'de-DE' }), 1.5);
  assert.equal(parseLocalizedNumber('12,5', { locale: 'de-DE', isFloat: false }), null);
  assert.equal(parseLocalizedNumber('12x', { locale: 'en-US' }), null);
  assert.equal(parseLocalizedNumber('', { locale: 'en-US' }), null);
});

test('uses locale plural categories', () => {
  assert.equal(pluralCategory(1, 'en'), 'one');
  assert.equal(pluralCategory(2, 'en'), 'other');
  assert.equal(pluralCategory(0, 'ar'), 'zero');
  assert.equal(pluralCategory(3, 'ar'), 'few');
});

test('steps canonical numbers and clamps them to bounds', () => {
  assert.equal(stepNumber(1.2, 1, { step: 0.1 }), 1.3);
  assert.equal(stepNumber(null, 1, { step: 1, min: 5 }), 5);
  assert.equal(stepNumber(9.5, 1, { step: 1, max: 10 }), 10);
  assert.equal(stepNumber(1, -1, { step: 1, min: 0 }), 0);
  assert.equal(stepNumber(1, 1, { step: 0 }), null);
});
