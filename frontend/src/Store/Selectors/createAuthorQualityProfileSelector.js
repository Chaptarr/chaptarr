import { createSelector } from 'reselect';
import createAuthorSelector from './createAuthorSelector';

function createAuthorQualityProfileSelector() {
  return createSelector(
    (state) => state.settings.qualityProfiles.items,
    createAuthorSelector(),
    (qualityProfiles, author = {}) => {
      // If no author or no quality profiles, return empty object to prevent undefined errors
      if (!author || !qualityProfiles || qualityProfiles.length === 0) {
        return { id: 0, name: '' };
      }
      
      // Determine which quality profile to use based on lastSelectedMediaType
      const qualityProfileId = author.lastSelectedMediaType === 'ebook' 
        ? author.ebookQualityProfileId 
        : author.audiobookQualityProfileId;
      
      // If no quality profile ID is set for this media type, return empty object
      if (!qualityProfileId) {
        return { id: 0, name: '' };
      }
        
      const profile = qualityProfiles.find((profile) => {
        return profile.id === qualityProfileId;
      });
      
      // Return found profile or empty object if not found
      return profile || { id: 0, name: '' };
    }
  );
}

export default createAuthorQualityProfileSelector;
