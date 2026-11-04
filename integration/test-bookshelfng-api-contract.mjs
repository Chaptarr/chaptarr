import assert from "node:assert/strict";
import {
  compareResponses,
  normalizeProviderApiResponse,
  sanitizeApiResponse,
} from "./bookshelfng-api-contract.mjs";

const additiveFields = compareResponses(
  { id: 1, title: "The Book" },
  { id: 1, title: "The Book", mediaType: "audiobook" },
);
assert.deepEqual(additiveFields.mismatchPaths, []);
assert.deepEqual(additiveFields.additivePaths, ["/mediaType"]);

const missingSourceField = compareResponses(
  { id: 1, foreignBookId: "100" },
  { id: 1 },
);
assert.deepEqual(missingSourceField.mismatchPaths, ["/foreignBookId"]);

const matchedFields = compareResponses(
  {
    fields: [
      { name: "username", type: "textbox", value: "reader" },
      { name: "password", type: "password", value: "<redacted-present>" },
    ],
  },
  {
    fields: [
      { name: "apiKey", type: "password", value: "<redacted-present>" },
      { name: "password", type: "password", value: "<redacted-present>" },
      { name: "username", type: "textbox", value: "reader" },
    ],
  },
);
assert.deepEqual(matchedFields.mismatchPaths, []);
assert.deepEqual(matchedFields.additivePaths, ["/fields/0"]);

const changedLegacyField = compareResponses(
  { fields: [{ name: "searchType", type: "select", value: 2 }] },
  { fields: [{ name: "searchType", type: "select", value: 1 }] },
);
assert.deepEqual(changedLegacyField.mismatchPaths, ["/fields/0/value"]);

const targetCollectionExtension = compareResponses(
  [{ id: 1 }, { id: 2 }],
  [{ id: 1 }, { id: 2 }, { id: 3 }],
);
assert.deepEqual(targetCollectionExtension.mismatchPaths, []);
assert.deepEqual(targetCollectionExtension.additivePaths, ["/2"]);

const missingCollectionItem = compareResponses(
  [{ id: 1 }, { id: 2 }],
  [{ id: 1 }],
);
assert.deepEqual(missingCollectionItem.mismatchPaths, ["/id:2"]);

const reorderedLegacyCollection = compareResponses(
  [{ id: 1 }, { id: 2 }],
  [{ id: 2 }, { id: 1 }],
);
assert.deepEqual(reorderedLegacyCollection.mismatchPaths, ["/order"]);

const reorderedProviderSchemas = compareResponses(
  [
    { implementation: "LegacyIndexer", fields: [{ name: "apiKey", value: "fixture" }] },
    { implementation: "LegacyDownloadClient", fields: [{ name: "host", value: "localhost" }] },
  ],
  [
    { implementation: "LegacyDownloadClient", fields: [{ name: "host", value: "localhost" }] },
    { implementation: "LegacyIndexer", fields: [{ name: "apiKey", value: "fixture" }] },
    { implementation: "ChaptarrOnly", fields: [] },
  ],
);
assert.deepEqual(reorderedProviderSchemas.mismatchPaths, []);
assert.deepEqual(reorderedProviderSchemas.additivePaths, ["/2"]);

assert.deepEqual(
  sanitizeApiResponse({
    mamId: "fixture-token",
    fields: [
      { name: "Mam Id", type: "textbox", value: "fixture-token" },
      { name: "username", type: "textbox", value: "reader" },
    ],
  }),
  {
    fields: [
      { name: "Mam Id", type: "textbox", value: "<redacted-present>" },
      { name: "username", type: "textbox", value: "reader" },
    ],
    mamId: "<redacted-present>",
  },
);

const sourceDirectDownload = [{
  id: 121,
  configContract: "DirectDownloadSettings",
  infoLink: "https://wiki.servarr.com/readarr/supported#directdownloadindexer",
  fields: [{
    name: "urls",
    type: "tag",
    order: 0,
    label: "URLs",
    helpText: "One URL per tag.",
    value: ["https://primary.example", "https://secondary.example"],
  }],
}];
const targetDirectDownload = [{
  id: 121,
  configContract: "DirectDownloadSettings",
  infoLink: "https://discord.gg/nqFGsGUug2",
  fields: [{
    name: "urls",
    type: "textArea",
    order: 0,
    label: "URLs",
    helpText: "One URL per line.",
    value: ["https://primary.example", "https://secondary.example"],
  }],
}];
assert.deepEqual(
  compareResponses(
    normalizeProviderApiResponse(sourceDirectDownload),
    normalizeProviderApiResponse(targetDirectDownload),
  ).mismatchPaths,
  [],
);
assert.deepEqual(
  compareResponses(
    normalizeProviderApiResponse(sourceDirectDownload),
    normalizeProviderApiResponse(targetDirectDownload.map((provider) => ({
      ...provider,
      fields: provider.fields.map((field) => ({
        ...field,
        value: "https://primary.example\nhttps://secondary.example",
      })),
    }))),
  ).mismatchPaths,
  ["/0/fields/0/value"],
);
assert.deepEqual(
  compareResponses(
    normalizeProviderApiResponse(sourceDirectDownload),
    normalizeProviderApiResponse(targetDirectDownload.map((provider) => ({
      ...provider,
      fields: provider.fields.map((field) => ({
        ...field,
        value: ["https://primary.example", "https://changed.example"],
      })),
    }))),
  ).mismatchPaths,
  ["/0/fields/0/value/1"],
);

console.log("BookshelfNG API contract comparator: 12 checks passed");
