import { get, set } from 'lodash';
import monitorNewItemsOptions from 'Utilities/Author/monitorNewItemsOptions';
import monitorOptions from 'Utilities/Author/monitorOptions';

export default function migrateAddAuthorDefaults(persistedState) {
  const initialAuthorPaths = [
    'addAuthor.defaults.monitor',
    'search.authorDefaults.monitor',
    'search.authorDefaults.audiobookMonitor',
    'search.authorDefaults.ebookMonitor'
  ];

  initialAuthorPaths.forEach((path) => {
    const monitor = get(persistedState, path);

    if (monitor && !monitorOptions.some((option) => option.key === monitor)) {
      set(persistedState, path, 'none');
    }
  });

  const initialBookPaths = [
    'search.bookDefaults.monitor',
    'search.bookDefaults.audiobookMonitor',
    'search.bookDefaults.ebookMonitor'
  ];

  initialBookPaths.forEach((path) => {
    const monitor = get(persistedState, path);

    if (monitor && monitor !== 'all' && monitor !== 'specificBook') {
      set(persistedState, path, 'specificBook');
    }
  });

  const monitorNewItemsPaths = [
    'search.authorDefaults.monitorNewItems',
    'search.authorDefaults.audiobookMonitorNewItems',
    'search.authorDefaults.ebookMonitorNewItems',
    'search.bookDefaults.monitorNewItems',
    'search.bookDefaults.audiobookMonitorNewItems',
    'search.bookDefaults.ebookMonitorNewItems'
  ];

  monitorNewItemsPaths.forEach((path) => {
    const monitorNewItems = get(persistedState, path);

    if (monitorNewItems && !monitorNewItemsOptions.some((option) => option.key === monitorNewItems)) {
      set(persistedState, path, 'none');
    }
  });
}
