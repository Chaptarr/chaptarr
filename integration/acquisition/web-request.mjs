import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
const fixture = JSON.parse(readFileSync('/fixture/manifest.json'));
const base = 'http://127.0.0.1:5055/api/v1';
const admin = { 'X-Api-Key': 'synthetic-seerr-web-admin-key-20261105' };
for (let n = 0; n < 120; n++) {
  try { if ((await fetch(base + '/status')).ok) break; } catch {}
  await new Promise(resolve => setTimeout(resolve, 1000));
}
const login = await fetch(base + '/auth/local', { method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ email: 'fixture-requester@example.invalid', password: 'Synthetic-Fixture-Password-20261105' }) });
assert.equal(login.status, 200, await login.text());
const cookie = login.headers.getSetCookie().map(value => value.split(';')[0]).join('; ');
assert.ok(cookie.includes('connect.sid='));
const submitted = await fetch(base + '/request', { method: 'POST', headers: { Cookie: cookie, 'Content-Type': 'application/json' },
  body: JSON.stringify({ mediaType: 'book', mediaId: 'bookshelf:1:MzA', format: fixture.mediaType, preferredEditionId: String(fixture.editionId) }) });
const request = await submitted.json();
assert.equal(submitted.status, 201, JSON.stringify(request));
assert.equal(request.status, 1); assert.equal(request.requestedBy.id, 2); assert.equal(request.bookFormat, fixture.mediaType);
const unauthorizedApproval = await fetch(base + '/request/' + request.id + '/approve', { method: 'POST', headers: { Cookie: cookie } });
assert.equal(unauthorizedApproval.status, 403);
const approval = await fetch(base + '/request/' + request.id + '/approve', { method: 'POST', headers: admin });
const approved = await approval.json();
assert.equal(approval.status, 200, JSON.stringify(approved)); assert.equal(approved.status, 2);
writeFileSync('/fixture/web-request.json', JSON.stringify({ requestId: request.id, mediaDatabaseId: request.media.id,
  mediaType: fixture.mediaType, requesterId: 2, userLogin: true, pendingRequest: true, requesterApprovalDenied: true, adminApproval: true }, null, 2));
console.log('Seerr server: user login, pending book request, denied self-approval and administrator approval passed.');
