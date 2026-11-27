import { createSelector } from 'reselect';

const AUDIOBOOKSHELF_KEEPER = 1;

// True when an AudioBookShelf connection is set to keep the metadata, which is when the
// "Sync from AudioBookShelf" actions apply.
function createAudioBookShelfKeeperSelector() {
  return createSelector(
    (state) => state.settings.notifications.items,
    (notifications) => {
      return (notifications || []).some((notification) => {
        if (notification.implementation !== 'AudioBookShelf') {
          return false;
        }

        const keeper = (notification.fields || []).find((field) => field.name === 'metadataKeeper');

        return keeper != null && Number(keeper.value) === AUDIOBOOKSHELF_KEEPER;
      });
    }
  );
}

export default createAudioBookShelfKeeperSelector;
