function isLibraryBook(book) {
  const isMonitored = book.mediaType === 'audiobook' ? book.audiobookMonitored :
    book.mediaType === 'ebook' && book.ebookMonitored;

  return !!isMonitored || (book.statistics?.bookFileCount || 0) > 0;
}

export default isLibraryBook;
