import { createSelector } from 'reselect';
import { getMediaTypeFromExtension } from 'Utilities/MediaFile/getMediaTypeFromExtension';

// Map quality IDs to their corresponding file extensions
const QUALITY_TO_EXTENSIONS = {
  // Text formats
  1: ['.pdf'], // PDF
  2: ['.mobi'], // MOBI
  3: ['.epub', '.kepub'], // EPUB
  4: ['.azw3', '.azw'], // AZW3

  // Audio formats
  10: ['.mp3', '.mp2', '.wma', '.m4a', '.m4p', '.aac', '.mp4a', '.ogg', '.oga', '.opus', '.vorbis'], // MP3
  11: ['.flac', '.ape', '.wavpack', '.wav', '.alac'], // FLAC
  12: ['.m4b'], // M4B
  13: ['.mka'], // Unknown Audio

  // MAM variations (they use the same extensions as their base types)
  20: ['.mp3', '.mp2', '.wma', '.m4a', '.m4p', '.aac', '.mp4a', '.ogg', '.oga', '.opus', '.vorbis'], // MP3 VIP Freeleech
  21: ['.flac', '.ape', '.wavpack', '.wav', '.alac'], // FLAC VIP Freeleech
  22: ['.m4b'], // M4B VIP Freeleech

  30: ['.mp3', '.mp2', '.wma', '.m4a', '.m4p', '.aac', '.mp4a', '.ogg', '.oga', '.opus', '.vorbis'], // MP3 VIP
  31: ['.flac', '.ape', '.wavpack', '.wav', '.alac'], // FLAC VIP
  32: ['.m4b'], // M4B VIP

  40: ['.mp3', '.mp2', '.wma', '.m4a', '.m4p', '.aac', '.mp4a', '.ogg', '.oga', '.opus', '.vorbis'], // MP3 Freeleech
  41: ['.flac', '.ape', '.wavpack', '.wav', '.alac'], // FLAC Freeleech
  42: ['.m4b'], // M4B Freeleech

  50: ['.mp3', '.mp2', '.wma', '.m4a', '.m4p', '.aac', '.mp4a', '.ogg', '.oga', '.opus', '.vorbis'], // MP3 Regular
  51: ['.flac', '.ape', '.wavpack', '.wav', '.alac'], // FLAC Regular
  52: ['.m4b'] // M4B Regular
};

function addExtension(ext, allowedExtensions, allowedAudioExtensions, allowedTextExtensions) {
  allowedExtensions.add(ext);

  const mediaType = getMediaTypeFromExtension(ext);
  if (mediaType === 'audiobook') {
    allowedAudioExtensions.add(ext);
  } else if (mediaType === 'ebook') {
    allowedTextExtensions.add(ext);
  }
}

function addAllowedItemExtensions(item, allowedExtensions, allowedAudioExtensions, allowedTextExtensions) {
  if (!item.allowed) {
    return;
  }

  const extensions = QUALITY_TO_EXTENSIONS[item.quality] || [];
  extensions.forEach((ext) => addExtension(ext, allowedExtensions, allowedAudioExtensions, allowedTextExtensions));

  if (item.items) {
    item.items.forEach((subItem) => addAllowedItemExtensions(subItem, allowedExtensions, allowedAudioExtensions, allowedTextExtensions));
  }
}

export function createAllowedFileExtensionsSelector() {
  return createSelector(
    (state) => state.settings.qualityProfiles.items,
    (qualityProfiles) => {
      const allowedExtensions = new Set();
      const allowedAudioExtensions = new Set();
      const allowedTextExtensions = new Set();

      qualityProfiles.forEach((profile) => {
        if (profile.items) {
          const items = typeof profile.items === 'string' ? JSON.parse(profile.items) : profile.items;
          items.forEach((item) => addAllowedItemExtensions(item, allowedExtensions, allowedAudioExtensions, allowedTextExtensions));
        }
      });

      return {
        allExtensions: Array.from(allowedExtensions),
        audioExtensions: Array.from(allowedAudioExtensions),
        textExtensions: Array.from(allowedTextExtensions)
      };
    }
  );
}

export default createAllowedFileExtensionsSelector;
