#!/usr/bin/env node

import assert from "node:assert/strict";
import { createRequire } from "node:module";
import { extname } from "node:path";

const require = createRequire(import.meta.url);
const seerrngRoot = process.env.SEERRNG_RUNTIME_PATH ?? "/seerrng";
const {
  default: ReadarrAPI,
} = require(`${seerrngRoot}/dist/api/servarr/readarr.js`);

const apiUrl = (
  process.env.CHAPTARR_API_URL ?? "http://chaptarrng:8789/api/v1"
).replace(/\/+$/, "");
const adminKey = process.env.CHAPTARR_GLOBAL_API_KEY;
const serviceKey = process.env.CHAPTARR_SEERR_API_KEY;
const pageSize = 200;
const maximumBooksPerFormat = 100_000;
const maximumFileSamplesForHistory = 50;
const ebookExtensions = new Set([
  ".epub",
  ".kepub",
  ".mobi",
  ".azw3",
  ".azw",
  ".pdf",
]);
const audiobookExtensions = new Set([
  ".flac",
  ".ape",
  ".wavpack",
  ".wav",
  ".alac",
  ".mp2",
  ".mp3",
  ".wma",
  ".m4a",
  ".m4p",
  ".m4b",
  ".mp4",
  ".aac",
  ".mp4a",
  ".ogg",
  ".oga",
  ".opus",
  ".vorbis",
  ".mka",
]);

const fileMediaType = (path) => {
  const extension = extname(path ?? "").toLowerCase();
  if (ebookExtensions.has(extension)) return "ebook";
  if (audiobookExtensions.has(extension)) return "audiobook";
  return null;
};

if (!adminKey || !serviceKey) {
  throw new Error(
    "Set CHAPTARR_GLOBAL_API_KEY and CHAPTARR_SEERR_API_KEY to disposable smoke keys."
  );
}

const requestWithServiceKey = (path) =>
  fetch(`${apiUrl}${path}`, {
    headers: { "X-Api-Key": serviceKey },
  });

const assertDenied = async (path) => {
  const response = await requestWithServiceKey(path);
  assert.ok(
    [401, 403].includes(response.status),
    `The restricted key unexpectedly accessed ${path}.`
  );
};

const readUnmappedFiles = async (mediaType) => {
  const response = await fetch(
    `${apiUrl}/bookfile?unmapped=true&mediaType=${mediaType}`,
    {
      headers: { "X-Api-Key": adminKey },
    }
  );
  assert.equal(response.status, 200);
  const files = await response.json();
  assert.ok(Array.isArray(files));
  assert.ok(files.length > 0, `No unmapped ${mediaType} files were returned.`);
  assert.ok(
    files.every((file) => file.mediaType === mediaType && file.bookId === 0),
    `Unmapped ${mediaType} files lost their media type or gained a book link.`
  );
  return files.length;
};

const readLibrary = async (mediaType) => {
  const api = new ReadarrAPI({ url: apiUrl, apiKey: serviceKey, mediaType });
  const ids = new Set();
  const fileCandidates = [];
  let expectedTotal;
  let offset = 0;
  let formatMismatchedBooks = 0;
  let booksWithFormatStatistics = 0;

  do {
    const page = await api.getBooksPage(offset, pageSize);
    if (expectedTotal === undefined) {
      expectedTotal = page.totalCount;
      assert.ok(
        expectedTotal > 0,
        `The converted ${mediaType} library is unexpectedly empty.`
      );
      assert.ok(
        expectedTotal <= maximumBooksPerFormat,
        `The converted ${mediaType} library exceeds the smoke safety limit.`
      );
    } else {
      assert.equal(
        page.totalCount,
        expectedTotal,
        `The converted ${mediaType} library total changed while paging.`
      );
    }

    assert.ok(
      page.books.length <= pageSize,
      `The ${mediaType} API returned more than the requested page size.`
    );
    assert.ok(
      page.books.length > 0 || offset >= expectedTotal,
      `The ${mediaType} API returned an empty page before the end of the library.`
    );

    for (const book of page.books) {
      assert.ok(Number.isSafeInteger(book.id) && book.id > 0);
      assert.ok(
        !ids.has(book.id),
        `The ${mediaType} API repeated a book record.`
      );
      ids.add(book.id);
      if (book.mediaType !== undefined) {
        assert.equal(book.mediaType, mediaType);
      }

      const statistics = book.statistics;
      if (
        Number.isSafeInteger(statistics?.bookFileCount) &&
        Number.isSafeInteger(statistics?.ebookFileCount) &&
        Number.isSafeInteger(statistics?.audiobookFileCount)
      ) {
        booksWithFormatStatistics++;
        const otherFormatCount =
          mediaType === "ebook"
            ? statistics.audiobookFileCount
            : statistics.ebookFileCount;
        if (otherFormatCount > 0) formatMismatchedBooks++;
      }

      const fileCount = book.statistics?.bookFileCount;
      if (Number.isSafeInteger(fileCount) && fileCount > 0) {
        fileCandidates.push(book);
      }
    }

    offset += page.books.length;
  } while (offset < expectedTotal);

  assert.equal(
    ids.size,
    expectedTotal,
    `The ${mediaType} paged API did not return every record exactly once.`
  );
  assert.ok(
    fileCandidates.length > 0,
    `The converted ${mediaType} library has no book with an available file.`
  );
  assert.ok(
    booksWithFormatStatistics > 0,
    `The ${mediaType} paged API did not provide format-specific file statistics.`
  );
  assert.equal(
    formatMismatchedBooks,
    0,
    `The ${mediaType} library contains books with files from the other format.`
  );

  let sampledFiles = 0;
  let sampledEbookFiles = 0;
  let sampledAudiobookFiles = 0;
  let sampledUnclassifiedFiles = 0;
  let sampledHistory = 0;
  let historyBookFound = false;
  let sampleFound = false;

  for (const candidate of fileCandidates.slice(
    0,
    maximumFileSamplesForHistory
  )) {
    const [book, files, history] = await Promise.all([
      api.getBook(candidate.id),
      api.getBookFiles(candidate.id),
      api.getBookHistory(candidate.id),
    ]);
    const statistics = book.statistics;
    const ownFormatCount =
      mediaType === "ebook"
        ? statistics?.ebookFileCount
        : statistics?.audiobookFileCount;
    const otherFormatCount =
      mediaType === "ebook"
        ? statistics?.audiobookFileCount
        : statistics?.ebookFileCount;

    assert.ok(
      Number.isSafeInteger(statistics?.bookFileCount) &&
        statistics.bookFileCount > 0,
      `The ${mediaType} detail response lost its file availability.`
    );
    assert.equal(
      otherFormatCount,
      0,
      `The ${mediaType} detail response includes files from the other format.`
    );
    assert.equal(
      files.length,
      statistics.bookFileCount,
      `The ${mediaType} file endpoint disagrees with book availability.`
    );
    assert.ok(
      files.every((file) => file.bookId === book.id),
      `The ${mediaType} file endpoint returned a file for another book.`
    );
    const ebookFiles = files.filter(
      (file) => fileMediaType(file.path ?? file.relativePath) === "ebook"
    ).length;
    const audiobookFiles = files.filter(
      (file) => fileMediaType(file.path ?? file.relativePath) === "audiobook"
    ).length;
    const unclassifiedFiles = files.length - ebookFiles - audiobookFiles;
    assert.equal(
      statistics?.ebookFileCount,
      ebookFiles,
      `The ${mediaType} ebook statistics disagree with file extensions.`
    );
    assert.equal(
      statistics?.audiobookFileCount,
      audiobookFiles,
      `The ${mediaType} audiobook statistics disagree with file extensions.`
    );
    assert.equal(
      ownFormatCount,
      mediaType === "ebook" ? ebookFiles : audiobookFiles,
      `The ${mediaType} detail response disagrees with file extensions.`
    );
    assert.equal(
      otherFormatCount,
      mediaType === "ebook" ? audiobookFiles : ebookFiles,
      `The ${mediaType} detail response misclassifies the other format.`
    );
    assert.ok(
      history.every((item) => item.bookId === book.id),
      `The ${mediaType} history endpoint returned an event for another book.`
    );

    sampledFiles += files.length;
    sampledEbookFiles += ebookFiles;
    sampledAudiobookFiles += audiobookFiles;
    sampledUnclassifiedFiles += unclassifiedFiles;
    sampledHistory += history.length;
    sampleFound = true;
    if (history.length > 0) {
      historyBookFound = true;
      break;
    }
  }

  assert.ok(
    sampleFound,
    `The converted ${mediaType} file sample was not readable.`
  );

  return {
    bookCount: expectedTotal,
    fileSampleCount: sampledFiles,
    ebookFileSampleCount: sampledEbookFiles,
    audiobookFileSampleCount: sampledAudiobookFiles,
    unclassifiedFileSampleCount: sampledUnclassifiedFiles,
    formatStatisticsRecordCount: booksWithFormatStatistics,
    historySampleCount: sampledHistory,
    historyBookFound,
  };
};

const ebookApi = new ReadarrAPI({
  url: apiUrl,
  apiKey: serviceKey,
  mediaType: "ebook",
});
const status = await ebookApi.getSystemStatus();
assert.equal(status.appName, "Chaptarr");

const capabilitiesResponse = await requestWithServiceKey(
  "/system/capabilities"
);
assert.equal(capabilitiesResponse.status, 200);
const capabilities = await capabilitiesResponse.json();
assert.equal(capabilities.contract, "chaptarrng-seerr-bookshelf");
assert.equal(capabilities.features?.formatScopedFacade, true);
assert.equal(capabilities.features?.pagedLibrary, true);
assert.equal(capabilities.features?.bookshelfLegacyIdentityMap, true);
assert.equal(capabilities.features?.formatSpecificAvailabilityStatistics, true);

await Promise.all([
  assertDenied("/config/host"),
  assertDenied("/command"),
  assertDenied("/bookfile?unmapped=true&mediaType=ebook"),
]);

const [ebooks, audiobooks, unmappedEbookFiles, unmappedAudiobookFiles] =
  await Promise.all([
    readLibrary("ebook"),
    readLibrary("audiobook"),
    readUnmappedFiles("ebook"),
    readUnmappedFiles("audiobook"),
  ]);

console.log(
  JSON.stringify({
    result: "passed",
    ebookRecords: ebooks.bookCount,
    audiobookRecords: audiobooks.bookCount,
    ebookSampleFiles: ebooks.fileSampleCount,
    audiobookSampleFiles: audiobooks.fileSampleCount,
    ebookSampleExtensionCounts: ebooks.ebookFileSampleCount,
    audiobookSampleExtensionCounts: audiobooks.audiobookFileSampleCount,
    unclassifiedSampleFiles:
      ebooks.unclassifiedFileSampleCount +
      audiobooks.unclassifiedFileSampleCount,
    ebookFormatStatisticsRecords: ebooks.formatStatisticsRecordCount,
    audiobookFormatStatisticsRecords: audiobooks.formatStatisticsRecordCount,
    unmappedEbookFiles,
    unmappedAudiobookFiles,
    ebookSampleHistory: ebooks.historySampleCount,
    audiobookSampleHistory: audiobooks.historySampleCount,
    ebookSampleHasHistory: ebooks.historyBookFound,
    audiobookSampleHasHistory: audiobooks.historyBookFound,
    restrictedKeyDeniedAdminCommandAndUnmappedRoutes: true,
  })
);
