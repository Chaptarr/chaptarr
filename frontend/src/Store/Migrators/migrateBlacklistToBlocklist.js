import get from 'lodash/get';
import remove from 'lodash/remove';

export default function migrateBlacklistToBlocklist(persistedState) {
  const blocklist = get(persistedState, 'blacklist');

  if (!blocklist) {
    return;
  }

  persistedState.blocklist = blocklist;
  remove(persistedState, 'blacklist');
}
