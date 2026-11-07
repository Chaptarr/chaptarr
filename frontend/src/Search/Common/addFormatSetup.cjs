function hasValue(field) {
  const value =
    field &&
    typeof field === 'object' &&
    Object.prototype.hasOwnProperty.call(field, 'value') ?
      field.value :
      field;

  return (
    value !== undefined &&
    value !== null &&
    value !== '' &&
    value !== 0 &&
    value !== '0' &&
    value !== 'none'
  );
}

function getMissingAddFormatSetup(props) {
  const {
    selectedMediaType,
    audiobookRootFolderPath,
    ebookRootFolderPath,
    audiobookQualityProfileId,
    ebookQualityProfileId,
    isExistingAuthor,
    folder
  } = props;

  const requiredTypes =
    selectedMediaType === 'both' ? ['audiobook', 'ebook'] : [selectedMediaType];

  return requiredTypes.filter((mediaType) => {
    const rootFolder =
      mediaType === 'ebook' ? ebookRootFolderPath : audiobookRootFolderPath;
    const qualityProfile =
      mediaType === 'ebook' ? ebookQualityProfileId : audiobookQualityProfileId;
    const hasRootFolder =
      hasValue(rootFolder) || (isExistingAuthor && hasValue(folder));

    return !hasRootFolder || !hasValue(qualityProfile);
  });
}

module.exports = {
  getMissingAddFormatSetup
};
