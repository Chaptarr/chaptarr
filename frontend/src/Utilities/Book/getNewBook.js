import getNewAuthor from 'Utilities/Author/getNewAuthor';

function getNewBook(book, payload, mediaType) {
  const {
    searchForNewBook = false
  } = payload;

  // Pass the original payload to getNewAuthor so selected media-type settings are
  // included even when the author already exists and only the sibling format is new.
  if (book.author) {
    book.author = getNewAuthor(book.author, payload, mediaType);

    if (payload.monitor === 'specificBook') {
      // "None (Just this one)" means:
      // - Monitor the author for this media type (already handled by getNewAuthor)
      // - Don't monitor any existing books
      // - Don't monitor future books
      // - Only monitor this specific book

      book.author.addOptions.monitor = 'specificBook';
      const bookToMonitor = book.foreignId || book.foreignBookId || book.hardcoverBookId || book.goodreadsBookId;
      book.author.addOptions.booksToMonitor = [bookToMonitor];
      console.log('[getNewBook] Setting up "None (Just this one)" monitoring:', {
        monitor: 'specificBook',
        booksToMonitor: [bookToMonitor],
        foreignId: book.foreignId,
        hardcoverBookId: book.hardcoverBookId,
        goodreadsBookId: book.goodreadsBookId,
        ebookMonitorExisting: book.author.ebookMonitorExisting,
        audiobookMonitorExisting: book.author.audiobookMonitorExisting
      });

      // Make sure the book itself is monitored when using "None (Just this one)"
      book.monitored = true;
    }
  }

  // Search results can carry a local sibling ID when the other format already
  // exists. Adding a new media type must create/update by provider ID + mediaType,
  // never by reusing the sibling row's database ID.
  book.id = 0;
  book.localBookId = null;

  book.addOptions = {
    searchForNewBook
  };

  // Set media-type specific monitoring based on what's being added
  if (mediaType === 'audiobook') {
    book.audiobookMonitored = true;
    book.ebookMonitored = false;
  } else if (mediaType === 'ebook') {
    book.audiobookMonitored = false;
    book.ebookMonitored = true;
  } else {
    // Default behavior - add both (backwards compatible)
    book.audiobookMonitored = true;
    book.ebookMonitored = true;
  }

  book.monitored = book.audiobookMonitored || book.ebookMonitored;

  // Override if using "None (Just this one)" - ensure the book is monitored
  if (payload.monitor === 'specificBook') {
    book.monitored = true;
    // Media-type specific monitoring was already set above
  }

  // CRITICAL: Preserve editions from search result
  // The backend AddBookService requires at least one monitored edition
  const hasEditions = Array.isArray(book.editions) && book.editions.length > 0;
  if (hasEditions) {
    // Ensure at least one edition is monitored
    const hasMonitored = book.editions.some((e) => e.monitored);
    if (!hasMonitored && book.editions.length > 0) {
      console.warn(`[getNewBook] Book ${book.foreignId || book.title} has editions but none are monitored - setting first as monitored`);
      book.editions[0].monitored = true;
      book.editions[0].manualAdd = false;
    }
  } else {
    console.warn(`[getNewBook] Book ${book.foreignId || book.title} arrived without editions - creating default edition`);
    // Create a default edition to prevent backend crash
    book.editions = [{
      monitored: true,
      manualAdd: false,
      title: book.title,
      overview: book.overview || ''
    }];
  }

  // Ensure provider IDs are properly set (handle both camelCase and PascalCase from backend)
  book.hardcoverBookId = book.hardcoverBookId || book.HardcoverBookId;
  book.goodreadsBookId = book.goodreadsBookId || book.GoodreadsBookId;
  book.openLibraryWorkId = book.openLibraryWorkId || book.OpenLibraryWorkId;
  book.googleBooksId = book.googleBooksId || book.GoogleBooksId;

  // Pass mediaType as string to backend - it will parse it to the enum
  // The backend needs this to know which database instance to update (MediaType=0 or MediaType=1)
  book.mediaType = mediaType; // Will be "audiobook" or "ebook" (lowercase strings)
  console.log('[getNewBook] Book configured for', mediaType, '- audiobookMonitored:', book.audiobookMonitored, 'ebookMonitored:', book.ebookMonitored, 'mediaType:', book.mediaType);

  return book;
}

export default getNewBook;
