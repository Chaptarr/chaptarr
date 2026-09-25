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

export function getAuthorSearchBookCount(author, selectedMediaType) {
  const mediaTypes = selectedMediaType ? [selectedMediaType] : ['audiobook', 'ebook'];
  return mediaTypes.reduce((count, mediaType) => {
    if (author?.[`${mediaType}Monitored`] !== true || !(author?.[`${mediaType}QualityProfileId`] > 0)) {
      return count;
    }

    return count + (author?.[`${mediaType}Statistics`]?.totalBookCount || 0);
  }, 0);
}
