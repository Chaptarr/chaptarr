import { createSelector } from 'reselect';
import createBookAuthorSelector from './createBookAuthorSelector';
import createBookSelector from './createBookSelector';

function createBookQualityProfileSelector() {
  const selectBook = createBookSelector();
  const selectAuthor = createBookAuthorSelector();

  return createSelector(
    (state) => state.settings.qualityProfiles.items,
    selectBook,
    selectAuthor,
    (qualityProfiles, book, author) => {
      if (!author) {
        return {};
      }

      const mediaType = String(book?.mediaType || author.lastSelectedMediaType || 'audiobook').toLowerCase();
      const qualityProfileId = mediaType === 'ebook'
        ? author.ebookQualityProfileId
        : author.audiobookQualityProfileId;

      return qualityProfiles.find((profile) => profile.id === qualityProfileId) || {};
    }
  );
}

export default createBookQualityProfileSelector;
