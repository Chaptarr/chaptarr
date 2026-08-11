import get from 'lodash/get';
import set from 'lodash/set';

const TABLES_TO_MIGRATE = ['blocklist', 'history', 'queue.paged', 'wanted.missing', 'wanted.cutoffUnmet'];

export default function migrateAuthorSortKey(persistedState) {

  for (const table of TABLES_TO_MIGRATE) {
    const key = `${table}.sortKey`;
    const sortKey = get(persistedState, key);

    if (sortKey === 'authorMetadata.sortName') {
      set(persistedState, key, 'authors.sortName');
    }
  }
}
