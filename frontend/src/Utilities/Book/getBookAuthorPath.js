function getBookAuthorPath(item) {
  const author = item?.author || {};
  const mediaType = String(item?.mediaType || '').toLowerCase();

  if (mediaType === 'ebook') {
    return author.ebookFolder || '';
  }

  return author.audiobookFolder || '';
}

export default getBookAuthorPath;
