// Version Gate for localStorage
// Ensures localStorage is cleared or migrated when app version changes

const STORAGE_VERSION_KEY = 'chaptarr_storage_version';
const CURRENT_VERSION = window.Chaptarr?.version || 'unknown';

// List of localStorage keys we want to preserve during version changes
const PRESERVE_KEYS = [
  // Redux persisted UI state (view/sort options, etc.)
  'chaptarr',
  'chaptarr_ui_settings',
  'chaptarr_column_widths',
  'chaptarr_filter_preferences',
  // Non-redux UI preferences
  'hideUnmonitoredMissing',
  'selectedMediaType'
];

// List of localStorage key prefixes we want to preserve during version changes
const PRESERVE_PREFIXES = [
  'authorPhotoChoice:'
];

function migrateStorage(oldVersion, newVersion) {
  console.log(`[VersionGate] Migrating storage from ${oldVersion} to ${newVersion}`);
  
  // Preserve important user preferences
  const preserved = {};
  PRESERVE_KEYS.forEach(key => {
    const value = localStorage.getItem(key);
    if (value !== null) {
      preserved[key] = value;
    }
  });
  
  // Preserve dynamic keys by prefix
  for (let i = 0; i < localStorage.length; i++) {
    const key = localStorage.key(i);
    if (key && PRESERVE_PREFIXES.some(prefix => key.startsWith(prefix))) {
      const value = localStorage.getItem(key);
      if (value !== null) {
        preserved[key] = value;
      }
    }
  }
  
  // Clear all localStorage
  localStorage.clear();
  
  // Restore preserved values
  Object.entries(preserved).forEach(([key, value]) => {
    localStorage.setItem(key, value);
  });
  
  // Set new version
  localStorage.setItem(STORAGE_VERSION_KEY, newVersion);
}

export default function checkStorageVersion() {
  try {
    const storedVersion = localStorage.getItem(STORAGE_VERSION_KEY);
    
    if (!storedVersion) {
      // First time or after manual clear
      console.log('[VersionGate] No stored version found, initializing with current version:', CURRENT_VERSION);
      localStorage.setItem(STORAGE_VERSION_KEY, CURRENT_VERSION);
      return;
    }
    
    if (storedVersion !== CURRENT_VERSION) {
      console.log(`[VersionGate] Version mismatch detected. Stored: ${storedVersion}, Current: ${CURRENT_VERSION}`);
      
      // Check if this is a build time change (more reliable than version string)
      const currentBuildTime = window.Chaptarr?.buildTime;
      const storedBuildTime = localStorage.getItem('chaptarr_build_time');
      
      if (currentBuildTime && storedBuildTime && currentBuildTime !== storedBuildTime) {
        console.log('[VersionGate] Build time changed, migrating storage');
        migrateStorage(storedVersion, CURRENT_VERSION);
        localStorage.setItem('chaptarr_build_time', currentBuildTime);
      } else if (!storedBuildTime && currentBuildTime) {
        // Initialize build time if not present
        localStorage.setItem('chaptarr_build_time', currentBuildTime);
        localStorage.setItem(STORAGE_VERSION_KEY, CURRENT_VERSION);
      } else {
        // Version changed but build time didn't - just update version
        localStorage.setItem(STORAGE_VERSION_KEY, CURRENT_VERSION);
      }
    }
  } catch (error) {
    console.error('[VersionGate] Error checking storage version:', error);
    // Don't break the app if version gating fails
  }
}
