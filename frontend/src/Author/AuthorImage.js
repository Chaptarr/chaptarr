import PropTypes from 'prop-types';
import React, { Component } from 'react';
import LazyLoad from 'react-lazyload';
import getImageUrl, { getDevicePixelRatio } from 'Utilities/Image/getImageUrl';

export function findImage(images, coverType) {
  const candidates = images.filter((image) => image.coverType === coverType);
  // The backend exposes its chosen author rendition locally without changing carousel order.
  // Book covers keep their existing first-cover behavior.
  return (coverType !== 'cover' && candidates.find((image) => image.url?.includes('/MediaCover/'))) || candidates[0];
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

function getUrl(image, coverType, size, allowTemporaryUrl) {
  let rawUrl = image?.url || image?.remoteUrl;
  const hasTemporaryUrl = image?.temporaryUrl !== undefined && image.temporaryUrl !== null;

  if (hasTemporaryUrl) {
    rawUrl = allowTemporaryUrl ? image.temporaryUrl : null;
  }

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

export { getUrl as getCoverImageUrl };

class AuthorImage extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    const {
      images,
      coverType,
      size,
      allowTemporaryUrl
    } = props;

    const image = findImage(images, coverType);

    this.state = {
      image,
      url: getUrl(image, coverType, size, allowTemporaryUrl),
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
      allowTemporaryUrl,
      onError
    } = this.props;

    const {
      image,
      url,
      hasError
    } = this.state;

    const nextImage = findImage(images, coverType);
    const nextUrl = nextImage ? getUrl(nextImage, coverType, size, allowTemporaryUrl) : null;

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
    } else if (hasError && nextImage && url !== getUrl(nextImage, coverType, size, allowTemporaryUrl)) {
      // If we had an error but the URL has changed (e.g., from external to local), retry
      this.setState({
        url: getUrl(nextImage, coverType, size, allowTemporaryUrl),
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
        this.props.onError(url);
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
      showPlaceholderWhileLoading
    } = this.props;

    const blurStyle = {
      ...style,
      objectFit: 'fill',
      filter: 'blur(8px)',
      WebkitFilter: 'blur(8px)'
    };

    const {
      url,
      hasError,
      retryTimer
    } = this.state;

    if (hasError || !url) {
      if (url && retryTimer && !showPlaceholderWhileLoading) {
        // Keep loading/retry states quiet, but show a placeholder after failure
        // rather than leaving an author tile permanently blank.
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
            showPlaceholderWhileLoading ?
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
  showPlaceholderWhileLoading: PropTypes.bool.isRequired,
  allowTemporaryUrl: PropTypes.bool,
  onError: PropTypes.func,
  onLoad: PropTypes.func
};

AuthorImage.defaultProps = {
  size: 250,
  lazy: true,
  overflow: false,
  blurBackground: false,
  showPlaceholderWhileLoading: false,
  allowTemporaryUrl: false
};

export default AuthorImage;
