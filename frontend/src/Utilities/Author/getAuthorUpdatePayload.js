// A full author resource is often used for unrelated edits. Only supplied
// monitoring intent should reach the API's linked-policy validation.
export default function getAuthorUpdatePayload(author, changes = {}) {
  const result = { ...author };
  ['audiobookMonitorExisting', 'audiobookMonitorFuture', 'ebookMonitorExisting', 'ebookMonitorFuture']
    .forEach((name) => delete result[name]);
  const policyNames = ['audiobookMonitorNewItems', 'ebookMonitorNewItems'];
  const has = (name) => Object.prototype.hasOwnProperty.call(changes, name);
  if (!has('syncMonitoredAcrossFormats')) {
    delete result.syncMonitoredAcrossFormats;
  }
  if (changes.syncMonitoredAcrossFormats !== true) {
    policyNames.filter((name) => !has(name)).forEach((name) => delete result[name]);
  }
  return result;
}
