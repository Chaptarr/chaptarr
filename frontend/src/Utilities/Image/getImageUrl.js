/**
 * Returns an image URL.
 *
 * The backend media cover endpoints use filename renditions rather than
 * query-param resizing (`poster-250.jpg`, not `poster.jpg?w=250`).
 * @param {string} imageUrl - The original image URL
 * @param {Object} options - Reserved for future use
 * @returns {string} The optimized image URL
 */
export default function getImageUrl(imageUrl, options = {}) {
  if (!imageUrl) {
    return null;
  }

  const {
    availableSizes = [],
    pixelRatio = 1,
    width
  } = options;

  if (!width || !availableSizes.length || imageUrl.includes('/MediaCoverProxy/')) {
    return imageUrl;
  }

  const desiredWidth = width * pixelRatio;
  const sortedSizes = [...availableSizes].sort((a, b) => a - b);
  const renditionSize = sortedSizes.find((size) => size >= desiredWidth) ??
    sortedSizes[sortedSizes.length - 1];
  const queryIndex = imageUrl.indexOf('?');
  const path = queryIndex === -1 ? imageUrl : imageUrl.substring(0, queryIndex);
  const query = queryIndex === -1 ? '' : imageUrl.substring(queryIndex);

  if (!path.includes('/MediaCover/')) {
    return imageUrl;
  }

  // On-demand author photos are stored as URL-identity variants such as
  // `poster-97708e34485fdce4.jpg`. They are already the final local asset and
  // do not have `-250`/`-500` renditions. Appending a rendition suffix here
  // fabricates a path that cannot exist and leaves the poster blank.
  if ((/-[0-9a-f]{16}\.[^./?]+$/i).test(path)) {
    return imageUrl;
  }

  const renditionPath = path.replace(
    /(?:-\d+)?(\.[^./?]+)$/i,
    `-${renditionSize}$1`
  );

  return renditionPath === path ? imageUrl : `${renditionPath}${query}`;
}

// Helper function to get device pixel ratio capped at 2
export function getDevicePixelRatio() {
  return Math.min(Math.ceil(window.devicePixelRatio || 1), 2);
}
