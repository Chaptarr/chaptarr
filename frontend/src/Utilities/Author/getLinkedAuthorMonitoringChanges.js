// Link only monitoring policies; author gates and per-format configuration
// deliberately keep their own values.
export default function getLinkedAuthorMonitoringChanges(name, value, syncEnabled) {
  if (syncEnabled && (/^(shared|audiobook|ebook)Monitor(NewItems)?$/).test(name)) {
    const suffix = name.endsWith('NewItems') ? 'MonitorNewItems' : 'Monitor';
    return { [`audiobook${suffix}`]: value, [`ebook${suffix}`]: value };
  }
  return { [name]: value };
}
