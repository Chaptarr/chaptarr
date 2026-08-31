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
