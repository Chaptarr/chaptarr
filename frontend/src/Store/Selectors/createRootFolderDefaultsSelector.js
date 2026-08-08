import { createSelector } from 'reselect';
import { FolderType, coerceFolderType } from 'Helpers/Props/folderTypes';

function createRootFolderDefaultsSelector() {
  return createSelector(
    (state) => state.settings.rootFolders.items,
    (rootFolders) => {
      const audiobookFolder = rootFolders.find((folder) => coerceFolderType(folder.folderType) === FolderType.Audiobook) ||
        rootFolders.find((folder) => coerceFolderType(folder.folderType) === FolderType.Mixed);
      const ebookFolder = rootFolders.find((folder) => coerceFolderType(folder.folderType) === FolderType.Ebook) ||
        rootFolders.find((folder) => coerceFolderType(folder.folderType) === FolderType.Mixed);
      
      return {
        audiobookRootFolderPath: audiobookFolder ? audiobookFolder.path : '',
        ebookRootFolderPath: ebookFolder ? ebookFolder.path : ''
      };
    }
  );
}

export default createRootFolderDefaultsSelector;
