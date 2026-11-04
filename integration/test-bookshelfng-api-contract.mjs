import assert from "node:assert/strict";
import {
  compareResponses,
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

console.log("BookshelfNG API contract comparator: 8 checks passed");
