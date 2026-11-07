const assert = require('node:assert/strict');
const { test } = require('node:test');
const { getMissingAddFormatSetup } = require('./addFormatSetup.cjs');

test('both formats are ready when each has a root folder and quality profile', () => {
  assert.deepEqual(
    getMissingAddFormatSetup({
      selectedMediaType: 'both',
      audiobookRootFolderPath: { value: '/books/audio' },
      ebookRootFolderPath: { value: '/books/ebooks' },
      audiobookQualityProfileId: { value: 1 },
      ebookQualityProfileId: { value: 2 }
    }),
    []
  );
});

test('reports only the selected format that is missing setup', () => {
  assert.deepEqual(
    getMissingAddFormatSetup({
      selectedMediaType: 'both',
      audiobookRootFolderPath: { value: '/books/audio' },
      ebookRootFolderPath: { value: '/books/ebooks' },
      audiobookQualityProfileId: { value: 1 },
      ebookQualityProfileId: { value: 0 }
    }),
    ['ebook']
  );
});

test('an existing author folder satisfies the root requirement for an added book', () => {
  assert.deepEqual(
    getMissingAddFormatSetup({
      selectedMediaType: 'audiobook',
      isExistingAuthor: true,
      folder: '/books/audio/Author Name',
      audiobookQualityProfileId: { value: 1 }
    }),
    []
  );
});

test('missing root folders and profiles are reported for newly added authors', () => {
  assert.deepEqual(
    getMissingAddFormatSetup({
      selectedMediaType: 'audiobook',
      audiobookRootFolderPath: { value: '' },
      audiobookQualityProfileId: { value: 'none' }
    }),
    ['audiobook']
  );
});
