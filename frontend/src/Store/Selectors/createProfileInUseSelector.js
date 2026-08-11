import some from 'lodash/some';
import { createSelector } from 'reselect';
import createAllAuthorsSelector from './createAllAuthorsSelector';

function createProfileInUseSelector(profileProp) {
  return createSelector(
    (state, { id }) => id,
    createAllAuthorsSelector(),
    (state) => state.settings.importLists.items,
    (state) => state.settings.rootFolders?.items || [],
    (id, author, lists, rootFolders) => {
      if (!id) {
        return false;
      }

      const matchesProfile = (item) => {
        if (profileProp === 'metadataProfileId') {
          return item?.metadataProfileId === id ||
            item?.audiobookMetadataProfileId === id ||
            item?.ebookMetadataProfileId === id ||
            item?.audiobook?.metadataProfileId === id ||
            item?.ebook?.metadataProfileId === id;
        }

        return item?.[profileProp] === id;
      };

      if (some(author, matchesProfile) || some(lists, matchesProfile) || some(rootFolders, matchesProfile)) {
        return true;
      }

      return false;
    }
  );
}

export default createProfileInUseSelector;
