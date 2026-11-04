#!/usr/bin/env node

import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdir, writeFile } from "node:fs/promises";
import { dirname } from "node:path";
import {
  compareResponses,
  normalizeProviderApiResponse,
  sanitizeApiResponse,
} from "./bookshelfng-api-contract.mjs";

const sourceUrl = (
  process.env.BOOKSHELFNG_API_URL ?? "http://127.0.0.1:18787/api/v1"
).replace(/\/+$/, "");
const targetUrl = (
  process.env.CHAPTARRNG_API_URL ?? "http://127.0.0.1:18789/api/v1"
).replace(/\/+$/, "");
const sourceKey = process.env.BOOKSHELFNG_API_KEY;
const targetKey = process.env.CHAPTARRNG_API_KEY;
const sourceImage = process.env.BOOKSHELFNG_SOURCE_IMAGE;
const sourceImageId = process.env.BOOKSHELFNG_SOURCE_IMAGE_ID;
const targetCommit = process.env.CHAPTARRNG_TARGET_COMMIT;
const targetImageId = process.env.CHAPTARRNG_TARGET_IMAGE_ID;
const fixtureSha256 = process.env.BOOKSHELFNG_FIXTURE_SHA256;
const reportPath = process.env.BOOKSHELFNG_DIFFERENTIAL_REPORT;
const includeDebugValues =
  process.env.BOOKSHELFNG_DIFFERENTIAL_DEBUG_VALUES === "true";

for (const [name, value] of Object.entries({
  BOOKSHELFNG_API_KEY: sourceKey,
  CHAPTARRNG_API_KEY: targetKey,
  BOOKSHELFNG_SOURCE_IMAGE: sourceImage,
  BOOKSHELFNG_SOURCE_IMAGE_ID: sourceImageId,
  CHAPTARRNG_TARGET_COMMIT: targetCommit,
  CHAPTARRNG_TARGET_IMAGE_ID: targetImageId,
  BOOKSHELFNG_FIXTURE_SHA256: fixtureSha256,
  BOOKSHELFNG_DIFFERENTIAL_REPORT: reportPath,
})) {
  assert.ok(value, name + " is required.");
}

function sha256(value) {
  return createHash("sha256").update(JSON.stringify(value)).digest("hex");
}

async function getJson(baseUrl, apiKey, path) {
  try {
    const response = await fetch(baseUrl + path, {
      headers: { "X-Api-Key": apiKey },
      signal: AbortSignal.timeout(30_000),
    });
    if (!response.ok) {
      return { status: response.status, value: null, error: null };
    }
    try {
      return {
        status: response.status,
        value: sanitizeApiResponse(await response.json()),
        error: null,
      };
    } catch {
      return { status: response.status, value: null, error: "invalid-json" };
    }
  } catch (error) {
    return {
      status: 0,
      value: null,
      error: error instanceof Error ? error.name : "request-failed",
    };
  }
}

async function waitForPing(baseUrl, name) {
  const pingUrl = new URL("/ping", baseUrl).toString();
  for (let attempt = 1; attempt <= 120; attempt++) {
    try {
      const response = await fetch(pingUrl, {
        signal: AbortSignal.timeout(3_000),
      });
      if (response.ok) {
        console.log(`${name} became healthy after ${attempt} checks.`);
        return;
      }
    } catch {
      // The two isolated services may still be migrating their databases.
    }
    if (attempt < 120) {
      await new Promise((resolve) => setTimeout(resolve, 5_000));
    }
  }
  throw new Error(`${name} did not become healthy: ${pingUrl}`);
}

const results = [];

async function compare(caseId, path, providerResource = false) {
  const [source, target] = await Promise.all([
    getJson(sourceUrl, sourceKey, path),
    getJson(targetUrl, targetKey, path),
  ]);
  let comparison;
  let providerPresentationDifferencePaths = [];
  if (source.status === 200 && target.status === 200) {
    if (providerResource) {
      const originalComparison = compareResponses(source.value, target.value);
      comparison = compareResponses(
        normalizeProviderApiResponse(source.value),
        normalizeProviderApiResponse(target.value),
        { includeDebugValues },
      );
      const semanticPaths = new Set(comparison.mismatchPaths);
      providerPresentationDifferencePaths = originalComparison.mismatchPaths
        .filter((mismatchPath) => !semanticPaths.has(mismatchPath));
    } else {
      comparison = compareResponses(source.value, target.value, { includeDebugValues });
    }
  } else {
    comparison = {
      mismatchPaths: ["/httpStatus"],
      mismatchDetails: [
        {
          path: "/httpStatus",
          kind: "http-status-difference",
          sourceStatus: source.status,
          targetStatus: target.status,
        },
      ],
      additivePaths: [],
      additiveDetails: [],
    };
  }
  const mismatchDetails = comparison.mismatchDetails;
  const mismatchPaths = comparison.mismatchPaths;
  const additiveDetails = comparison.additiveDetails;
  const additivePaths = comparison.additivePaths;
  const result = {
    caseId,
    method: "GET",
    path,
    sourceStatus: source.status,
    targetStatus: target.status,
    sourceError: source.error,
    targetError: target.error,
    sourceSha256: source.value === null ? null : sha256(source.value),
    targetSha256: target.value === null ? null : sha256(target.value),
    mismatchPaths,
    mismatchDetails,
    additivePaths,
    additiveDetails,
    providerPresentationDifferenceCount: providerPresentationDifferencePaths.length,
    providerPresentationDifferencePaths: providerPresentationDifferencePaths.slice(0, 20),
    result:
      source.status === 200 && target.status === 200 && mismatchPaths.length === 0
        ? "pass"
        : "fail",
  };
  results.push(result);
  return { source, target };
}

const cases = [
  ["authors.list", "/author"],
  ["books.list", "/book"],
  ["series.list", "/series"],
  ["history.list", "/history"],
  ["root-folders.list", "/rootFolder"],
  ["quality-profiles.list", "/qualityProfile"],
  ["metadata-profiles.list", "/metadataProfile"],
  ["indexers.list", "/indexer", true],
  ["download-clients.list", "/downloadClient", true],
  ["notifications.list", "/notification", true],
];

await Promise.all([
  waitForPing(sourceUrl, "BookshelfNG"),
  waitForPing(targetUrl, "ChaptarrNG"),
]);

for (const [caseId, path, providerResource = false] of cases) {
  const { source, target } = await compare(caseId, path, providerResource);
  if (
    providerResource &&
    Array.isArray(source.value) &&
    Array.isArray(target.value)
  ) {
    for (const provider of source.value) {
      if (Number.isInteger(provider?.id)) {
        await compare(`${caseId}.detail.${provider.id}`, `${path}/${provider.id}`, true);
      }
    }
  }
}

const authorsResult = results.find((result) => result.caseId === "authors.list");
const booksResult = results.find((result) => result.caseId === "books.list");

if (
  authorsResult?.sourceStatus === 200 &&
  authorsResult?.targetStatus === 200 &&
  booksResult?.sourceStatus === 200 &&
  booksResult?.targetStatus === 200
) {
  const [sourceAuthors, targetAuthors, sourceBooks, targetBooks] = await Promise.all([
    getJson(sourceUrl, sourceKey, "/author"),
    getJson(targetUrl, targetKey, "/author"),
    getJson(sourceUrl, sourceKey, "/book"),
    getJson(targetUrl, targetKey, "/book"),
  ]);
  if (
    Array.isArray(sourceAuthors.value) &&
    Array.isArray(targetAuthors.value)
  ) {
    for (const author of sourceAuthors.value) {
      await compare(
        "author.detail." + author.id,
        "/author/" + encodeURIComponent(author.id),
      );
    }
  }
  if (Array.isArray(sourceBooks.value) && Array.isArray(targetBooks.value)) {
    for (const book of sourceBooks.value) {
      await compare(
        "book.detail." + book.id,
        "/book/" + encodeURIComponent(book.id),
      );
      await compare(
        "book.files." + book.id,
        "/bookFile?bookId=" + encodeURIComponent(book.id),
      );
    }
  }
}

const report = {
  schemaVersion: 4,
  suite: "bookshelfng-read-api-differential",
  source: {
    release: "BookshelfNG 0.4.21.76",
    image: sourceImage,
    imageId: sourceImageId,
  },
  target: {
    commit: targetCommit,
    imageId: targetImageId,
  },
  fixture: {
    description: "synthetic expanded BookshelfNG schema 158, seeded identically",
    sourceDatabaseSha256: fixtureSha256,
  },
  normalization: {
    objectKeys: "sorted recursively",
    collectionIdentity: "matched by id when available; source collection order preserved",
    providerFields: "matched by name; select options matched by value",
    providerPresentation: "labels, hints, help, links, control type, and display order are reported separately; persisted field values and option values remain strict",
    directDownloadUrls: "newline-delimited target URLs are compared with the legacy ordered tag array",
    secretFields: "compared as empty or present, never recorded",
  },
  debugValues: includeDebugValues,
  caseCount: results.length,
  passed: results.filter((result) => result.result === "pass").length,
  failed: results.filter((result) => result.result !== "pass").length,
  additiveDifferenceCount: results.reduce((total, result) => total + result.additivePaths.length, 0),
  providerPresentationDifferenceCount: results.reduce(
    (total, result) => total + result.providerPresentationDifferenceCount,
    0,
  ),
  results,
};

await mkdir(dirname(reportPath), { recursive: true });
await writeFile(reportPath, JSON.stringify(report, null, 2) + "\n", {
  mode: 0o600,
});

console.log(
  JSON.stringify(
    {
      suite: report.suite,
      targetCommit: report.target.commit,
      caseCount: report.caseCount,
      passed: report.passed,
      failed: report.failed,
      providerPresentationDifferenceCount: report.providerPresentationDifferenceCount,
      reportPath,
    },
    null,
    2,
  ),
);
console.log(report.failed === 0 ? "RESULT=PASS" : "RESULT=FAIL");
if (report.failed !== 0) process.exitCode = 1;
