// A full author resource is often used for unrelated edits. Only supplied
// monitoring intent should reach the API's linked-policy validation.
export default function getAuthorUpdatePayload(author, changes = {}) {
  const result = { ...author };
  // Per-format fields carry the edit; the combined field is for legacy clients.
  if (result.audiobookTags != null || result.ebookTags != null) {
    delete result.tags;
  }
  ['audiobookMonitorExisting', 'audiobookMonitorFuture', 'ebookMonitorExisting', 'ebookMonitorFuture']
    .forEach((name) => delete result[name]);
  const policyNames = ['audiobookMonitorNewItems', 'ebookMonitorNewItems'];
  const has = (name) => Object.prototype.hasOwnProperty.call(changes, name);
  ['audiobookFolder', 'ebookFolder'].filter((name) => !has(name)).forEach((name) => delete result[name]);
  if (!has('syncMonitoredAcrossFormats')) {
    delete result.syncMonitoredAcrossFormats;
  }
  if (changes.syncMonitoredAcrossFormats !== true) {
    policyNames.filter((name) => !has(name)).forEach((name) => delete result[name]);
  }
  return result;
}
