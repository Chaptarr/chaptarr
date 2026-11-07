export default function getBookIndexItemMediaType(selectedMediaType, mediaType) {
  if (selectedMediaType === 'audiobook' || selectedMediaType === 'ebook') {
    return selectedMediaType;
  }

  return mediaType === 'ebook' ? 'ebook' : 'audiobook';
}
