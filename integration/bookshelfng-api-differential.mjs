#!/usr/bin/env node

import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdir, writeFile } from "node:fs/promises";
import { dirname } from "node:path";
import { isDeepStrictEqual } from "node:util";

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

const secretKey =
  /(?:password|secret|token|api.?key|authorization|credential)/i;

function sanitize(value, key = "") {
  if (secretKey.test(key)) {
    return value === null || value === undefined || value === ""
      ? ""
      : "<redacted-present>";
  }
  if (Array.isArray(value)) {
    return value.map((item) => {
      if (key !== "fields" || !item || typeof item !== "object") {
        return sanitize(item);
      }
      const fieldDescription = [item.name, item.label, item.key, item.id]
        .filter((part) => typeof part === "string")
        .join(" ");
      const redactValue =
        item.privacy === true ||
        item.type === "password" ||
        secretKey.test(fieldDescription);
      return Object.fromEntries(
        Object.keys(item)
          .sort()
          .map((fieldKey) => [
            fieldKey,
            fieldKey === "value" && redactValue
              ? item.value === null || item.value === undefined || item.value === ""
                ? ""
                : "<redacted-present>"
              : sanitize(item[fieldKey], fieldKey),
          ]),
      );
    });
  }
  if (value && typeof value === "object") {
    return Object.fromEntries(
      Object.keys(value)
        .sort()
        .map((childKey) => [childKey, sanitize(value[childKey], childKey)]),
    );
  }
  return value;
}

function debugValue(value) {
  if (typeof value === "string") {
    return value.length > 160 ? value.slice(0, 160) + "…" : value;
  }
  if (value === null || typeof value === "number" || typeof value === "boolean") {
    return value;
  }
  if (Array.isArray(value)) {
    return { type: "array", length: value.length };
  }
  if (value && typeof value === "object") {
    return { type: "object", keys: Object.keys(value).slice(0, 25) };
  }
  return { type: typeof value };
}

function differences(expected, actual, path = "", output = []) {
  if (output.length >= 100 || isDeepStrictEqual(expected, actual)) {
    return output;
  }

  if (Array.isArray(expected) && Array.isArray(actual)) {
    if (expected.length !== actual.length) {
      output.push({
        path: (path || "") + "/length",
        kind: "array-length",
        sourceLength: expected.length,
        targetLength: actual.length,
      });
    }
    for (let index = 0; index < Math.min(expected.length, actual.length); index++) {
      differences(expected[index], actual[index], path + "/" + index, output);
      if (output.length >= 100) break;
    }
    return output;
  }

  if (
    expected &&
    actual &&
    typeof expected === "object" &&
    typeof actual === "object" &&
    !Array.isArray(expected) &&
    !Array.isArray(actual)
  ) {
    const keys = new Set([...Object.keys(expected), ...Object.keys(actual)]);
    for (const key of [...keys].sort()) {
      if (!(key in expected) || !(key in actual)) {
        const detail = {
          path: path + "/" + key,
          kind: key in expected ? "source-only" : "target-only",
        };
        if (includeDebugValues) {
          if (key in expected) detail.sourceValue = debugValue(expected[key]);
          if (key in actual) detail.targetValue = debugValue(actual[key]);
        }
        output.push(detail);
      } else {
        differences(expected[key], actual[key], path + "/" + key, output);
      }
      if (output.length >= 100) break;
    }
    return output;
  }

  const detail = {
    path: path || "/",
    kind:
      typeof expected !== typeof actual ||
      Array.isArray(expected) !== Array.isArray(actual)
        ? "type-difference"
        : "value-difference",
  };
  if (includeDebugValues) {
    detail.sourceValue = debugValue(expected);
    detail.targetValue = debugValue(actual);
  }
  output.push(detail);
  return output;
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
        value: sanitize(await response.json()),
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

async function compare(caseId, path) {
  const [source, target] = await Promise.all([
    getJson(sourceUrl, sourceKey, path),
    getJson(targetUrl, targetKey, path),
  ]);
  const mismatchDetails =
    source.status === 200 && target.status === 200
      ? differences(source.value, target.value)
      : [
          {
            path: "/httpStatus",
            kind: "http-status-difference",
            sourceStatus: source.status,
            targetStatus: target.status,
          },
        ];
  const mismatchPaths = mismatchDetails.map((detail) => detail.path);
  results.push({
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
    result:
      source.status === 200 && target.status === 200 && mismatchPaths.length === 0
        ? "pass"
        : "fail",
  });
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
  ["indexers.list", "/indexer"],
  ["download-clients.list", "/downloadClient"],
  ["notifications.list", "/notification"],
];

await Promise.all([
  waitForPing(sourceUrl, "BookshelfNG"),
  waitForPing(targetUrl, "ChaptarrNG"),
]);

for (const [caseId, path] of cases) {
  await compare(caseId, path);
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
  schemaVersion: 2,
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
    arrayOrder: "preserved",
    secretFields: "compared as empty or present, never recorded",
  },
  debugValues: includeDebugValues,
  caseCount: results.length,
  passed: results.filter((result) => result.result === "pass").length,
  failed: results.filter((result) => result.result !== "pass").length,
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
      reportPath,
    },
    null,
    2,
  ),
);
console.log(report.failed === 0 ? "RESULT=PASS" : "RESULT=FAIL");
if (report.failed !== 0) process.exitCode = 1;
