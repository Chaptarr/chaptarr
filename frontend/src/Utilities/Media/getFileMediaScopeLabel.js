import translate from 'Utilities/String/translate';

export default function getFileMediaScopeLabel(mediaType) {
  if (mediaType === 'ebook') {
    return translate('MediaScopeEbookFiles');
  }

  if (mediaType === 'audiobook') {
    return translate('MediaScopeAudiobookFiles');
  }

  return translate('MediaScopeFiles');
}
