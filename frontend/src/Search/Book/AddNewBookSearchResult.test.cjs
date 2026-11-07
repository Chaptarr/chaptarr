const assert = require('node:assert/strict');
const { test } = require('node:test');
const { shouldShowMissingCatalogIdentityHelp } = require('./AddNewBookSearchResult.cjs');

test('shows a visible explanation when a missing format cannot be added', () => {
  assert.equal(shouldShowMissingCatalogIdentityHelp(false, [], []), true);
  assert.equal(shouldShowMissingCatalogIdentityHelp(false, [{ id: 1 }], []), true);
  assert.equal(shouldShowMissingCatalogIdentityHelp(false, [], [{ id: 2 }]), true);
});

test('hides the explanation when both formats exist or required IDs are present', () => {
  assert.equal(shouldShowMissingCatalogIdentityHelp(false, [{ id: 1 }], [{ id: 2 }]), false);
  assert.equal(shouldShowMissingCatalogIdentityHelp(true, [], []), false);
});
