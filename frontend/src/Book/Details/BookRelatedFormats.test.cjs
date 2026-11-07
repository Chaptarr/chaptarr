const test = require('node:test');
const assert = require('node:assert/strict');
const {
  buildAddFormatModalProps,
  canAddFormat,
  getBookFormatInstances,
  haveRelatedFormatInputsChanged,
  shouldShowFormatIdentityHelp
} = require('./BookRelatedFormats.cjs');

test('book details show the current format and only work-matched records in the other format', () => {
  const formats = [
    { bookId: 11, mediaType: 'audiobook', title: 'Dune' },
    { bookId: 12, mediaType: 'ebook', title: 'Dune', disambiguation: '1965' },
    { bookId: 13, mediaType: 'ebook', title: 'Dune: Illustrated' },
    { bookId: 14, mediaType: 'comic', title: 'Dune' }
  ];

  assert.deepEqual(getBookFormatInstances(formats, {
    id: 11,
    mediaType: 'audiobook',
    title: 'Dune',
    narrator: 'Simon Vance'
  }), {
    localAudiobookBooks: [
      { id: 11, mediaType: 'audiobook', title: 'Dune', narrator: 'Simon Vance', disambiguation: undefined }
    ],
    localEbookBooks: [
      { id: 12, mediaType: 'ebook', title: 'Dune', narrator: undefined, disambiguation: '1965' },
      { id: 13, mediaType: 'ebook', title: 'Dune: Illustrated', narrator: undefined, disambiguation: undefined }
    ]
  });
});

test('format instances ignore malformed records and duplicate current records', () => {
  assert.deepEqual(getBookFormatInstances([
    null,
    { bookId: 'not-an-id', mediaType: 'ebook', title: 'Dune' },
    { bookId: 14, mediaType: 'comic', title: 'Dune' },
    { bookId: 15, mediaType: ' EBOOK ', title: 'Dune' },
    { bookId: 11, mediaType: 'audiobook', title: 'Duplicate current row' }
  ], { id: 11, mediaType: 'audiobook', title: 'Dune' }), {
    localAudiobookBooks: [
      { id: 11, mediaType: 'audiobook', title: 'Dune', narrator: undefined, disambiguation: undefined }
    ],
    localEbookBooks: [
      { id: 15, mediaType: 'ebook', title: 'Dune', narrator: undefined, disambiguation: undefined }
    ]
  });
});

test('adding a missing format requires book and author provider identities', () => {
  assert.equal(canAddFormat({ foreignBookId: 'hc:123' }, { foreignAuthorId: 'hc:456' }), true);
  assert.equal(canAddFormat({ foreignBookId: 'hc:123' }, {}), false);
  assert.equal(canAddFormat({}, { foreignAuthorId: 'hc:456' }), false);
});

test('explains how to enable the add action only when the other format is missing', () => {
  const ebook = { mediaType: 'ebook', foreignBookId: 'hc:123' };
  const ebookWithoutCatalogId = { ...ebook, foreignBookId: '' };
  const author = { foreignAuthorId: 'hc:456' };
  const noOtherFormat = { localAudiobookBooks: [], localEbookBooks: [] };
  const matchingAudiobook = {
    localAudiobookBooks: [{ id: 20 }],
    localEbookBooks: []
  };

  assert.equal(shouldShowFormatIdentityHelp(ebook, {}, noOtherFormat), true);
  assert.equal(shouldShowFormatIdentityHelp({ foreignBookId: 'hc:123' }, {}, noOtherFormat, 'ebook'), true);
  assert.equal(shouldShowFormatIdentityHelp(ebookWithoutCatalogId, author, noOtherFormat), true);
  assert.equal(shouldShowFormatIdentityHelp(ebook, author, noOtherFormat), false);
  assert.equal(shouldShowFormatIdentityHelp(ebook, {}, matchingAudiobook), false);
});

test('refreshes related-format matches when catalog identities change', () => {
  const props = {
    bookId: 11,
    mediaType: 'ebook',
    book: { foreignBookId: 'hc:123' },
    author: { foreignAuthorId: 'hc:456' }
  };

  assert.equal(haveRelatedFormatInputsChanged(props, { ...props }), false);
  assert.equal(haveRelatedFormatInputsChanged(props, {
    ...props,
    book: { foreignBookId: 'hc:789' }
  }), true);
  assert.equal(haveRelatedFormatInputsChanged(props, {
    ...props,
    author: { foreignAuthorId: 'hc:999' }
  }), true);
  assert.equal(haveRelatedFormatInputsChanged(props, { ...props, bookId: 12 }), true);
  assert.equal(haveRelatedFormatInputsChanged(props, { ...props, mediaType: 'audiobook' }), true);
});

test('direct add uses the requested format, existing author, and known format records', () => {
  const book = {
    id: 11,
    title: 'Dune',
    mediaType: 'audiobook',
    foreignBookId: 'hc:work-123',
    images: [{ url: '/cover.jpg' }]
  };
  const author = { foreignAuthorId: 'hc:author-456', authorName: 'Frank Herbert', path: '/books/Frank Herbert' };
  const instances = getBookFormatInstances([], book);

  assert.deepEqual(buildAddFormatModalProps(book, author, instances, 'ebook'), {
    initialMediaType: 'ebook',
    foreignBookId: 'hc:work-123',
    foreignAuthorId: 'hc:author-456',
    book: { ...book, author },
    bookTitle: 'Dune',
    seriesTitle: undefined,
    disambiguation: undefined,
    authorName: 'Frank Herbert',
    overview: '',
    folder: '/books/Frank Herbert',
    images: [{ url: '/cover.jpg' }],
    localAudiobookBooks: [
      { id: 11, mediaType: 'audiobook', title: 'Dune', narrator: undefined, disambiguation: undefined }
    ],
    localEbookBooks: [],
    isExistingAuthor: true
  });
});
