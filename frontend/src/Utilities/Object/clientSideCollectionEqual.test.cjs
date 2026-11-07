const assert = require('node:assert/strict');
const test = require('node:test');
const clientSideCollectionEqual = require('./clientSideCollectionEqual.cjs');

test('collection state changes are detected when the item list stays empty', () => {
  const before = { items: [], isFetching: true, isPopulated: false, error: null };
  const after = { items: [], isFetching: false, isPopulated: true, error: null };

  assert.equal(clientSideCollectionEqual(before, after), false);
});

test('collection item changes with the same id are detected', () => {
  const before = { items: [{ id: 1, title: 'Old title' }], totalItems: 1 };
  const after = { items: [{ id: 1, title: 'Updated title' }], totalItems: 1 };

  assert.equal(clientSideCollectionEqual(before, after), false);
});

test('equivalent collection state remains memoizable across new arrays', () => {
  const before = { items: [{ id: 1, title: 'The Hobbit' }], isPopulated: true };
  const after = { items: [{ id: 1, title: 'The Hobbit' }], isPopulated: true };

  assert.equal(clientSideCollectionEqual(before, after), true);
});

test('item order changes are detected', () => {
  const before = { items: [{ id: 1 }, { id: 2 }] };
  const after = { items: [{ id: 2 }, { id: 1 }] };

  assert.equal(clientSideCollectionEqual(before, after), false);
});
