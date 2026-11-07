const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const views = [
  ['overview', 'Overview/BookIndexOverview.js'],
  ['poster', 'Posters/BookIndexPoster.js'],
  ['table', 'Table/BookIndexRow.js']
];

for (const [viewName, relativePath] of views) {
  test(`${viewName} book rows open the author editor in the row media type`, () => {
    const sourcePath = path.join(__dirname, relativePath);
    const source = fs.readFileSync(sourcePath, 'utf8');
    const modalStart = source.indexOf('<EditAuthorModalConnector');
    assert.notEqual(modalStart, -1, 'expected the view to render EditAuthorModalConnector');

    const modalEnd = source.indexOf('/>', modalStart);
    assert.notEqual(modalEnd, -1, 'expected a complete author editor element');
    const modalElement = source.slice(modalStart, modalEnd);

    assert.match(source, /const rowMediaType = getBookIndexItemMediaType\(selectedMediaType,\s*mediaType\);/);
    assert.match(modalElement, /initialMediaType=\{rowMediaType\}/);
  });
}

test('the author editor initializes its format from the requested row media type', () => {
  const sourcePath = path.join(__dirname, '../../Author/Edit/EditAuthorModalContent.js');
  const source = fs.readFileSync(sourcePath, 'utf8');

  assert.match(source, /const requestedMediaType = props\.initialMediaType;/);
  assert.match(source, /selectedMediaType:\s*initialMediaType/);
});
