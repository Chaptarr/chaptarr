#!/usr/bin/env node

import assert from "node:assert/strict";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const seerrngRoot = process.env.SEERRNG_RUNTIME_PATH ?? "/seerrng";
const {
  default: ReadarrAPI,
} = require(`${seerrngRoot}/dist/api/servarr/readarr.js`);

const apiUrl = (
  process.env.CHAPTARR_API_URL ?? "http://chaptarrng:8789/api/v1"
).replace(/\/+$/, "");
const serviceKey = process.env.CHAPTARR_SEERR_API_KEY;
const globalKey = process.env.CHAPTARR_GLOBAL_API_KEY;
const rootFolderPath = process.env.CHAPTARR_SMOKE_ROOT_FOLDER ?? "/books";

if (!serviceKey || !globalKey || serviceKey === globalKey) {
  throw new Error(
    "Set distinct CHAPTARR_SEERR_API_KEY and CHAPTARR_GLOBAL_API_KEY values."
  );
}

const requestWithKey = (path, key, init = {}) =>
  fetch(/^https?:\/\//i.test(path) ? path : `${apiUrl}${path}`, {
    ...init,
    headers: {
      "X-Api-Key": key,
      ...(init.headers ?? {}),
    },
  });

const api = new ReadarrAPI({
  url: apiUrl,
  apiKey: serviceKey,
  mediaType: "ebook",
});
const audiobookApi = new ReadarrAPI({
  url: apiUrl,
  apiKey: serviceKey,
  mediaType: "audiobook",
});

const systemStatus = await api.getSystemStatus();
assert.equal(systemStatus.appName, "Chaptarr");

const capabilitiesResponse = await requestWithKey(
  "/system/capabilities",
  serviceKey
);
assert.equal(capabilitiesResponse.status, 200);
const capabilities = await capabilitiesResponse.json();
assert.equal(capabilities.contract, "chaptarrng-seerr-bookshelf");
assert.equal(capabilities.features?.bookshelfLegacyIdentityMap, true);
assert.equal(capabilities.features?.pendingAuthorImports, true);
assert.equal(capabilities.features?.pendingImportCancellation, true);
assert.equal(capabilities.features?.formatSpecificAvailabilityStatistics, true);

const legacyIdentitiesResponse = await requestWithKey(
  "/bookshelf/legacy-identities?entityType=Book&sourceIds=30",
  serviceKey
);
assert.equal(legacyIdentitiesResponse.status, 200);
const legacyIdentities = await legacyIdentitiesResponse.json();
assert.deepEqual(
  legacyIdentities.map(({ sourceId, targetId, mediaType }) => ({
    sourceId,
    targetId,
    mediaType,
  })),
  [
    { sourceId: 30, targetId: 30, mediaType: "audiobook" },
    { sourceId: 30, targetId: 31, mediaType: "ebook" },
  ]
);

const legacyBookSearchResponse = await requestWithKey("/command", globalKey, {
  method: "POST",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify({ name: "BookSearch", bookIds: [30] }),
});
assert.equal(legacyBookSearchResponse.status, 201);
const legacyBookSearchLocation = legacyBookSearchResponse.headers.get("Location");
assert.ok(legacyBookSearchLocation, "BookSearch should return its command URL");
const legacyBookSearchId = Number(
  new URL(legacyBookSearchLocation, apiUrl).pathname.split("/").at(-1)
);
assert.ok(Number.isSafeInteger(legacyBookSearchId) && legacyBookSearchId > 0);
const legacyBookSearchCommandResponse = await requestWithKey(
  `/command/${legacyBookSearchId}`,
  globalKey
);
assert.equal(legacyBookSearchCommandResponse.status, 200);
const legacyBookSearchCommand = await legacyBookSearchCommandResponse.json();
assert.deepEqual(
  legacyBookSearchCommand.body.bookIds,
  [30],
  "The legacy command response should project split format records to the original BookshelfNG book ID"
);
const nativeBookSearchCommandResponse = await requestWithKey(
  `/command/${legacyBookSearchId}`,
  globalKey,
  { headers: { "X-ChaptarrNG-Client": "native" } }
);
assert.equal(nativeBookSearchCommandResponse.status, 200);
const nativeBookSearchCommand = await nativeBookSearchCommandResponse.json();
assert.deepEqual(
  nativeBookSearchCommand.body.bookIds,
  [30, 31],
  "The queued native BookSearch should include both migrated format records"
);
const ebookFacade = new URL(apiUrl);
ebookFacade.pathname = `${ebookFacade.pathname.replace(/\/api\/v\d+\/?$/i, "")}/readarr/${capabilities.providerIdDialect}/ebook/api/v1`;
ebookFacade.search = "";
const ebookFacadeUrl = ebookFacade.toString().replace(/\/$/, "");
const scopedBookSearchResponse = await requestWithKey(
  `${ebookFacadeUrl}/command`,
  globalKey,
  {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ name: "BookSearch", bookIds: [30] }),
  }
);
assert.equal(scopedBookSearchResponse.status, 201);
const scopedBookSearchLocation = scopedBookSearchResponse.headers.get("Location");
assert.ok(scopedBookSearchLocation, "Scoped BookSearch should return its command URL");
const scopedBookSearchId = Number(
  new URL(scopedBookSearchLocation, apiUrl).pathname.split("/").at(-1)
);
assert.ok(Number.isSafeInteger(scopedBookSearchId) && scopedBookSearchId > 0);
const scopedBookSearchCommandResponse = await requestWithKey(
  `${ebookFacadeUrl}/command/${scopedBookSearchId}`,
  globalKey
);
assert.equal(scopedBookSearchCommandResponse.status, 200);
const scopedBookSearchCommand = await scopedBookSearchCommandResponse.json();
assert.deepEqual(
  scopedBookSearchCommand.body.bookIds,
  [31],
  "The ebook facade should resolve the legacy source ID to its ebook target"
);

const [legacyAudiobook, legacyEbook] = await Promise.all([
  audiobookApi.getBook(30),
  api.getBook(30),
]);
assert.equal(legacyAudiobook.id, 30);
assert.equal(legacyEbook.id, 31);

const [legacyAudiobookFiles, legacyEbookFiles] = await Promise.all([
  audiobookApi.getBookFiles(30),
  api.getBookFiles(30),
]);
assert.deepEqual(
  legacyAudiobookFiles.map(({ id, bookId }) => ({ id, bookId })),
  [{ id: 50, bookId: 30 }]
);
assert.deepEqual(
  legacyEbookFiles.map(({ id, bookId }) => ({ id, bookId })),
  [{ id: 51, bookId: 31 }]
);

const [
  legacyAudiobookDetails,
  legacyEbookDetails,
  audiobookHistory,
  ebookHistory,
] = await Promise.all([
  audiobookApi.getBook(legacyAudiobook.id),
  api.getBook(legacyEbook.id),
  audiobookApi.getBookHistory(legacyAudiobook.id),
  api.getBookHistory(legacyEbook.id),
]);
assert.equal(
  legacyAudiobookDetails.statistics?.bookFileCount,
  1,
  "SeerrNG should see the audiobook's file in its format-specific availability"
);
assert.equal(legacyAudiobookDetails.statistics?.ebookFileCount, 0);
assert.equal(legacyAudiobookDetails.statistics?.audiobookFileCount, 1);
assert.equal(legacyAudiobookDetails.statistics?.ebookSizeOnDisk, 0);
assert.equal(legacyAudiobookDetails.statistics?.audiobookSizeOnDisk, 100);
assert.equal(
  legacyEbookDetails.statistics?.bookFileCount,
  1,
  "SeerrNG should see the ebook's file in its format-specific availability"
);
assert.equal(legacyEbookDetails.statistics?.ebookFileCount, 1);
assert.equal(legacyEbookDetails.statistics?.audiobookFileCount, 0);
assert.equal(legacyEbookDetails.statistics?.ebookSizeOnDisk, 200);
assert.equal(legacyEbookDetails.statistics?.audiobookSizeOnDisk, 0);
assert.ok(
  ebookHistory.some(({ id, bookId }) => id === 93 && bookId === legacyEbook.id),
  "The migrated ebook import history should remain attached to the ebook"
);
assert.ok(
  !audiobookHistory.some(({ id }) => id === 93),
  "The ebook import history should not leak into the audiobook history"
);

const [qualityProfiles, metadataProfiles] = await Promise.all([
  api.getProfiles({ forceRefresh: true }),
  api.getMetadataProfiles(),
]);
const qualityProfile = qualityProfiles[0];
const metadataProfile =
  metadataProfiles.find((profile) => profile.name.toLowerCase() !== "none") ??
  metadataProfiles[0];
assert.ok(qualityProfile, "ChaptarrNG must expose an ebook quality profile");
assert.ok(metadataProfile, "ChaptarrNG must expose an ebook metadata profile");

const rootsResponse = await requestWithKey("/rootfolder", globalKey);
assert.equal(rootsResponse.status, 200);
const roots = await rootsResponse.json();
let root = roots.find((candidate) => candidate.path === rootFolderPath);
if (!root) {
  const createRootResponse = await requestWithKey("/rootfolder", globalKey, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      name: "SeerrNG lifecycle smoke",
      path: rootFolderPath,
      folderType: 2,
      ebookQualityProfileId: qualityProfile.id,
      ebookMetadataProfileId: metadataProfile.id,
    }),
  });
  assert.ok(
    [200, 201].includes(createRootResponse.status),
    `Could not create the disposable ebook root folder: ${createRootResponse.status}`
  );
  const refreshedRootsResponse = await requestWithKey("/rootfolder", globalKey);
  assert.equal(refreshedRootsResponse.status, 200);
  const refreshedRoots = await refreshedRootsResponse.json();
  root = refreshedRoots.find((candidate) => candidate.path === rootFolderPath);
}
assert.ok(root, "The disposable ebook root folder should be available");
assert.equal(root.folderType, 2, "The smoke root folder must be ebook-only");

const idSeed = BigInt(Date.now());
const providerPrefix = capabilities.providerIdDialect === "gr" ? "gr" : "hc";
const workId = `${providerPrefix}:${idSeed + 1n}`;
const authorId = `${providerPrefix}:${idSeed + 2n}`;
const editionId = `${providerPrefix}:${idSeed + 3n}`;
const syntheticTitle = `SeerrNG lifecycle smoke ${idSeed}`;
let pendingId;
let createdBookId;

try {
  const added = await api.addBook({
    title: syntheticTitle,
    foreignBookId: workId,
    qualityProfileId: qualityProfile.id,
    metadataProfileId: metadataProfile.id,
    rootFolderPath,
    monitored: true,
    author: {
      foreignAuthorId: authorId,
      authorName: syntheticTitle,
      rootFolderPath,
      qualityProfileId: qualityProfile.id,
      metadataProfileId: metadataProfile.id,
      monitored: true,
      addOptions: {
        monitor: "specificBook",
        searchForMissingBooks: false,
      },
    },
    editions: [
      { foreignEditionId: editionId, title: syntheticTitle, monitored: true },
    ],
    addOptions: { searchForNewBook: true },
  });

  if (!added.pending) {
    if (added.createdBook === true && Number.isSafeInteger(added.id)) {
      createdBookId = added.id;
    }
    throw new Error(
      `Expected unavailable synthetic author metadata to create a pending request; received pending=${added.pending}.`
    );
  }

  pendingId = added.pendingId;
  assert.ok(Number.isSafeInteger(pendingId) && pendingId > 0);

  const pending = await api.getPendingAuthorImport(pendingId);
  assert.ok(pending, "SeerrNG adapter should read the pending import");
  assert.equal(pending.id, pendingId);

  await api.cancelPendingAuthorImport(pendingId);
  const cancelled = await api.getPendingAuthorImport(pendingId);
  assert.ok(
    cancelled,
    "Cancelled imports remain queryable as terminal records"
  );
  assert.equal(cancelled.id, pendingId);
  assert.equal(cancelled.overallStatus, "Failed");
  assert.equal(cancelled.lastError, "Cancelled by user");

  console.log(
    `SeerrNG ReadarrAPI read format-specific file counts and history, then created, polled, and cancelled synthetic pending import ${pendingId}; ChaptarrNG returned its terminal cancellation state.`
  );
} finally {
  if (pendingId !== undefined) {
    await api.cancelPendingAuthorImport(pendingId);
  }
  if (createdBookId !== undefined) {
    const cleanupResponse = await requestWithKey(
      `/book/${createdBookId}?deleteFiles=false&addImportListExclusion=false`,
      globalKey,
      { method: "DELETE" }
    );
    assert.ok(
      [200, 204].includes(cleanupResponse.status),
      `Could not remove the synthetic book created during the smoke: ${cleanupResponse.status}`
    );
  }
}
