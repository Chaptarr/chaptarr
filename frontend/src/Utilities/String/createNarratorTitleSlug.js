function createNarratorTitleSlug(narratorName) {
  if (!narratorName || typeof narratorName !== 'string') {
    return '';
  }

  return narratorName
    .trim()
    .toLowerCase()
    .replace(/ /g, '-')          // Replace spaces with hyphens
    .replace(/\./g, '')          // Remove dots
    .replace(/'/g, '')           // Remove single quotes
    .replace(/&/g, 'and')        // Replace & with 'and'
    .replace(/[()]/g, '')        // Remove parentheses
    .replace(/--+/g, '-')        // Replace multiple hyphens with single hyphen
    .replace(/^-+|-+$/g, '');    // Remove leading/trailing hyphens
}

export default createNarratorTitleSlug;