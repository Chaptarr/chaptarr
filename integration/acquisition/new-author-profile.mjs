import assert from 'node:assert/strict';
import { writeFileSync } from 'node:fs';
const base = 'http://127.0.0.1:8789/readarr/hc/ebook/api/v1';
const checks = [];
for (const [foreignId, profileId, native] of [['99', 2, false], ['100', 4, true]]) {
  const headers = { 'X-Api-Key': 'synthetic-e2e-admin-key-20261105', 'Content-Type': 'application/json',
    ...(native ? { 'X-ChaptarrNG-Client': 'native' } : {}) };
  // Synthetic keys over shared loopback in a network-none test namespace.
  // nosemgrep: typescript.react.security.react-insecure-request.react-insecure-request
  const posted = await fetch(base + '/author', { method: 'POST', headers, body: JSON.stringify({
    authorName: 'Fixture New Author ' + foreignId, foreignAuthorId: 'hc:' + foreignId,
    qualityProfileId: profileId, metadataProfileId: 2, rootFolderPath: '/library', monitored: true,
    monitorNewItems: 'none', addOptions: { monitor: 'none', searchForMissingBooks: false }
  }) });
  const response = await posted.json();
  assert.ok([201, 202].includes(posted.status), JSON.stringify({ status: posted.status, response }));
  let author;
  for (let attempt = 0; attempt < 90; attempt++) {
    // Synthetic keys over shared loopback in a network-none test namespace.
    // nosemgrep: typescript.react.security.react-insecure-request.react-insecure-request
    const listed = await fetch(base + '/author', { headers });
    assert.equal(listed.status, 200);
    author = (await listed.json()).find(row => row.foreignAuthorId === foreignId || row.foreignAuthorId === 'hc:' + foreignId);
    if (author && author.id > 0) break;
    await new Promise(resolve => setTimeout(resolve, 1000));
  }
  assert.ok(author?.id > 0, 'New author did not leave the pending state');
  assert.equal(author.ebookQualityProfileId, 4, 'Old source profile must resolve to its ebook target; native target IDs stay unchanged');
  assert.equal(author.ebookMetadataProfileId, 2);
  // Synthetic keys over shared loopback in a network-none test namespace.
  // nosemgrep: typescript.react.security.react-insecure-request.react-insecure-request
  const legacyResponse = await fetch('http://127.0.0.1:8789/api/v1/author/' + author.id,
    { headers: { 'X-Api-Key': 'synthetic-e2e-admin-key-20261105' } });
  assert.equal(legacyResponse.status, 200);
  const legacy = await legacyResponse.json();
  assert.equal(legacy.ebookQualityProfileId, 2, 'Legacy clients must see the source profile identity for newly created authors');
  checks.push({ nativeClient: native, submittedProfileId: profileId, storedEbookProfileId: author.ebookQualityProfileId,
    storedMetadataProfileId: author.ebookMetadataProfileId, legacyEbookProfileId: legacy.ebookQualityProfileId, createdAuthorId: author.id });
  writeFileSync('/fixture/new-author-profile-checks.json', JSON.stringify(checks, null, 2));
}
console.log('New author profile handoff: unchanged source profile ID and explicit native profile ID both persist the correct ebook profile.');
