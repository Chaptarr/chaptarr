import assert from "node:assert/strict";
import {
  annotateProviderMismatchDetails,
  compareResponses,
  classifySystemDeploymentDifferences,
  hasVerifiedHostSettingsAuthentication,
  normalizeProviderApiResponse,
  sanitizeApiResponse,
} from "./bookshelfng-api-contract.mjs";

const providers = Array.from({ length: 20 }, (_, index) => ({
  implementation: `Provider${index}`,
}));
providers[19].implementation = "LegacyNotification";
assert.deepEqual(
  annotateProviderMismatchDetails(
    [
      { path: "/19/supportsOnBookFileDelete", kind: "value-difference" },
      { path: "/appName", kind: "value-difference" },
    ],
    providers,
  ),
  [
    {
      path: "/19/supportsOnBookFileDelete",
      kind: "value-difference",
      providerImplementation: "LegacyNotification",
    },
    { path: "/appName", kind: "value-difference" },
  ],
);

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

const preset = (name, url) => ({
  name,
  implementation: "Newznab",
  fields: [{ name: "baseUrl", value: url }],
});
const legacyPresets = { presets: [preset("A", "https://a.example"), preset("B", "https://b.example")] };
const extendedPresets = { presets: [preset("New", "https://new.example"), ...legacyPresets.presets] };
assert.deepEqual(compareResponses(legacyPresets, extendedPresets).mismatchPaths, []);
assert.deepEqual(compareResponses(legacyPresets, extendedPresets).additivePaths, ["/presets/0"]);
assert.deepEqual(
  compareResponses(legacyPresets, { presets: [preset("A", "https://changed.example"), legacyPresets.presets[1]] }).mismatchPaths,
  ["/presets/0/fields/0/value"],
);
assert.deepEqual(
  compareResponses(legacyPresets, { presets: [legacyPresets.presets[1]] }).mismatchPaths,
  ["/presets/name:A"],
);

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

const customFormatSource = [{ implementation: "IndexerFlagSpecification", fields: [{
  name: "value", value: 4, helpText: "Legacy help", selectOptions: [{ value: 4, name: "DoubleUpload", order: 4 }],
}] }];
const customFormatTarget = structuredClone(customFormatSource);
customFormatTarget[0].fields[0].helpText = "Localized help";
customFormatTarget[0].fields[0].selectOptions[0].name = "Double Upload";
customFormatTarget[0].fields[0].selectOptions[0].order = 114;
const customComparison = () => compareResponses(normalizeProviderApiResponse(customFormatSource), normalizeProviderApiResponse(customFormatTarget));
assert.deepEqual(customComparison().mismatchPaths, []);
customFormatTarget[0].fields[0].value = 8;
assert.deepEqual(customComparison().mismatchPaths, ["/0/fields/0/value"]);
customFormatTarget[0].fields[0].value = 4;
customFormatTarget[0].fields[0].selectOptions[0].value = 8;
assert.ok(customComparison().mismatchPaths.length > 0);
console.log("BookshelfNG API contract comparator: 18 checks passed");

const sourceStatusFacts = {
  appName: "Readarr", version: "0.4.21.76", migrationVersion: 158,
  buildTime: "2026-10-26T01:13:37Z", startTime: "2026-11-05T11:00:00Z",
  startupPath: "/app/readarr/bin", osName: "alpine", osVersion: "3.24.2", runtimeVersion: "10.0.13",
  packageAuthor: "pennydreadful", packageVersion: "f512a17f866d1fa8e5d92ae792d044de1fb45535",
  instanceName: "Readarr", authentication: "basic", urlBase: "",
};
const targetStatusFacts = {
  appName: "Chaptarr", version: "0.9.978.0", migrationVersion: 126,
  buildTime: "2026-11-05T11:13:36Z", startTime: "2026-11-05T11:14:00Z",
  startupPath: "/app", osName: "ubuntu", osVersion: "24.04", runtimeVersion: "10.0.14",
  instanceName: "Readarr", authentication: "basic", urlBase: "",
};
const nativeStatusFacts = { ...targetStatusFacts, instanceName: "Chaptarr" };
const classifiedFacts = classifySystemDeploymentDifferences(sourceStatusFacts, targetStatusFacts, nativeStatusFacts);
assert.equal(classifiedFacts.length, 11);
assert.deepEqual(new Set(classifiedFacts.map((item) => item.path)), new Set(compareResponses(sourceStatusFacts, targetStatusFacts).mismatchPaths));
for (const [key, value] of [["instanceName", "Changed"], ["authentication", "none"], ["urlBase", "/different"]]) {
  const changed = { ...targetStatusFacts, [key]: value };
  assert.ok(compareResponses(sourceStatusFacts, changed).mismatchPaths.includes("/" + key));
  assert.ok(!classifySystemDeploymentDifferences(sourceStatusFacts, changed, nativeStatusFacts).some((item) => item.path === "/" + key));
}
assert.deepEqual(classifySystemDeploymentDifferences(sourceStatusFacts, targetStatusFacts, { ...nativeStatusFacts, appName: "Unexpected" }), []);
assert.deepEqual(classifySystemDeploymentDifferences(sourceStatusFacts, { ...targetStatusFacts, migrationVersion: 0 }, nativeStatusFacts), []);
for (const [key, value] of [["version", "unknown"], ["runtimeVersion", ""], ["buildTime", "not-a-date"], ["startupPath", "relative"], ["osName", ""], ["osVersion", "unavailable"]]) {
  const malformed = { ...targetStatusFacts, [key]: value };
  assert.ok(!classifySystemDeploymentDifferences(sourceStatusFacts, malformed, malformed).some((item) => item.path === "/" + key));
}
assert.ok(!classifySystemDeploymentDifferences(sourceStatusFacts, targetStatusFacts, { ...nativeStatusFacts, runtimeVersion: "10.0.15" }).some((item) => item.path === "/runtimeVersion"));
console.log("Deployment fact classifications preserve strict business fields and reject malformed or unconfirmed facts.");

const settingsProofs = [
  { caseId: "host-config.target-readback", result: "pass", sourceStatus: null, targetStatus: 202, passwordWriteOnly: true },
  { caseId: "authentication.legacy-password-after-settings-save", result: "pass", sourceStatus: 200, targetStatus: 200 },
  { caseId: "authentication.wrong-password-after-settings-save", result: "pass", sourceStatus: 401, targetStatus: 401 },
];
assert.equal(hasVerifiedHostSettingsAuthentication(settingsProofs), true);
for (let index = 0; index < settingsProofs.length; index++) {
  assert.equal(hasVerifiedHostSettingsAuthentication(settingsProofs.filter((_, i) => i !== index)), false);
  assert.equal(hasVerifiedHostSettingsAuthentication(settingsProofs.map((proof, i) => i === index ? { ...proof, result: "fail" } : proof)), false);
  assert.equal(hasVerifiedHostSettingsAuthentication(settingsProofs.map((proof, i) => i === index ? { ...proof, targetStatus: 400 } : proof)), false);
}
assert.equal(hasVerifiedHostSettingsAuthentication(settingsProofs.map((proof, index) => index === 0 ? { ...proof, passwordWriteOnly: false } : proof)), false);
console.log("Host password concealment requires successful settings save and both authentication proofs.");
