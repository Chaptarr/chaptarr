const test = require('node:test');
const assert = require('node:assert/strict');
const { getBookshelfWorkGroups, getWorkProviderIds } = require('./workGroups.cjs');

test('groups an ebook and audiobook only when they share a work-level provider ID', () => {
  const audiobook = { id: 1, title: 'Dune', mediaType: 'audiobook', hardcoverBookId: '123' };
  const ebook = { id: 2, title: 'Dune', mediaType: 'ebook', hardcoverBookId: 'hc:123' };
  const unrelated = { id: 3, title: 'Dune', mediaType: 'ebook', hardcoverBookId: 'hc:456' };

  const groups = getBookshelfWorkGroups([audiobook, unrelated, ebook]);

  assert.equal(groups.length, 2);
  assert.deepEqual(groups[0].books, [audiobook, ebook]);
  assert.deepEqual(groups[1].books, [unrelated]);
});

test('cross-provider work IDs can link a record without merging edition-only IDs', () => {
  const audiobook = { id: 1, mediaType: 'audiobook', goodreadsWorkId: '987' };
  const ebook = { id: 2, mediaType: 'ebook', foreignBookId: 'gr:987' };
  const editionOnly = { id: 3, mediaType: 'ebook', foreignBookId: 'az:B000123' };

  assert.deepEqual(getWorkProviderIds(editionOnly), []);
  assert.deepEqual(getBookshelfWorkGroups([audiobook, ebook, editionOnly]).map((group) => group.books), [
    [audiobook, ebook],
    [editionOnly]
  ]);
});

test('uses recognized remote work IDs and ignores unrelated provider namespaces', () => {
  const audiobook = {
    id: 1,
    mediaType: 'audiobook',
    workProviderIds: ['OL:OL123W', 'rn:123']
  };
  const ebook = {
    id: 2,
    mediaType: 'ebook',
    workProviderIds: ['ol:ol123w', 'rn:456']
  };

  assert.deepEqual(getWorkProviderIds(audiobook), ['ol:ol123w']);
  assert.deepEqual(getBookshelfWorkGroups([audiobook, ebook]).map((group) => group.books), [[audiobook, ebook]]);
});

test('rejects mismatched and malformed provider IDs before grouping', () => {
  const audiobook = {
    id: 1,
    mediaType: 'audiobook',
    hardcoverBookId: 'gr:123',
    workProviderIds: ['hc:edition:456', 'rn:789', 'hc:System.Collections.Generic.List']
  };
  const ebook = {
    id: 2,
    mediaType: 'ebook',
    hardcoverBookId: 'hc:456'
  };

  assert.deepEqual(getWorkProviderIds(audiobook), []);
  assert.deepEqual(getBookshelfWorkGroups([audiobook, ebook]).map((group) => group.books), [[audiobook], [ebook]]);
});

test('a repeated title without shared work identity remains separate', () => {
  const books = [
    { id: 1, title: 'Emma', mediaType: 'audiobook' },
    { id: 2, title: 'Emma', mediaType: 'ebook' }
  ];

  assert.deepEqual(getBookshelfWorkGroups(books).map((group) => group.books), books.map((book) => [book]));
});
