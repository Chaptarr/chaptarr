import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { createPortal } from 'react-dom';
import Icon from 'Components/Icon';
import { icons } from 'Helpers/Props';
import AuthorImage from './AuthorImage';
import styles from './AuthorPoster.css';

const defaultPosterPlaceholder = '/Content/Images/chaptarr-logo.svg';
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

function preloadImage(url) {
  return new Promise((resolve, reject) => {
    const image = new Image();

    image.onload = resolve;
    image.onerror = () => reject(new Error(`Failed to preload author photo: ${url}`));
    image.src = url;
  });
}

// Helper function to deduplicate poster size variants and pick highest quality
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
    if (!current || size > current._size) {
      byKey.set(key, { ...img, _size: size });
    }
  });
  
  // Return images without the temporary _size property
  return Array.from(byKey.values()).map(({ _size, ...img }) => img);
}

class AuthorPoster extends Component {
  constructor(props) {
    super(props);
    
    // Initialize from localStorage if available
    const { authorId, images, selectedPosterHash } = props;
    const posterImages = deduplicatePosterImages(images);
    let initialIndex = 0;
    let hasLocalSelection = false;
    
    if (authorId && posterImages.length > 0) {
      try {
        const storageKey = `authorPhotoChoice:${authorId}`;
        const savedToken = localStorage.getItem(storageKey);
        if (savedToken) {
          // Backward compatibility: if saved value is a full URL, normalize it to a key
          const savedKey = (/^https?:|^\//.test(savedToken))
            ? normalizePosterKey(savedToken)
            : savedToken;
          
          const savedIndex = posterImages.findIndex(img => getImageKey(img) === savedKey);
          if (savedIndex !== -1) {
            initialIndex = savedIndex;
            hasLocalSelection = true;
          }
        }
      } catch (e) {
        // Ignore localStorage errors
        console.warn('[AuthorPoster] Failed to load saved photo selection:', e);
      }
    }

    if (!hasLocalSelection && selectedPosterHash && posterImages.length > 0) {
      const hashIndex = posterImages.findIndex(img => img.hash === selectedPosterHash);
      if (hashIndex !== -1) {
        initialIndex = hashIndex;
      }
    }
    
    this.state = {
      currentPhotoIndex: initialIndex,
      pendingPhotoIndex: null,     // Photo being loaded
      isHovering: false,
      isLoading: false,
      failedIndices: new Set(),    // Track failed photos this session
      loadedPhotoUrls: {},         // Unique local URLs returned by on-demand loads
      showSandersonOverlay: false
    };
    
    this.requestSeq = 0;  // Guard against race conditions
    this.hasLocalSelection = hasLocalSelection;
    this.sandersonOverlayTimer = null;
  }

  componentDidUpdate(prevProps) {
    const { authorId, images, selectedPosterHash } = this.props;
    if (!authorId) {
      return;
    }

    const imagesBecameAvailable = prevProps.images.length === 0 && images.length > 0;
    const hashChanged = prevProps.selectedPosterHash !== selectedPosterHash;

    if (imagesBecameAvailable && !this.hasLocalSelection) {
      try {
        const storageKey = `authorPhotoChoice:${authorId}`;
        const savedToken = localStorage.getItem(storageKey);
        if (savedToken) {
          const savedKey = (/^https?:|^\//.test(savedToken))
            ? normalizePosterKey(savedToken)
            : savedToken;

          const posterImages = deduplicatePosterImages(images);
          const savedIndex = posterImages.findIndex(img => getImageKey(img) === savedKey);
          if (savedIndex !== -1) {
            this.hasLocalSelection = true;
            this.setState({ currentPhotoIndex: savedIndex });
            return;
          }
        }
      } catch (e) {
        // Ignore localStorage errors
      }
    }

    if ((imagesBecameAvailable || hashChanged) && selectedPosterHash && !this.hasLocalSelection) {
      const posterImages = deduplicatePosterImages(images);
      const hashIndex = posterImages.findIndex(img => img.hash === selectedPosterHash);
      if (hashIndex !== -1 && hashIndex !== this.state.currentPhotoIndex) {
        this.setState({ currentPhotoIndex: hashIndex });
      }
    }
  }

  componentWillUnmount() {
    if (this.sandersonOverlayTimer) {
      clearTimeout(this.sandersonOverlayTimer);
      this.sandersonOverlayTimer = null;
    }
  }

  //
  // Listeners

  getNextValidIndex = (direction) => {
    const { images } = this.props;
    const posterImages = deduplicatePosterImages(images);
    const { currentPhotoIndex, failedIndices } = this.state;
    
    if (posterImages.length <= 1) return currentPhotoIndex;
    
    const step = direction === 'next' ? 1 : -1;
    const n = posterImages.length;
    let targetIndex = (currentPhotoIndex + step + n) % n;
    
    // Skip known failed indices
    const seen = new Set();
    while (failedIndices.has(targetIndex) && !seen.has(targetIndex)) {
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
    if (targetIndex !== this.state.currentPhotoIndex) {
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
      const { currentPhotoIndex } = this.state;
      const posterImages = deduplicatePosterImages(images);
      const safePhotoIndex = Math.min(currentPhotoIndex, Math.max(0, posterImages.length - 1));

      if (posterImages.length > 0 && safePhotoIndex === posterImages.length - 1) {
        this.showSandersonOverlay();
        return;
      }
    }

    const targetIndex = this.getNextValidIndex('next');
    if (targetIndex !== this.state.currentPhotoIndex) {
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

  onPhotoChange = async (targetIndex) => {
    const { images, authorId } = this.props;
    const posterImages = deduplicatePosterImages(images);
    const selectedImage = posterImages[targetIndex];
    
    if (!selectedImage || !authorId) return;
    
    // Increment request sequence to guard against race conditions
    const seq = ++this.requestSeq;
    
    // Set pending state and show loading spinner
    this.setState({ 
      pendingPhotoIndex: targetIndex,
      isLoading: true 
    });
    
    try {
      // Load image on-demand when user selects it
      const loadResponse = await fetch(`/api/v1/author/${authorId}/loadImage`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'X-Api-Key': window.Chaptarr.apiKey
        },
        body: JSON.stringify({
          ImageUrl: selectedImage.remoteUrl || selectedImage.url
        })
      });
      
      // Check if this is still the latest request
      if (seq !== this.requestSeq) {
        console.log('[AuthorPoster] Ignoring stale response');
        return;
      }
      
      const loadResult = await loadResponse.json();
      console.log(`[AuthorPoster] Load image result for author ${authorId}:`, loadResult);
      
      if (loadResult.status === 'success' && loadResult.localPath) {
        // Keep the current photo visible until the replacement is both present
        // locally and loaded by the browser. Committing the index before this
        // point creates a blank frame while the new source is still loading.
        await preloadImage(loadResult.localPath);

        if (seq !== this.requestSeq) {
          return;
        }

        const imageKey = getImageKey(selectedImage);

        this.setState(prevState => {
          const nextFailedIndices = new Set(prevState.failedIndices);
          nextFailedIndices.delete(targetIndex);
          const loadedPhotoUrls = loadResult.localPath ?
            {
              ...prevState.loadedPhotoUrls,
              [imageKey]: loadResult.localPath
            } :
            prevState.loadedPhotoUrls;

          return {
            currentPhotoIndex: targetIndex,
            failedIndices: nextFailedIndices,
            loadedPhotoUrls
          };
        });
        
        // Save to localStorage for immediate fallback
        const storageKey = `authorPhotoChoice:${authorId}`;
        try {
          localStorage.setItem(storageKey, imageKey);
          this.hasLocalSelection = true;
        } catch (e) {
          console.warn('[AuthorPoster] Failed to save photo selection to localStorage:', e);
        }
        
        // Persist the selection to the server
        try {
          const persistResponse = await fetch(`/api/v1/author/${authorId}/primaryPhoto`, {
            method: 'PUT',
            headers: {
              'Content-Type': 'application/json',
              'X-Api-Key': window.Chaptarr.apiKey
            },
            body: JSON.stringify({
              PhotoUrl: selectedImage.remoteUrl || selectedImage.url
            })
          });
          
          if (persistResponse.ok) {
            console.log(`[AuthorPoster] Persisted photo selection for author ${authorId}`);
          } else {
            console.warn('[AuthorPoster] Failed to persist photo selection:', await persistResponse.text());
          }
        } catch (e) {
          console.warn('[AuthorPoster] Failed to persist photo selection:', e);
        }
      } else if (loadResult.status !== 'pending') {
        // Failed to load - mark as failed but stay on current image
        console.warn(`[AuthorPoster] Failed to load image for author ${authorId}:`, loadResult.errorCode);
        
        this.setState(prevState => ({
          failedIndices: new Set(prevState.failedIndices).add(targetIndex)
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
          failedIndices: new Set(prevState.failedIndices).add(targetIndex)
        }));
      }
    } finally {
      // Clear loading state if this is still the latest request
      if (seq === this.requestSeq) {
        this.setState({ 
          pendingPhotoIndex: null,
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
    const { currentPhotoIndex, isHovering, isLoading, loadedPhotoUrls, showSandersonOverlay } = this.state;
    const isSandersonTarget = this.isBrandonSandersonTarget(foreignAuthorId);
    
    // Filter and deduplicate poster images
    // The backend sends multiple size variants (poster.jpg, poster-250.jpg, poster-500.jpg)
    // We need to deduplicate these to show only unique posters
    const posterImages = deduplicatePosterImages(images);
    
    const hasMultiplePhotos = posterImages.length > 1;
    
    // Ensure currentPhotoIndex is valid after filtering
    const safePhotoIndex = Math.min(currentPhotoIndex, Math.max(0, posterImages.length - 1));
    
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
    if (hasMultiplePhotos && posterImages.length > 0) {
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
            {...otherProps}
            images={displayImages}
            placeholder={placeholder}
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
  isEditorActive: PropTypes.bool,
  showArrowsOnHover: PropTypes.bool
};

AuthorPoster.defaultProps = {
  size: 250,
  coverType: 'poster',
  placeholder: defaultPosterPlaceholder
};

export default AuthorPoster;
