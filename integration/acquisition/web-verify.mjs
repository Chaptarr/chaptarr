import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
const fixture = JSON.parse(readFileSync('/fixture/manifest.json'));
const plan = JSON.parse(readFileSync('/fixture/web-request.json'));
const base = 'http://127.0.0.1:5055/api/v1';
const headers = { 'X-Api-Key': 'synthetic-seerr-web-admin-key-20261105' };
for (let n = 0; n < 120; n++) {
  try { if ((await fetch(base + '/status')).ok) break; } catch {}
  await new Promise(resolve => setTimeout(resolve, 1000));
}
const scan = await fetch(base + '/settings/jobs/readarr-scan/run', { method: 'POST', headers });
assert.ok([200, 409].includes(scan.status), 'Expected a started or already-active library scan');
let request;
for (let n = 0; n < 120; n++) {
  const response = await fetch(base + '/request/' + plan.requestId, { headers });
  assert.equal(response.status, 200);
  request = await response.json();
  if (request.media?.status === 5) break;
  await new Promise(resolve => setTimeout(resolve, 1000));
}
assert.ok([2, 5].includes(request.status), JSON.stringify(request));
assert.equal(request.requestedBy.id, 2); assert.equal(request.bookFormat, fixture.mediaType);
assert.equal(request.media.id, plan.mediaDatabaseId); assert.equal(request.media.status, 5);
const requested = fixture.mediaType === 'ebook' ? request.media.externalServiceId : request.media.audiobookExternalServiceId;
const other = fixture.mediaType === 'ebook' ? request.media.audiobookExternalServiceId : request.media.externalServiceId;
assert.equal(requested, fixture.bookId); assert.equal(other ?? null, null);
const phase = process.argv.includes('--restart') ? 'restart' : 'import';
writeFileSync('/fixture/web-' + phase + '-result.json', JSON.stringify({ requestId: plan.requestId, mediaType: fixture.mediaType,
  available: true, requestedBookLinked: true, otherFormatNotReportedAvailable: true, originalRequestAndRequesterRetained: true }, null, 2));
console.log('Seerr server:', phase, 'availability and persisted request identity passed.');
