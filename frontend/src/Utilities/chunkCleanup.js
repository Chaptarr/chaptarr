// Chunk Cleanup Utility
// Detects and handles old cached webpack chunks that interfere with the app

const KNOWN_OLD_CHUNKS = [
  'index.html-1.js',
  'index.l8fe4ddd.js',
  'pageInteraction.2f84ddce.js',
  // Add more as discovered
];

function detectProblematicChunks() {
  // Check if any old chunks are being loaded
  const scripts = document.querySelectorAll('script[src]');
  const loadedChunks = Array.from(scripts).map(s => {
    const src = s.getAttribute('src');
    return src ? src.split('/').pop().split('?')[0] : null;
  }).filter(Boolean);

  console.log('[ChunkCleanup] Loaded chunks:', loadedChunks);

  const problematicChunks = loadedChunks.filter(chunk => 
    KNOWN_OLD_CHUNKS.includes(chunk) || 
    // Detect old patterns
    chunk.match(/^index\.[a-z0-9]+\.js$/) || // Old chunk pattern
    chunk.match(/^pageInteraction\.[a-z0-9]+\.js$/) // Old interaction chunks
  );

  if (problematicChunks.length > 0) {
    console.error('[ChunkCleanup] Detected problematic old chunks:', problematicChunks);
    return problematicChunks;
  }

  return [];
}

function checkChunkMismatch() {
  // Look for webpack errors that indicate chunk loading issues
  const originalError = window.onerror;
  let chunkErrors = [];

  window.onerror = function(message, source, lineno, colno, error) {
    if (message && (
      message.includes('ChunkLoadError') ||
      message.includes('Loading chunk') ||
      message.includes('Failed to fetch dynamically imported module')
    )) {
      console.error('[ChunkCleanup] Webpack chunk error detected:', message);
      chunkErrors.push({ message, source });
      
      // Force reload after accumulating errors
      if (chunkErrors.length >= 2) {
        handleChunkError();
      }
    }
    
    if (originalError) {
      return originalError(message, source, lineno, colno, error);
    }
  };

  // Also catch unhandled promise rejections
  window.addEventListener('unhandledrejection', event => {
    if (event.reason && event.reason.message && (
      event.reason.message.includes('ChunkLoadError') ||
      event.reason.message.includes('Loading CSS chunk')
    )) {
      console.error('[ChunkCleanup] Unhandled chunk error:', event.reason);
      handleChunkError();
    }
  });
}

function handleChunkError() {
  // Show user-friendly message
  const message = `
    Chaptarr has detected cached files from an older version that are interfering with the application.
    The page will reload to fix this issue.
    
    If this problem persists, please clear your browser cache.
  `;
  
  console.error('[ChunkCleanup] ' + message);
  
  // Force a hard reload to bypass cache
  setTimeout(() => {
    // Clear service worker caches if any
    if ('caches' in window) {
      caches.keys().then(names => {
        names.forEach(name => {
          console.log('[ChunkCleanup] Clearing cache:', name);
          caches.delete(name);
        });
      });
    }
    
    // Force reload with cache bypass
    window.location.reload(true);
  }, 2000);
}

export default function initializeChunkCleanup() {
  console.log('[ChunkCleanup] Initializing chunk cleanup detection');
  
  // Check for problematic chunks on load
  const problematicChunks = detectProblematicChunks();
  if (problematicChunks.length > 0) {
    console.error('[ChunkCleanup] Found old chunks on load, forcing reload');
    handleChunkError();
    return;
  }
  
  // Monitor for chunk loading errors
  checkChunkMismatch();
  
  // Also check after a delay in case chunks load asynchronously
  setTimeout(() => {
    const delayedCheck = detectProblematicChunks();
    if (delayedCheck.length > 0) {
      console.error('[ChunkCleanup] Found old chunks after delay, forcing reload');
      handleChunkError();
    }
  }, 3000);
}