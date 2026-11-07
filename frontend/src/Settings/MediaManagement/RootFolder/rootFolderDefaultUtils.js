import { getRootFolderMediaTypes } from 'Helpers/Props/folderTypes';

export const rootFolderMediaTypes = {
  AUDIOBOOK: 'audiobook',
  EBOOK: 'ebook'
};

const rootFolderProfileFields = {
  audiobook: {
    quality: 'audiobookQualityProfileId',
    metadata: 'audiobookMetadataProfileId'
  },
  ebook: {
    quality: 'ebookQualityProfileId',
    metadata: 'ebookMetadataProfileId'
  }
};

export function cleanRootFolderPath(path) {
  if (!path) {
    return '';
  }

  if (path === '/') {
    return path;
  }

  if ((/^[a-zA-Z]:[\\/]{1}$/).test(path)) {
    return path;
  }

  return path.replace(/[\\/]+$/, '');
}

export function rootFolderSupportsMediaType(rootFolder, mediaType) {
  return getRootFolderMediaTypes(rootFolder).includes(mediaType);
}

export function hasRootFolderProfileDefault(rootFolder, mediaType, profileType) {
  const profileField = rootFolderProfileFields[mediaType]?.[profileType];

  if (!profileField) {
    return false;
  }

  const profileId = rootFolder?.[profileField] ?? rootFolder?.[mediaType]?.[`${profileType}ProfileId`];
  const numericProfileId = Number(profileId);

  return profileId != null && Number.isInteger(numericProfileId) && numericProfileId > 0;
}

export function getCompatibleRootFolders(rootFolders, mediaType) {
  return (rootFolders || []).filter((rootFolder) => {
    return !!cleanRootFolderPath(rootFolder?.path) &&
      rootFolderSupportsMediaType(rootFolder, mediaType);
  });
}

export function getRootFoldersWithCandidate(rootFolders, candidateRootFolder) {
  const candidatePath = cleanRootFolderPath(candidateRootFolder?.path);

  if (!candidatePath) {
    return rootFolders || [];
  }

  const candidateId = candidateRootFolder?.id;
  const candidate = {
    ...candidateRootFolder,
    path: candidatePath
  };

  let hasCandidate = false;
  const updatedRootFolders = (rootFolders || []).map((rootFolder) => {
    const isSameRootFolder = candidateId ?
      rootFolder.id === candidateId :
      cleanRootFolderPath(rootFolder.path) === candidatePath;

    if (isSameRootFolder) {
      hasCandidate = true;
      return {
        ...rootFolder,
        ...candidate
      };
    }

    return rootFolder;
  });

  return hasCandidate ? updatedRootFolders : updatedRootFolders.concat(candidate);
}
