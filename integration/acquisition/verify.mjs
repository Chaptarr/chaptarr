import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';

process.env.CONFIG_DIRECTORY = '/tmp/seerrng-acquisition-probe';
process.env.SEERR_EXTERNAL_CONFIG = JSON.stringify({ clientId: 'isolated-acquisition-probe', main: {}, plex: {}, jellyfin: {}, oidc: {}, tautulli: {}, notifications: { agents: {} }, network: {}, radarr: [], sonarr: [], lidarr: [], readarr: [] });
const require = createRequire(import.meta.url);
const { default: ReadarrAPI } = require('/app/dist/api/servarr/readarr.js');
const manifest = JSON.parse(readFileSync('/fixture/manifest.json'));
const api = new ReadarrAPI({ url: 'http://127.0.0.1:8789/api/v1', apiKey: 'synthetic-e2e-restricted-key-20261105', mediaType: manifest.mediaType });
const pause = () => new Promise(resolve => setTimeout(resolve, 1000));

if (process.argv.includes('--transfer')) {
  let torrent;
  for (let n = 0; n < 90; n++) {
    // Disposable keys over loopback in a network-none namespace; no external traffic.
    // nosemgrep: typescript.react.security.react-insecure-request.react-insecure-request
    const response = await fetch('http://127.0.0.1:8080/api/v2/torrents/info');
    torrent = (await response.json()).find(item => item.hash === manifest.infoHash);
    if (torrent) break;
    await pause();
  }
  assert.ok(torrent, 'Seerr request did not lead to a qBittorrent grab');
  // Disposable keys over loopback in a network-none namespace; no external traffic.
  // nosemgrep: typescript.react.security.react-insecure-request.react-insecure-request
  const peers = await fetch('http://127.0.0.1:8080/api/v2/torrents/addPeers', { method: 'POST', body: new URLSearchParams({ hashes: manifest.infoHash, peers: '127.0.0.1:6881' }) });
  assert.equal(peers.status, 200);
  for (let n = 0; n < 90; n++) {
    // Disposable keys over loopback in a network-none namespace; no external traffic.
    // nosemgrep: typescript.react.security.react-insecure-request.react-insecure-request
    const response = await fetch('http://127.0.0.1:8080/api/v2/torrents/info');
    torrent = (await response.json()).find(item => item.hash === manifest.infoHash);
    if (torrent?.progress === 1) break;
    await pause();
  }
  assert.equal(torrent?.progress, 1, 'Local peer transfer did not complete');
  assert.equal(createHash('sha256').update(readFileSync('/fixture/downloads/' + manifest.filename)).digest('hex'), manifest.sha256);
  // Poll the real client through the application's normal completed-download path.
  for (const name of ['RefreshMonitoredDownloads', 'ProcessMonitoredDownloads']) {
    // Disposable keys over loopback in a network-none namespace; no external traffic.
    // nosemgrep: typescript.react.security.react-insecure-request.react-insecure-request
    const response = await fetch('http://127.0.0.1:8789/api/v1/command', { method: 'POST', headers: { 'X-Api-Key': 'synthetic-e2e-admin-key-20261105', 'Content-Type': 'application/json', 'X-ChaptarrNG-Client': 'native' }, body: JSON.stringify({ name }) });
    assert.equal(response.status, 201);
  }
}

let book;
for (let n = 0; n < 120; n++) {
  book = await api.getBook(manifest.bookId, 0);
  if (book.statistics?.bookFileCount > 0) break;
  await pause();
}
assert.equal(book.monitored, true);
assert.equal(book.statistics?.bookFileCount, 1);
assert.equal(book.statistics?.ebookFileCount, manifest.mediaType === 'ebook' ? 1 : 0);
assert.equal(book.statistics?.audiobookFileCount, manifest.mediaType === 'audiobook' ? 1 : 0);
const files = await api.getBookFiles(manifest.bookId);
assert.equal(files.length, 1);
assert.ok(files[0].path.endsWith(manifest.mediaType === 'ebook' ? '.epub' : '.mp3'));
const history = await api.getBookHistory(manifest.bookId);
assert.ok(history.some(item => item.eventType === 'grabbed'), 'Grab history missing');
assert.ok(history.some(item => item.eventType === 'bookFileImported'), 'Import history missing');
console.log(JSON.stringify({ phase: process.argv.includes('--transfer') ? 'acquired' : 'restart-availability', mediaType: manifest.mediaType, files: files.length, ebookFileCount: book.statistics.ebookFileCount, audiobookFileCount: book.statistics.audiobookFileCount, historyEvents: history.map(item => item.eventType) }));
