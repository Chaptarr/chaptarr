import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { createPortal } from 'react-dom';
import Icon from 'Components/Icon';
import { icons } from 'Helpers/Props';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import getPathWithUrlBase from 'Utilities/getPathWithUrlBase';
import AuthorImage, { findImage } from './AuthorImage';
import styles from './AuthorPoster.css';

const defaultPosterPlaceholder = '/Content/Images/author-placeholder-muted.png';
const sandersonEasterEggImage = `${window.Chaptarr?.urlBase ?? ''}/Content/Images/sanderson-easter-egg.jpg`;
const brandonSandersonProviderIds = new Set(['hc:204214', 'gr:38550']);

// Helper functions for stable image key generation
function normalizePosterKey(url) {
  if (!url) return '';
  // Remove query parameters
  const noQuery = url.split('?')[0];
  // Normalize size variants to base name (filename-250.jpg -> filename.jpg)
  // This handles any filename pattern, not just "poster"
  return noQuery.replace(/-\d+\.(jpg|jpeg|png|webp)$/i, '.$1');
}

function getImageKey(img) {
  // The local URL is shared by the author's current canonical poster. The
  // remote URL is the stable identity of an individual carousel choice.
  const url = img?.remoteUrl || img?.url || '';
  // If normalization fails, fall back to using the original URL as the key
  const normalized = normalizePosterKey(url);
  return normalized || url;
}

function getSavedPhotoKey(authorId) {
  try {
    return authorId ? normalizePosterKey(localStorage.getItem(`authorPhotoChoice:${authorId}`)) || null : null;
  } catch (e) {
    return null;
  }
}

function preloadImage(url) {
  return new Promise((resolve, reject) => {
    const image = new Image();

    image.onload = resolve;
    image.onerror = () => reject(new Error(`Failed to preload author photo: ${url}`));
    image.src = url;
  });
}

// Keep one rendition per photo without discarding the backend's local choice.
function deduplicatePosterImages(images) {
  const posters = images.filter(img => img.coverType === 'poster');
  const byKey = new Map();
  
  posters.forEach(img => {
    const key = getImageKey(img);
    if (!key) return; // Skip images with no URL at all
    
    const src = img.remoteUrl || img.url || '';
    // Extract size from filename (e.g., filename-250.jpg -> 250)
    const match = src.match(/-(\d+)\.(jpg|jpeg|png|webp)$/i);
    const size = match ? parseInt(match[1], 10) : 0; // 0 for original or unknown
    
    const current = byKey.get(key);
    // Prefer local availability; retain the size preference when availability is equal.
    if (!current || findImage(size > current._size ? [img, current] : [current, img], 'poster') === img) {
      byKey.set(key, { ...img, _size: size });
    }
  });
  
  // Return images without the temporary _size property
  return Array.from(byKey.values()).map(({ _size, ...img }) => img);
}

class AuthorPoster extends Component {
  constructor(props) {
    super(props);
    
    this.state = {
      selectedPhotoKey: getSavedPhotoKey(props.authorId),
      isHovering: false,
      isLoading: false,
      failedPhotoKeys: new Set(),
      loadedPhotoUrls: {},         // Unique local URLs returned by on-demand loads
      showSandersonOverlay: false
    };
    
    this.requestSeq = 0;  // Guard against race conditions
    this.sandersonOverlayTimer = null;
  }

  componentDidUpdate(prevProps) {
    if (prevProps.authorId !== this.props.authorId) {
      this.requestSeq++;
      this.setState({
        selectedPhotoKey: getSavedPhotoKey(this.props.authorId),
        failedPhotoKeys: new Set(),
        loadedPhotoUrls: {},
        isLoading: false
      });
    }
  }

  componentWillUnmount() {
    this.requestSeq++;
    if (this.sandersonOverlayTimer) {
      clearTimeout(this.sandersonOverlayTimer);
      this.sandersonOverlayTimer = null;
    }
  }

  //
  // Listeners

  getCurrentPhotoIndex = (posterImages = deduplicatePosterImages(this.props.images)) => {
    // A saved choice follows the photo, never its position in a refreshed list.
    // Without an explicit choice, follow the backend's current default.
    const keyIndex = posterImages.findIndex((img) => getImageKey(img) === this.state.selectedPhotoKey);
    if (keyIndex !== -1) {
      return keyIndex;
    }
    const hashIndex = posterImages.findIndex((img) => img.hash && img.hash === this.props.selectedPosterHash);
    return hashIndex === -1 ? Math.max(0, posterImages.indexOf(findImage(posterImages, 'poster'))) : hashIndex;
  };

  getNextValidIndex = (direction) => {
    const { images } = this.props;
    const posterImages = deduplicatePosterImages(images);
    const currentPhotoIndex = this.getCurrentPhotoIndex(posterImages);
    const { failedPhotoKeys } = this.state;
    
    if (posterImages.length <= 1) return currentPhotoIndex;
    
    const step = direction === 'next' ? 1 : -1;
    const n = posterImages.length;
    let targetIndex = (currentPhotoIndex + step + n) % n;
    
    // Skip known failed photos even when the API changes their order.
    const seen = new Set();
    while (failedPhotoKeys.has(getImageKey(posterImages[targetIndex])) && !seen.has(targetIndex)) {
      seen.add(targetIndex);
      targetIndex = (targetIndex + step + n) % n;
      
      // Prevent infinite loop if all images have failed
      if (seen.size >= n) {
        return currentPhotoIndex;
      }
    }
    
    return targetIndex;
  };

  onPreviousPhoto = () => {
    const targetIndex = this.getNextValidIndex('previous');
    if (targetIndex !== this.getCurrentPhotoIndex()) {
      this.onPhotoChange(targetIndex);
    }
  };

  onPreviousPhotoPress = (event) => {
    event.preventDefault();
    event.stopPropagation();
    this.onPreviousPhoto();
  };

  onNextPhoto = () => {
    if (this.isBrandonSandersonTarget()) {
      const { images } = this.props;
      const posterImages = deduplicatePosterImages(images);
      const safePhotoIndex = this.getCurrentPhotoIndex(posterImages);

      if (posterImages.length > 0 && safePhotoIndex === posterImages.length - 1) {
        this.showSandersonOverlay();
        return;
      }
    }

    const targetIndex = this.getNextValidIndex('next');
    if (targetIndex !== this.getCurrentPhotoIndex()) {
      this.onPhotoChange(targetIndex);
    }
  };

  onNextPhotoPress = (event) => {
    event.preventDefault();
    event.stopPropagation();
    this.onNextPhoto();
  };

  isBrandonSandersonTarget = (foreignAuthorIdOverride) => {
    const foreignAuthorId = (foreignAuthorIdOverride ?? this.props.foreignAuthorId)?.trim().toLowerCase();
    return foreignAuthorId ? brandonSandersonProviderIds.has(foreignAuthorId) : false;
  };

  showSandersonOverlay = () => {
    if (this.sandersonOverlayTimer) {
      clearTimeout(this.sandersonOverlayTimer);
    }

    this.setState({ showSandersonOverlay: true });
    this.sandersonOverlayTimer = setTimeout(() => {
      this.setState({ showSandersonOverlay: false });
      this.sandersonOverlayTimer = null;
    }, 5000);
  };

  onImageError = (failedUrl) => {
    const { onError } = this.props;
    const posterImages = deduplicatePosterImages(this.props.images);
    const selectedIndex = this.getCurrentPhotoIndex(posterImages);
    const selectedImage = posterImages[selectedIndex];
    const imageKey = getImageKey(selectedImage);
    const displayedUrl = selectedImage && (this.state.loadedPhotoUrls[imageKey] || selectedImage.url);

    if (displayedUrl?.includes('/MediaCover/') && failedUrl?.includes('/MediaCover/')) {
      this.onPhotoChange(selectedIndex, false);
    }

    if (onError) {
      onError();
    }
  };

  onPhotoChange = async(targetIndex, persistSelection = true) => {
    const { images, authorId } = this.props;
    const posterImages = deduplicatePosterImages(images);
    const selectedImage = posterImages[targetIndex];

    if (!selectedImage || !authorId) return;
    const imageKey = getImageKey(selectedImage);

    if (!persistSelection && (this.state.isLoading || this.state.failedPhotoKeys.has(imageKey) || !selectedImage.remoteUrl)) {
      return;
    }

    // Increment request sequence to guard against race conditions
    const seq = ++this.requestSeq;
    
    // Set pending state and show loading spinner
    this.setState({ 
      isLoading: true 
    });
    
    try {
      // Ensure the photo is local before displaying or selecting it
      const loadResult = await createAjaxRequest({
        url: `/author/${authorId}/loadImage`,
        method: 'POST',
        dataType: 'json',
        contentType: 'application/json',
        data: JSON.stringify({
          ImageUrl: selectedImage.remoteUrl || selectedImage.url
        })
      }).request;
      
      // Check if this is still the latest request
      if (seq !== this.requestSeq) {
        console.log('[AuthorPoster] Ignoring stale response');
        return;
      }
      
      console.log(`[AuthorPoster] Load image result for author ${authorId}:`, loadResult);
      
      if (loadResult.status === 'success' && loadResult.localPath) {
        // Keep the current photo visible until the replacement is both present
        // locally and loaded by the browser. Committing the choice before this
        // point creates a blank frame while the new source is still loading.
        const localPathUrl = new URL(getPathWithUrlBase(loadResult.localPath), window.location.origin);
        if (!persistSelection) {
          localPathUrl.searchParams.set('v', Date.now().toString());
        }
        const localPath = `${localPathUrl.pathname}${localPathUrl.search}${localPathUrl.hash}`;
        await preloadImage(localPath);

        const currentPosterImages = deduplicatePosterImages(this.props.images);
        const currentImage = currentPosterImages[this.getCurrentPhotoIndex(currentPosterImages)];
        if (seq !== this.requestSeq ||
            !this.props.images.some((img) => getImageKey(img) === imageKey) ||
            (!persistSelection && getImageKey(currentImage) !== imageKey)) {
          return;
        }

        this.setState(prevState => {
          const nextFailedKeys = new Set(prevState.failedPhotoKeys);
          nextFailedKeys.delete(imageKey);
          const loadedPhotoUrls = {
            ...prevState.loadedPhotoUrls,
            [imageKey]: localPath
          };

          return {
            selectedPhotoKey: persistSelection ? imageKey : prevState.selectedPhotoKey,
            failedPhotoKeys: nextFailedKeys,
            loadedPhotoUrls
          };
        });

        if (persistSelection) {
          // Save to localStorage for immediate fallback
          const storageKey = `authorPhotoChoice:${authorId}`;
          try {
            localStorage.setItem(storageKey, imageKey);
          } catch (e) {
            console.warn('[AuthorPoster] Failed to save photo selection to localStorage:', e);
          }

          // Persist the selection to the server
          try {
            await createAjaxRequest({
              url: `/author/${authorId}/primaryPhoto`,
              method: 'PUT',
              contentType: 'application/json',
              data: JSON.stringify({
                PhotoUrl: selectedImage.remoteUrl || selectedImage.url
              })
            }).request;
            console.log(`[AuthorPoster] Persisted photo selection for author ${authorId}`);
          } catch (e) {
            console.warn('[AuthorPoster] Failed to persist photo selection:', e);
          }
        }
      } else if (loadResult.status !== 'pending' || !persistSelection) {
        // Failed to load - mark as failed but stay on current image
        console.warn(`[AuthorPoster] Failed to load image for author ${authorId}:`, loadResult.errorCode);
        
        this.setState(prevState => ({
          failedPhotoKeys: new Set(prevState.failedPhotoKeys).add(imageKey)
        }));
        
        // Could show a toast/notification here
        // toast.error("Couldn't load photo");
      }
    } catch (e) {
      // Network or other error
      console.warn('[AuthorPoster] Failed to load photo:', e);
      
      // Check if this is still the latest request
      if (seq === this.requestSeq) {
        this.setState(prevState => ({
          failedPhotoKeys: new Set(prevState.failedPhotoKeys).add(imageKey)
        }));
      }
    } finally {
      // Clear loading state if this is still the latest request
      if (seq === this.requestSeq) {
        this.setState({ 
          isLoading: false 
        });
      }
    }
  };

  onMouseEnter = () => {
    this.setState({ isHovering: true });
  };

  onMouseLeave = () => {
    this.setState({ isHovering: false });
  };

  //
  // Render

  render() {
    const {
      images,
      className,
      size,
      isEditorActive,
      showArrowsOnHover,
      authorId,
      foreignAuthorId,
      placeholder,
      ...otherProps
    } = this.props;
    const imageProps = { ...otherProps, onError: this.onImageError };
    const { isHovering, isLoading, loadedPhotoUrls, showSandersonOverlay } = this.state;
    const isSandersonTarget = this.isBrandonSandersonTarget(foreignAuthorId);
    
    // Size variants share one carousel position.
    const posterImages = deduplicatePosterImages(images);
    
    const hasMultiplePhotos = posterImages.length > 1;
    
    const safePhotoIndex = this.getCurrentPhotoIndex(posterImages);
    
    // Determine if arrows should be shown
    let showArrows = false;
    if (hasMultiplePhotos) {
      if (showArrowsOnHover) {
        // On details page: show only on hover
        showArrows = isHovering;
      } else if (isEditorActive) {
        // On library page: show only when editor is active
        showArrows = true;
      }
    }
    
    // Debug logging removed
    
    // Create modified images array with only the current photo showing
    let displayImages = images;
    if (posterImages.length > 0) {
      const selectedPoster = posterImages[safePhotoIndex];
      const selectedLocalUrl = loadedPhotoUrls[getImageKey(selectedPoster)];
      const displayPoster = selectedLocalUrl ?
        { ...selectedPoster, url: selectedLocalUrl } :
        selectedPoster;
      displayImages = images.map(img => 
        img.coverType === 'poster' ? displayPoster : img
      ).filter((img, index, arr) => 
        img.coverType !== 'poster' || arr.findIndex(i => i.coverType === 'poster') === index
      );
    }

    const overlay = showSandersonOverlay && isSandersonTarget && typeof document !== 'undefined'
      ? createPortal(
        <div className={styles.sandersonOverlay}>
          <img
            className={styles.sandersonOverlayImage}
            src={sandersonEasterEggImage}
            alt="Brandon Sanderson"
          />
        </div>,
        document.body
      )
      : null;

    return (
      <>
      <div 
        className={`${styles.posterContainer} ${className || ''}`}
        onMouseEnter={showArrowsOnHover ? this.onMouseEnter : undefined}
        onMouseLeave={showArrowsOnHover ? this.onMouseLeave : undefined}
      >
        <div className={styles.posterImageWrapper}>
          <AuthorImage
            {...imageProps}
            images={displayImages}
            placeholder={placeholder.startsWith('/Content/') ? getPathWithUrlBase(placeholder) : placeholder}
            size={size}
          />
          
          {isLoading && (
            <div className={styles.loadingOverlay} title="Loading image...">
              <Icon name={icons.SPINNER} size={24} />
            </div>
          )}
          
          {showArrows && (
            <div className={styles.photoNavigation}>
              <button
                className={styles.photoNavButton}
                type="button"
                data-select-exempt="true"
                onClick={this.onPreviousPhotoPress}
                title="Previous photo"
                disabled={posterImages.length <= 1}
              >
                <Icon name={icons.ARROW_LEFT} size={16} />
              </button>
              
              <button
                className={styles.photoNavButton}
                type="button"
                data-select-exempt="true"
                onClick={this.onNextPhotoPress}
                title="Next photo"
                disabled={posterImages.length <= 1}
              >
                <Icon name={icons.ARROW_RIGHT} size={16} />
              </button>
            </div>
          )}
          
          
          {showArrows && (
            <div className={styles.photoCounter}>
              {safePhotoIndex + 1} / {posterImages.length}
            </div>
          )}
        </div>

      </div>
        {overlay}
      </>
    );
  }
}

AuthorPoster.propTypes = {
  images: PropTypes.arrayOf(PropTypes.object).isRequired,
  authorId: PropTypes.number,
  foreignAuthorId: PropTypes.string,
  selectedPosterHash: PropTypes.string,
  placeholder: PropTypes.string,
  size: PropTypes.number.isRequired,
  className: PropTypes.string,
  onPhotoChange: PropTypes.func,
  onError: PropTypes.func,
  isEditorActive: PropTypes.bool,
  showArrowsOnHover: PropTypes.bool
};

AuthorPoster.defaultProps = {
  size: 250,
  coverType: 'poster',
  placeholder: defaultPosterPlaceholder
};

export default AuthorPoster;
