// Audio extensions from MediaFileExtensions.cs
const AUDIO_EXTENSIONS = [
  '.flac', '.ape', '.wavpack', '.wav', '.alac',
  '.mp2', '.mp3', '.wma', '.m4a', '.m4p', '.m4b',
  '.aac', '.mp4a', '.ogg', '.oga', '.opus', '.vorbis', '.mka'
];

// Text extensions from MediaFileExtensions.cs
const TEXT_EXTENSIONS = [
  '.epub', '.kepub', '.mobi', '.azw3', '.azw', '.pdf'
];

export function getMediaTypeFromExtension(filePath, allowedExtensions = null) {
  if (!filePath) {
    return null;
  }

  const extension = filePath.substring(filePath.lastIndexOf('.')).toLowerCase();

  // If allowedExtensions is provided, check if the extension is allowed
  if (allowedExtensions && !allowedExtensions.includes(extension)) {
    return null;
  }

  if (AUDIO_EXTENSIONS.includes(extension)) {
    return 'audiobook';
  }

  if (TEXT_EXTENSIONS.includes(extension)) {
    return 'ebook';
  }

  return null;
}

export function isAudioFile(filePath, allowedExtensions = null) {
  return getMediaTypeFromExtension(filePath, allowedExtensions) === 'audiobook';
}

export function isTextFile(filePath, allowedExtensions = null) {
  return getMediaTypeFromExtension(filePath, allowedExtensions) === 'ebook';
}

export function isAllowedFile(filePath, allowedExtensions) {
  if (!filePath || !allowedExtensions) {
    return true; // If no restrictions, allow all
  }

  const extension = filePath.substring(filePath.lastIndexOf('.')).toLowerCase();
  return allowedExtensions.includes(extension);
}
