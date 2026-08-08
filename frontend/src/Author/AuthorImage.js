import PropTypes from 'prop-types';
import React, { Component } from 'react';
import LazyLoad from 'react-lazyload';
import getImageUrl, { getDevicePixelRatio } from 'Utilities/Image/getImageUrl';

function selectOptimalImage(images, targetSize = 150) {
  return images
    .map((image) => {
      // Try to parse dimensions from Extension metadata
      let dimensions = null;
      try {
        if (image.Extension) {
          const metadata = JSON.parse(image.Extension);
          dimensions = metadata.dimensions;
        }
      } catch (e) {
        // Ignore parsing errors
      }

      let score = 0;

      // If we have dimensions, prefer images close to target size
      if (dimensions && dimensions.width && dimensions.height) {
        const avgSize = (dimensions.width + dimensions.height) / 2;
        const sizeDiff = Math.abs(avgSize - targetSize);

        // Penalize images much larger than target more heavily
        if (avgSize > targetSize * 2) {
          score = 500 - sizeDiff; // Lower base score for oversized images
        } else {
          score = 1000 - sizeDiff; // Higher score for closer to target
        }

        // Bonus for reasonable aspect ratios (not too wide/tall)
        const aspectRatio = dimensions.width / dimensions.height;
        if (aspectRatio >= 0.7 && aspectRatio <= 1.4) {
          score += 100;
        }
      } else {
        // No dimensions - give neutral score
        score = 500;
      }

      // Prefer primary photos from server, but not if user has made a selection
      try {
        if (image.Extension) {
          const metadata = JSON.parse(image.Extension);
          if (metadata.isPrimary === true && !metadata.userSelected) {
            score += 200;
          }
          // Strongly prefer user-selected photos
          if (metadata.userSelected === true) {
            score += 500;
          }
        }
      } catch (e) {
        // Ignore parsing errors
      }

      return { image, score };
    })
    .sort((a, b) => b.score - a.score) // Sort by score descending
    .map((item) => item.image)[0]; // Return the highest scoring image
}

function findImage(images, coverType, targetSize = 150) {
  const matchingImages = images.filter((image) => image.coverType === coverType);

  if (matchingImages.length === 0) {
    return null;
  }

  if (matchingImages.length === 1) {
    return matchingImages[0];
  }

  // Choose the best quality image based on the target size
  return selectOptimalImage(matchingImages, targetSize);
}

function getAvailableRenditionSizes(coverType) {
  if (coverType === 'banner') {
    return [35, 70];
  }

  if (coverType === 'fanart' || coverType === 'screenshot') {
    return [180, 360];
  }

  return [250, 500];
}

function getUrl(image, coverType, size) {
  const rawUrl = image?.url || image?.remoteUrl;

  if (!rawUrl) {
    return null;
  }

  const availableSizes = getAvailableRenditionSizes(coverType);

  // Check if this is an absolute URL (external/CDN)
  const isAbsolute = (/^https?:\/\//i).test(rawUrl);

  // For external URLs, return as-is. For local URLs, apply transformations
  return isAbsolute ?
    rawUrl :
    getImageUrl(rawUrl, {
      availableSizes,
      width: size,
      pixelRatio: getDevicePixelRatio()
    });
}

class AuthorImage extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    const {
      images,
      coverType,
      size
    } = props;

    const image = findImage(images, coverType, size);

    this.state = {
      image,
      url: getUrl(image, coverType, size),
      isLoaded: false,
      hasError: false,
      retryCount: 0,
      retryTimer: null
    };
  }

  componentDidMount() {
    if (!this.state.url && this.props.onError) {
      this.props.onError();
    }
  }

  componentDidUpdate() {
    const {
      images,
      coverType,
      placeholder,
      size,
      onError
    } = this.props;

    const {
      image,
      url,
      hasError
    } = this.state;

    const nextImage = findImage(images, coverType, size);
    const nextUrl = nextImage ? getUrl(nextImage, coverType, size) : null;

    if (nextUrl !== url) {
      // Clear any pending retry timer when image changes
      if (this.state.retryTimer) {
        clearTimeout(this.state.retryTimer);
      }

      this.setState({
        image: nextImage,
        url: nextUrl,
        hasError: false,
        isLoaded: false, // Reset isLoaded to trigger image reload
        retryCount: 0,
        retryTimer: null
      });
    } else if (!nextImage && image) {
      this.setState({
        image: nextImage,
        url: placeholder,
        hasError: false
      });

      if (onError) {
        onError();
      }
    } else if (hasError && nextImage && url !== getUrl(nextImage, coverType, size)) {
      // If we had an error but the URL has changed (e.g., from external to local), retry
      this.setState({
        url: getUrl(nextImage, coverType, size),
        hasError: false,
        isLoaded: false
      });
    }
  }

  componentWillUnmount() {
    if (this.state.retryTimer) {
      clearTimeout(this.state.retryTimer);
    }
  }

  //
  // Listeners

  onError = () => {
    const { url, retryCount } = this.state;
    const maxRetries = 3;

    console.error(`[AuthorImage] Failed to load image (attempt ${retryCount + 1}/${maxRetries + 1}):`, url);

    // Only retry for certain types of images (MediaCover images that might not be ready yet)
    const shouldRetry = url && url.includes('/MediaCover/') && retryCount < maxRetries;

    if (shouldRetry) {
      // Exponential backoff: 1s, 2s, 4s
      const retryDelay = Math.pow(2, retryCount) * 1000;

      const retryTimer = setTimeout(() => {
        console.log(`[AuthorImage] Retrying image load after ${retryDelay}ms:`, url);

        // Force reload by clearing the image src and setting it again
        this.setState({
          retryCount: retryCount + 1,
          hasError: false,
          isLoaded: false,
          retryTimer: null
        });
      }, retryDelay);

      this.setState({
        retryTimer,
        hasError: true // Temporarily show error state
      });
    } else {
      // Max retries reached or non-retryable image
      this.setState({
        hasError: true,
        retryTimer: null
      });

      if (this.props.onError) {
        this.props.onError();
      }
    }
  };

  onLoad = () => {
    // Clear any pending retry timer on successful load
    if (this.state.retryTimer) {
      clearTimeout(this.state.retryTimer);
    }

    this.setState({
      isLoaded: true,
      hasError: false,
      retryCount: 0,
      retryTimer: null
    });

    if (this.props.onLoad) {
      this.props.onLoad();
    }
  };

  //
  // Render

  render() {
    const {
      className,
      style,
      placeholder,
      size,
      lazy,
      overflow,
      blurBackground,
      usePlaceholderOnError
    } = this.props;

    const blurStyle = {
      ...style,
      objectFit: 'fill',
      filter: 'blur(8px)',
      WebkitFilter: 'blur(8px)'
    };

    const {
      url,
      hasError
    } = this.state;

    if (hasError || !url) {
      if (url && !usePlaceholderOnError) {
        // A provider photo exists but is temporarily unavailable. The author
        // placeholder represents "no provider photo", so do not flash it while
        // retrying or after a transient image error.
        return (
          <span
            className={className}
            style={style}
            aria-hidden={true}
          />
        );
      }

      return (
        <img
          className={className}
          style={style}
          src={placeholder}
        />
      );
    }

    if (lazy) {
      return (
        <LazyLoad
          height={size}
          offset={100}
          overflow={overflow}
          placeholder={
            usePlaceholderOnError ?
              <img
                className={className}
                style={style}
                src={placeholder}
              /> :
              <span
                className={className}
                style={style}
                aria-hidden={true}
              />
          }
        >
          <img
            className={className}
            style={style}
            src={url}
            onError={this.onError}
            onLoad={this.onLoad}
            rel="noreferrer"
          />
        </LazyLoad>
      );
    }

    return (
      <>
        {
          blurBackground ?
            <img
              style={blurStyle}
              src={url || placeholder}
              onError={this.onError}
              onLoad={this.onLoad}
            /> :
            null
        }

        <img
          className={className}
          style={style}
          src={url || placeholder}
          onError={this.onError}
          onLoad={this.onLoad}
        />
      </>
    );
  }
}

AuthorImage.propTypes = {
  className: PropTypes.string,
  style: PropTypes.object,
  images: PropTypes.arrayOf(PropTypes.object).isRequired,
  coverType: PropTypes.string.isRequired,
  placeholder: PropTypes.string.isRequired,
  size: PropTypes.number.isRequired,
  lazy: PropTypes.bool.isRequired,
  overflow: PropTypes.bool.isRequired,
  blurBackground: PropTypes.bool.isRequired,
  usePlaceholderOnError: PropTypes.bool.isRequired,
  onError: PropTypes.func,
  onLoad: PropTypes.func
};

AuthorImage.defaultProps = {
  size: 250,
  lazy: true,
  overflow: false,
  blurBackground: false,
  usePlaceholderOnError: false
};

export default AuthorImage;
