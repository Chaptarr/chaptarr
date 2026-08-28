import { createSelector } from 'reselect';
import createAuthorSelector from './createAuthorSelector';

function createAuthorMetadataProfileSelector() {
  return createSelector(
    (state) => state.settings.metadataProfiles.items,
    createAuthorSelector(),
    (state, ownProps) => ownProps?.selectedMediaType,
    (metadataProfiles, author = {}, selectedMediaType) => {
      const mediaType = selectedMediaType === 'audiobook' || selectedMediaType === 'ebook' ?
        selectedMediaType :
        author.lastSelectedMediaType;
      const profileId = mediaType === 'ebook' ?
        author.ebookMetadataProfileId :
        author.audiobookMetadataProfileId;

      return metadataProfiles.find((profile) => {
        return profile.id === profileId;
      });
    }
  );
}

export default createAuthorMetadataProfileSelector;
