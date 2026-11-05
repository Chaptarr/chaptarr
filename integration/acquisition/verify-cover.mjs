import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { createHash } from 'node:crypto';

process.env.CONFIG_DIRECTORY = '/tmp/seerrng-cover-probe';
process.env.SEERR_EXTERNAL_CONFIG = JSON.stringify({ clientId: 'isolated-cover-probe', main: {}, plex: {}, jellyfin: {}, oidc: {}, tautulli: {}, notifications: { agents: {} }, network: {}, radarr: [], sonarr: [], lidarr: [], readarr: [] });
const { default: ReadarrAPI } = createRequire(import.meta.url)('/app/dist/api/servarr/readarr.js');
const root = 'http://127.0.0.1:8789';
const key = 'synthetic-e2e-restricted-key-20261105';
const api = new ReadarrAPI({ url: root + '/api/v1', apiKey: key, mediaType: 'ebook' });
const expectedHash = createHash('sha256').update(readFileSync('/checks/fixture-cover.png')).digest('hex');

for (let n = 0; n < 90; n++) {
  try { if ((await fetch(root + '/ping')).ok) break; } catch {}
  await new Promise(resolve => setTimeout(resolve, 1000));
}
const book = await api.getBook(31, 0);
const advertised = book.images.find(image => image.url?.startsWith('/MediaCoverProxy/'));
assert.ok(advertised, 'Expected a registered remote-image proxy path');
const images = await Promise.all([api.getBookCover(31), api.getAuthorCover(20)]);
for (const image of images) {
  assert.equal(image.contentType, 'image/png');
  assert.equal(createHash('sha256').update(image.imageBuffer).digest('hex'), expectedHash);
}
const valid = root + advertised.url;
for (const headers of [{}, { 'X-Api-Key': 'wrong-key' }]) {
  assert.equal((await fetch(valid, { headers, redirect: 'manual' })).status, 401);
}
assert.equal((await fetch(root + '/', { headers: { 'X-Api-Key': key }, redirect: 'manual' })).status, 401);
assert.equal((await fetch(root + '/MediaCoverProxy/' + 'a'.repeat(64) + '/cover.png', { headers: { 'X-Api-Key': key } })).status, 404);
assert.equal((await fetch(valid.replace(/[^/]+$/, 'config.xml'), { headers: { 'X-Api-Key': key }, redirect: 'manual' })).status, 401);
console.log(JSON.stringify({ bookCover: true, authorCover: true, remoteProxyImageHashesMatch: true, authenticationBoundaries: true }));
