import elementClass from 'element-class';
import { setScrollLock } from 'Utilities/scrollLock';

/**
 * Emergency modal cleanup utility
 * Call this when modals get stuck (grey screen bug)
 */
export function forceCloseAllModals() {
  console.warn('Force closing all modals due to stuck modal state');

  try {
    // Only clean up body classes and scroll lock
    // Let React handle DOM cleanup
    elementClass(document.body).remove('Modal-modalOpen');
    elementClass(document.body).remove('Modal-modalOpenIOS');
    setScrollLock(false);

    console.log('Modal cleanup completed');
  } catch (error) {
    console.error('Error during modal cleanup:', error);
  }
}

/**
 * Check if there are orphaned modal elements in the DOM
 */
export function detectOrphanedModals() {
  // Only check for body classes, not portal content
  // Portal content is managed by React
  const hasModalBodyClasses =
    elementClass(document.body).has('Modal-modalOpen') ||
    elementClass(document.body).has('Modal-modalOpenIOS');

  return hasModalBodyClasses;
}

// Auto-cleanup on window unload
window.addEventListener('beforeunload', () => {
  forceCloseAllModals();
});
