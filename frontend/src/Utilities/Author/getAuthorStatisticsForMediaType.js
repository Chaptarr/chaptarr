export function getAuthorStatisticsForMediaType(author, selectedMediaType) {
  if (selectedMediaType === 'audiobook' && author.audiobookStatistics) {
    return author.audiobookStatistics;
  }

  if (selectedMediaType === 'ebook' && author.ebookStatistics) {
    return author.ebookStatistics;
  }

  return author.statistics || {};
}

export function getAuthorBookProgress(statistics = {}) {
  const bookCount = statistics.bookCount || 0;
  const availableBookCount = statistics.availableBookCount || 0;

  // Readarr assumes one file per book. Chaptarr audiobooks can have many
  // parts, so progress must count owned books rather than media files.
  return bookCount ? availableBookCount / bookCount * 100 : 100;
}

export const SEARCH_CONFIRMATION_THRESHOLD = 5;

export function shouldConfirmSearch(count) {
  return count > SEARCH_CONFIRMATION_THRESHOLD;
}

export function isAuthorSearchEnabled(author, mediaType) {
  return author?.[`${mediaType}Monitored`] === true && author?.[`${mediaType}QualityProfileId`] > 0;
}

export function isBookSearchEligible(book, author = book.author) {
  const mediaType = book.mediaType;
  return (mediaType === 'audiobook' || mediaType === 'ebook') &&
    book[`${mediaType}Monitored`] === true && isAuthorSearchEnabled(author, mediaType);
}

export function getAuthorSearchBookCount(author, selectedMediaType) {
  const mediaTypes = selectedMediaType ? [selectedMediaType] : ['audiobook', 'ebook'];
  return mediaTypes.reduce((count, mediaType) => {
    if (!isAuthorSearchEnabled(author, mediaType)) {
      return count;
    }

    return count + (author?.[`${mediaType}Statistics`]?.totalBookCount || 0);
  }, 0);
}
