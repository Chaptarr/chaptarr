#!/usr/bin/env node

import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdir, writeFile } from "node:fs/promises";
import { dirname } from "node:path";
import {
  annotateProviderMismatchDetails,
  compareResponses,
  classifySystemDeploymentDifferences,
  hasVerifiedHostSettingsAuthentication,
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

function providerSchemaOrderDifferencePaths(source, target) {
  const isSchemaArray = (value) =>
    Array.isArray(value) &&
    value.length > 0 &&
    value.every((item) =>
      item &&
      typeof item === "object" &&
      !Array.isArray(item) &&
      !Object.hasOwn(item, "id") &&
      typeof item.implementation === "string",
    );
  if (!isSchemaArray(source) || !isSchemaArray(target)) return [];

  const targetImplementations = new Set(target.map((item) => item.implementation));
  const sourceImplementations = new Set(source.map((item) => item.implementation));
  const sharedSourceOrder = source
    .map((item) => item.implementation)
    .filter((implementation) => targetImplementations.has(implementation));
  const sharedTargetOrder = target
    .map((item) => item.implementation)
    .filter((implementation) => sourceImplementations.has(implementation));
  return JSON.stringify(sharedSourceOrder) === JSON.stringify(sharedTargetOrder)
    ? []
    : ["/provider-schema-order"];
}

async function getJson(baseUrl, apiKey, path, native = false) {
  try {
    const response = await fetch(baseUrl + path, {
      headers: { "X-Api-Key": apiKey, ...(native ? { "X-ChaptarrNG-Client": "native" } : {}) },
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

async function requestJson(baseUrl, apiKey, method, path, body) {
  try {
    const response = await fetch(baseUrl + path, {
      method,
      headers: {
        "X-Api-Key": apiKey,
        ...(body === undefined ? {} : { "Content-Type": "application/json" }),
      },
      body: body === undefined ? undefined : JSON.stringify(body),
      signal: AbortSignal.timeout(30_000),
    });
    const text = response.status === 204 ? "" : await response.text();
    if (!text) {
      return { status: response.status, value: null, error: null };
    }
    try {
      return {
        status: response.status,
        value: sanitizeApiResponse(JSON.parse(text)),
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

async function compareBasicAuthentication(caseId, password, expectedStatus, path = "/") {
  const headers = password === null ? {} : {
    Authorization: "Basic " + Buffer.from("fixture-user:" + password).toString("base64"),
  };
  async function probe(baseUrl) {
    try {
      const response = await fetch(path === "/" ? new URL("/", baseUrl) : baseUrl + path, {
        headers,
        redirect: "manual",
        signal: AbortSignal.timeout(30_000),
      });
      await response.arrayBuffer();
      return { status: response.status, error: null };
    } catch (error) {
      return { status: 0, error: error instanceof Error ? error.name : "request-failed" };
    }
  }
  const [source, target] = await Promise.all([probe(sourceUrl), probe(targetUrl)]);
  const passed = source.status === expectedStatus && target.status === expectedStatus;
  results.push({
    caseId, method: "GET", path, expectedStatus,
    sourceStatus: source.status, targetStatus: target.status,
    sourceError: source.error, targetError: target.error,
    sourceSha256: null, targetSha256: null,
    mismatchPaths: passed ? [] : ["/httpStatus"],
    mismatchDetails: passed ? [] : [{ path: "/httpStatus", kind: "authentication-status-difference", expectedStatus, sourceStatus: source.status, targetStatus: target.status }],
    additivePaths: [], additiveDetails: [],
    providerPresentationDifferenceCount: 0, providerPresentationDifferencePaths: [],
    result: passed ? "pass" : "fail",
  });
}

async function verifyHostConfigReadback() {
  // Raw secrets remain only in memory while exercising an unchanged client's
  // GET/PUT flow. Reports contain only sanitized hashes and mismatch paths.
  let before;
  let savedStatus = 0;
  let comparison = { mismatchPaths: ["/request"], mismatchDetails: [], additivePaths: [], additiveDetails: [] };
  let secretsHidden = false;
  try {
    const headers = { "X-Api-Key": targetKey };
    const response = await fetch(targetUrl + "/config/host", { headers, signal: AbortSignal.timeout(30_000) });
    assert.equal(response.status, 200);
    before = await response.json();
    secretsHidden = before.password === "" && before.passwordConfirmation === "";
    const saved = await fetch(targetUrl + "/config/host/1", {
      method: "PUT", headers: { ...headers, "Content-Type": "application/json" },
      body: JSON.stringify(before), signal: AbortSignal.timeout(30_000),
    });
    savedStatus = saved.status;
    await saved.arrayBuffer();
    const after = await getJson(targetUrl, targetKey, "/config/host");
    comparison = compareResponses(sanitizeApiResponse(before), after.value);
  } catch {
    // Never serialize the request body, credential values or exception details.
  }
  const passed = secretsHidden && savedStatus === 202 && comparison.mismatchPaths.length === 0;
  results.push({
    caseId: "host-config.target-readback", method: "PUT", path: "/config/host/1",
    sourceStatus: null, targetStatus: savedStatus,
    sourceSha256: null, targetSha256: before ? sha256(sanitizeApiResponse(before)) : null,
    mismatchPaths: passed ? [] : comparison.mismatchPaths.concat(secretsHidden ? [] : ["/password-visibility"], savedStatus === 202 ? [] : ["/httpStatus"]),
    mismatchDetails: comparison.mismatchDetails,
    additivePaths: comparison.additivePaths, additiveDetails: comparison.additiveDetails,
    providerPresentationDifferenceCount: 0, providerPresentationDifferencePaths: [],
    passwordWriteOnly: secretsHidden,
    result: passed ? "pass" : "fail",
  });
  await compareBasicAuthentication("authentication.legacy-password-after-settings-save", "fixture-password", 200);
  await compareBasicAuthentication("authentication.wrong-password-after-settings-save", "incorrect-fixture-password", 401);
}

async function compare(caseId, path, providerResource = false, expectedTargetStatus = null) {
  const [source, target] = await Promise.all([
    getJson(sourceUrl, sourceKey, path),
    getJson(targetUrl, targetKey, path),
  ]);
  let comparison;
  let providerPresentationDifferencePaths = [];
  const expectedPolicyDifference =
    expectedTargetStatus !== null &&
    source.status === 200 &&
    target.status === expectedTargetStatus;
  if (expectedPolicyDifference) {
    comparison = {
      mismatchPaths: [],
      mismatchDetails: [],
      additivePaths: [],
      additiveDetails: [],
    };
  } else if (source.status === 200 && target.status === 200) {
    if (providerResource) {
      const originalComparison = compareResponses(source.value, target.value);
      comparison = compareResponses(
        normalizeProviderApiResponse(source.value),
        normalizeProviderApiResponse(target.value),
        { includeDebugValues },
      );
      const semanticPaths = new Set(comparison.mismatchPaths);
      providerPresentationDifferencePaths = originalComparison.mismatchPaths
        .filter((mismatchPath) => !semanticPaths.has(mismatchPath))
        .concat(providerSchemaOrderDifferencePaths(source.value, target.value));
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
  const mismatchDetails = providerResource
    ? annotateProviderMismatchDetails(comparison.mismatchDetails, target.value)
    : comparison.mismatchDetails;
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
    expectedPolicyDifference: expectedPolicyDifference
      ? "Production ChaptarrNG hides developer route diagnostics with HTTP 404."
      : null,
    inconclusiveReason:
      source.status >= 500 && target.status === 200
        ? "BookshelfNG returned a server error, so this fixture cannot provide a valid response for parity comparison."
        : null,
    result:
      expectedPolicyDifference
        ? "expected-policy-difference"
        : source.status >= 500 && target.status === 200
          ? "source-error"
          : source.status === 200 && target.status === 200 && mismatchPaths.length === 0
            ? "pass"
            : "fail",
  };
  if (caseId === "system.status" && source.status === 200 && target.status === 200) {
    const native = await getJson(targetUrl, targetKey, path, true);
    result.deploymentFactDifferences = native.status === 200
      ? classifySystemDeploymentDifferences(source.value, target.value, native.value) : [];
    const confirmed = new Set(result.deploymentFactDifferences.map((item) => item.path));
    if (result.result === "fail" && mismatchPaths.length > 0 && mismatchPaths.every((item) => confirmed.has(item))) {
      result.result = "expected-policy-difference";
      result.expectedPolicyDifference = "Only replacement deployment facts differ; each is confirmed by the native API. Original strict differences remain recorded.";
    }
  }
  if (caseId === "host-config.list" && source.value?.password === "<redacted-present>" &&
      target.value?.password === "" && mismatchPaths.length === 1 && mismatchPaths[0] === "/password") {
    result.pendingPasswordSecurityDifference = true;
  }
  results.push(result);
  return { source, target };
}

async function compareMutation(caseId, method, path, body) {
  const [source, target] = await Promise.all([
    requestJson(sourceUrl, sourceKey, method, path, body),
    requestJson(targetUrl, targetKey, method, path, body),
  ]);
  const comparison =
    source.status === target.status &&
    source.error === null &&
    target.error === null
      ? compareResponses(source.value, target.value, { includeDebugValues })
      : {
          mismatchPaths: ["/response"],
          mismatchDetails: [
            {
              path: "/response",
              kind: "response-status-or-body-difference",
              sourceStatus: source.status,
              targetStatus: target.status,
              sourceError: source.error,
              targetError: target.error,
            },
          ],
          additivePaths: [],
          additiveDetails: [],
        };
  results.push({
    caseId,
    method,
    path,
    sourceStatus: source.status,
    targetStatus: target.status,
    sourceError: source.error,
    targetError: target.error,
    sourceSha256: source.value === null ? null : sha256(source.value),
    targetSha256: target.value === null ? null : sha256(target.value),
    mismatchPaths: comparison.mismatchPaths,
    mismatchDetails: comparison.mismatchDetails,
    additivePaths: comparison.additivePaths,
    additiveDetails: comparison.additiveDetails,
    providerPresentationDifferenceCount: 0,
    providerPresentationDifferencePaths: [],
    result:
      source.status > 0 &&
      source.status === target.status &&
      source.error === null &&
      target.error === null &&
      comparison.mismatchPaths.length === 0
        ? "pass"
        : "fail",
  });
}

const cases = [
  ["authors.list", "/author"],
  ["books.list", "/book"],
  ["books.editions.mixed", "/book/30/editions/catalog"],
  ["books.editions.audiobook", "/book/31/editions/catalog"],
  ["books.editions.ebook", "/book/32/editions/catalog"],
  ["books.editions.empty", "/book/33/editions/catalog"],
  ["books.editions.missing", "/book/999999/editions/catalog"],
  ["series.list", "/series"],
  ["history.list", "/history"],
  ["root-folders.list", "/rootFolder"],
  ["quality-profiles.list", "/qualityProfile"],
  ["metadata-profiles.list", "/metadataProfile"],
  ["indexers.list", "/indexer", true],
  ["download-clients.list", "/downloadClient", true],
  ["notifications.list", "/notification", true],
  // Read-only source/target API sweep. These endpoints use the isolated
  // schema-158 fixture and do not perform external-provider, filesystem, or
  // command side effects. Keep binary downloads, live logs, update checks,
  // and provider-backed searches in separately controlled acceptance tests.
  ["blocklist.page", "/blocklist?page=1&pageSize=25"],
  ["books.paged", "/book/paged?offset=0&pageSize=25"],
  ["books.paged.first", "/book/paged?offset=0&pageSize=1"],
  ["books.paged.second", "/book/paged?offset=1&pageSize=1"],
  ["books.paged.unaligned", "/book/paged?offset=1&pageSize=2"],
  ["calendar.list", "/calendar"],
  ["custom-filters.list", "/customfilter"],
  ["custom-formats.list", "/customformat"],
  ["custom-formats.schema", "/customformat/schema", true],
  ["delay-profiles.list", "/delayprofile"],
  ["development-config.list", "/config/development"],
  ["download-clients.schema", "/downloadclient/schema", true],
  ["download-client-config.list", "/config/downloadclient", true],
  ["history.page", "/history?page=1&pageSize=25"],
  ["host-config.list", "/config/host"],
  ["import-lists.list", "/importlist", true],
  ["import-lists.schema", "/importlist/schema", true],
  ["import-list-exclusions.list", "/importlistexclusion"],
  ["indexers.schema", "/indexer/schema", true],
  ["indexer-config.list", "/config/indexer", true],
  ["indexer-flags.list", "/indexerflag"],
  ["languages.list", "/language"],
  ["localization.list", "/localization"],
  ["media-management-config.list", "/config/mediamanagement"],
  ["metadata-providers.list", "/metadata", true],
  ["metadata-providers.schema", "/metadata/schema", true],
  ["metadata-provider-config.list", "/config/metadataprovider", true],
  ["metadata-profiles.schema", "/metadataprofile/schema"],
  ["naming-config.list", "/config/naming"],
  ["notification.schema", "/notification/schema", true],
  ["quality-definitions.list", "/qualitydefinition"],
  ["quality-profiles.schema", "/qualityprofile/schema"],
  ["queue.status", "/queue/status"],
  ["release-profiles.list", "/releaseprofile"],
  ["remote-path-mappings.list", "/remotepathmapping"],
  ["wanted.missing.page", "/wanted/missing?page=1&pageSize=25"],
  ["wanted.cutoff.page", "/wanted/cutoff?page=1&pageSize=25"],
  ["system.status", "/system/status"],
  // The target deliberately hides these developer-only diagnostics in its
  // production build. Keep the behavior visible as an explicit policy
  // difference instead of treating it as an accidental endpoint regression.
  ["system.routes", "/system/routes", false, 404],
  ["system.routes.duplicate", "/system/routes/duplicate", false, 404],
  ["tags.list", "/tag"],
  ["tags.detail", "/tag/detail"],
  ["ui-config.list", "/config/ui"],
];

await Promise.all([
  waitForPing(sourceUrl, "BookshelfNG"),
  waitForPing(targetUrl, "ChaptarrNG"),
]);

await compareBasicAuthentication("authentication.legacy-password", "fixture-password", 200);
await compareBasicAuthentication("authentication.wrong-password", "incorrect-fixture-password", 401);
await compareBasicAuthentication("authentication.missing-credentials", null, 401);
await compareBasicAuthentication("authentication.api-requires-key", "fixture-password", 401, "/system/status");

for (const [caseId, path, providerResource = false, expectedTargetStatus = null] of cases) {
  const { source, target } = await compare(caseId, path, providerResource, expectedTargetStatus);
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

await verifyHostConfigReadback();
const passwordFlowVerified = hasVerifiedHostSettingsAuthentication(results);
for (const result of results) {
  if (result.pendingPasswordSecurityDifference && passwordFlowVerified) {
    result.result = "expected-policy-difference";
    result.expectedPolicyDifference = "Stored password hashes are write-only. The unchanged GET/PUT flow and original-password/wrong-password authentication are verified after saving.";
  }
  delete result.pendingPasswordSecurityDifference;
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

// Exercise legacy write contracts against the two isolated copies. The
// monitoring edit verifies the old work ID still addresses its migrated
// audiobook, and the delete verifies one old mixed-format ID removes both
// target rows that represent the same legacy work.
await compareMutation("book.monitor.disable", "PUT", "/book/editor", {
  bookIds: [31],
  monitored: false,
});
const monitoredReadback = await compare("book.monitor.disable.readback", "/book/31");
assert.equal(
  monitoredReadback.source.value?.monitored,
  false,
  "BookshelfNG must persist the legacy monitoring edit.",
);
assert.equal(
  monitoredReadback.target.value?.monitored,
  false,
  "ChaptarrNG must persist the legacy monitoring edit.",
);
await compareMutation("author.monitor.disable", "PUT", "/author/editor", {
  authorIds: [21],
  monitored: false,
});
const monitoredAuthorReadback = await compare(
  "author.monitor.disable.readback",
  "/author/21",
);
assert.equal(
  monitoredAuthorReadback.source.value?.monitored,
  false,
  "BookshelfNG must persist the legacy author monitoring edit.",
);
assert.equal(
  monitoredAuthorReadback.target.value?.monitored,
  false,
  "ChaptarrNG must persist the legacy author monitoring edit.",
);
await compareMutation("book.delete.mixed-format", "DELETE", "/book/editor", {
  bookIds: [34],
  deleteFiles: false,
  addImportListExclusion: false,
});
const deletedReadback = await compare("book.delete.mixed-format.readback", "/book");
assert.ok(
  Array.isArray(deletedReadback.source.value),
  "BookshelfNG book list must remain readable after deletion.",
);
assert.ok(
  Array.isArray(deletedReadback.target.value),
  "ChaptarrNG book list must remain readable after deletion.",
);
assert.equal(
  deletedReadback.source.value.some((book) => book.id === 34),
  false,
  "BookshelfNG must delete the requested legacy work.",
);
assert.equal(
  deletedReadback.target.value.some((book) => book.id === 34),
  false,
  "ChaptarrNG must delete every target row mapped to the requested legacy work.",
);

const report = {
  schemaVersion: 5,
  suite: "bookshelfng-api-differential",
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
    directDownloadUrls: "ordered legacy tag arrays remain strict; the target compatibility API must project the same array shape",
    secretFields: "compared as empty or present, never recorded; host hash concealment requires successful settings readback and authentication proofs",
    deploymentFacts: "explicit typed status facts independently confirmed through the native API; all strict differences remain recorded",
  },
  debugValues: includeDebugValues,
  caseCount: results.length,
  mutationCaseCount: results.filter((result) => result.method !== "GET").length,
  passed: results.filter((result) => result.result === "pass").length,
  failed: results.filter((result) => result.result === "fail").length,
  inconclusive: results.filter((result) => result.result === "source-error").length,
  expectedPolicyDifferenceCount: results.filter((result) => result.result === "expected-policy-difference").length,
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
      mutationCaseCount: report.mutationCaseCount,
      passed: report.passed,
      failed: report.failed,
      inconclusive: report.inconclusive,
      expectedPolicyDifferenceCount: report.expectedPolicyDifferenceCount,
      providerPresentationDifferenceCount: report.providerPresentationDifferenceCount,
      reportPath,
    },
    null,
    2,
  ),
);
console.log(
  report.failed !== 0
    ? "RESULT=FAIL"
    : report.inconclusive !== 0
      ? "RESULT=PASS_WITH_SOURCE_ERRORS"
      : report.expectedPolicyDifferenceCount !== 0
        ? "RESULT=PASS_WITH_EXPECTED_POLICY_DIFFERENCES"
        : "RESULT=PASS",
);
if (report.failed !== 0) process.exitCode = 1;
