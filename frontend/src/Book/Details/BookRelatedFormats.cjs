function normalizeMediaType(mediaType) {
  return (mediaType || '').toString().trim().toLowerCase();
}

function getBookFormatInstances(formats, currentBook) {
  const related = Array.isArray(formats) ? formats : [];
  const currentMediaType = normalizeMediaType(currentBook?.mediaType);
  const instances = [];

  if (
    Number.isInteger(Number(currentBook?.id)) &&
    Number(currentBook.id) > 0 &&
    ['audiobook', 'ebook'].includes(currentMediaType)
  ) {
    instances.push({
      id: Number(currentBook.id),
      mediaType: currentMediaType,
      title: currentBook.title,
      narrator: currentBook.narrator,
      disambiguation: currentBook.disambiguation
    });
  }

  related.forEach((format) => {
    const id = Number(format?.bookId);
    const mediaType = normalizeMediaType(format?.mediaType);

    if (
      !Number.isInteger(id) ||
      id <= 0 ||
      !['audiobook', 'ebook'].includes(mediaType) ||
      (currentMediaType && mediaType === currentMediaType) ||
      instances.some((instance) => instance.id === id)
    ) {
      return;
    }

    instances.push({
      id,
      mediaType,
      title: format.title,
      narrator: format.narrator,
      disambiguation: format.disambiguation
    });
  });

  return {
    localAudiobookBooks: instances.filter((instance) => instance.mediaType === 'audiobook'),
    localEbookBooks: instances.filter((instance) => instance.mediaType === 'ebook')
  };
}

function canAddFormat(book, author) {
  return !!(
    String(book?.foreignBookId ?? '').trim() &&
    String(author?.foreignAuthorId ?? '').trim()
  );
}

function haveRelatedFormatInputsChanged(previousProps, nextProps) {
  const previousBookId = String(previousProps.book?.foreignBookId ?? '').trim();
  const nextBookId = String(nextProps.book?.foreignBookId ?? '').trim();
  const previousAuthorId = String(previousProps.author?.foreignAuthorId ?? '').trim();
  const nextAuthorId = String(nextProps.author?.foreignAuthorId ?? '').trim();

  return (
    previousProps.bookId !== nextProps.bookId ||
    previousProps.mediaType !== nextProps.mediaType ||
    previousBookId !== nextBookId ||
    previousAuthorId !== nextAuthorId
  );
}

function shouldShowFormatIdentityHelp(book, author, instances, mediaTypeOverride) {
  if (canAddFormat(book, author)) {
    return false;
  }

  const mediaType = normalizeMediaType(book?.mediaType || mediaTypeOverride);
  let otherFormatBooks = null;

  if (mediaType === 'ebook') {
    otherFormatBooks = instances?.localAudiobookBooks;
  } else if (mediaType === 'audiobook') {
    otherFormatBooks = instances?.localEbookBooks;
  }

  return Array.isArray(otherFormatBooks) && otherFormatBooks.length === 0;
}

function buildAddFormatModalProps(book, author, instances, mediaType) {
  const title = book?.title || '';

  return {
    initialMediaType: mediaType,
    foreignBookId: book?.foreignBookId,
    foreignAuthorId: author?.foreignAuthorId,
    book: { ...book, author },
    bookTitle: title,
    seriesTitle: book?.seriesTitle,
    disambiguation: book?.disambiguation,
    authorName: author?.authorName || author?.authorNameLastFirst || title,
    overview: book?.overview || '',
    folder: author?.folder || author?.path,
    images: Array.isArray(book?.images) ? book.images : [],
    localAudiobookBooks: instances.localAudiobookBooks,
    localEbookBooks: instances.localEbookBooks,
    isExistingAuthor: true
  };
}

module.exports = {
  buildAddFormatModalProps,
  canAddFormat,
  getBookFormatInstances,
  haveRelatedFormatInputsChanged,
  shouldShowFormatIdentityHelp
};
