import migrateAddAuthorDefaults from './migrateAddAuthorDefaults';
import migrateAuthorSortKey from './migrateAuthorSortKey';
import migrateBlacklistToBlocklist from './migrateBlacklistToBlocklist';
import migrateSeriesPageCountColumn from './migrateSeriesPageCountColumn';

export default function migrate(persistedState) {
  migrateAddAuthorDefaults(persistedState);
  migrateAuthorSortKey(persistedState);
  migrateBlacklistToBlocklist(persistedState);
  migrateSeriesPageCountColumn(persistedState);
}
