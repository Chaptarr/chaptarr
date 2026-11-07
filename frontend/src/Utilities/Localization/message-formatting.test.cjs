const assert = require('node:assert/strict');
const test = require('node:test');
const {
  resolveMessageFallback,
  resolvePluralKey,
  resolveSelectKey
} = require('./message-formatting.cjs');

test('resolves exact and locale plural categories before other', () => {
  const messages = {
    'books.=0': 'none',
    'books.one': 'one',
    'books.other': 'many',
    'books.few': 'few'
  };

  assert.equal(resolvePluralKey('books', 0, messages, 'en'), 'books.=0');
  assert.equal(resolvePluralKey('books', 1, messages, 'en'), 'books.one');
  assert.equal(resolvePluralKey('books', 3, messages, 'ar'), 'books.few');
  assert.equal(resolvePluralKey('books', 11, messages, 'ar'), 'books.other');
});

test('resolves select values and falls back to other', () => {
  const messages = {
    'import.mode.book': 'Book',
    'import.mode.other': 'Item'
  };

  assert.equal(resolveSelectKey('import.mode', 'book', messages), 'import.mode.book');
  assert.equal(resolveSelectKey('import.mode', 'author', messages), 'import.mode.other');
});


test('falls back to other, then the base message ID', () => {
  assert.equal(
    resolveMessageFallback('books.count', { 'books.count.other': '{count} books' }),
    '{count} books'
  );
  assert.equal(
    resolveMessageFallback('books.count', { 'books.count': '{count} book' }),
    '{count} book'
  );
  assert.equal(resolveMessageFallback('books.count', {}), 'books.count');
});
