const assert = require('assert');
const fs = require('fs');
const path = require('path');
const { URL } = require('url');

const root = path.resolve(__dirname, '..');
const repoRoot = path.resolve(root, '..');
const readme = fs.readFileSync(path.join(repoRoot, 'README.md'), 'utf8');
const addModal = fs.readFileSync(path.join(root, 'src/Settings/Indexers/Indexers/AddIndexerModalContent.js'), 'utf8');
const addConnector = fs.readFileSync(path.join(root, 'src/Settings/Indexers/Indexers/AddIndexerModalContentConnector.js'), 'utf8');
const formGroup = fs.readFileSync(path.join(root, 'src/Components/Form/ProviderFieldFormGroup.js'), 'utf8');
const editModal = fs.readFileSync(path.join(root, 'src/Settings/Indexers/Indexers/EditIndexerModalContent.js'), 'utf8');
const indexerCard = fs.readFileSync(path.join(root, 'src/Settings/Indexers/Indexers/Indexer.js'), 'utf8');
const indexerTyping = fs.readFileSync(path.join(root, 'src/typings/Indexer.ts'), 'utf8');
const testHandler = fs.readFileSync(path.join(root, 'src/Store/Actions/Creators/createTestProviderHandler.js'), 'utf8');
const provider = fs.readFileSync(path.join(repoRoot, 'src/NzbDrone.Core/Indexers/DirectDownload/DirectDownloadIndexer.cs'), 'utf8');
const openApi = fs.readFileSync(path.join(repoRoot, 'src/Chaptarr.Api.V1/openapi.json'), 'utf8');
const englishTranslations = fs.readFileSync(path.join(repoRoot, 'src/NzbDrone.Core/Localization/Core/en.json'), 'utf8');

assert.match(addModal, /usenetIndexers\.map/);
assert.match(addModal, /torrentIndexers\.map/);
assert.match(editModal, /fields\.map/);
assert.match(editModal, /name="protocolDisplay"/);
assert.match(editModal, /DOWNLOAD_CLIENT_SELECT/);
assert.match(editModal, /onTestPress/);
assert.match(editModal, /translate\('DirectDownload'\)/);
assert.match(indexerCard, /translate\('DirectDownload'\)/);

assert.match(addConnector, /protocol: 'direct'/);
assert.match(addModal, /directIndexers/);
assert.match(addModal, /DirectDownload/);
assert.match(formGroup, /case 'textArea'/);
assert.match(testHandler, /successMessages/);
assert.match(provider, /URL \{index \+ 1\}: configuration valid/);
const openApiDocument = JSON.parse(openApi);
const translationDocument = JSON.parse(englishTranslations);

assert.deepStrictEqual(openApiDocument.components.schemas.DownloadProtocol.enum, [
  'unknown',
  'usenet',
  'torrent',
  'direct'
]);
assert.strictEqual(translationDocument.DirectDownload, 'Direct Download');
assert.match(addModal, /translate\('DirectDownload'\)/);

const indexerResource = openApiDocument.components.schemas.IndexerResource;
assert.ok(indexerResource, 'OpenAPI must expose IndexerResource');
for (const property of [
  'enable',
  'supportsRss',
  'supportsSearch',
  'protocol',
  'priority',
  'downloadClientId',
  'proxyId',
  'fields',
  'message'
]) {
  assert.ok(indexerResource.properties[property], `IndexerResource is missing ${property}`);
}

for (const property of [
  'enable',
  'supportsRss',
  'supportsSearch',
  'downloadClientId',
  'proxyId',
  'message',
  'presets'
]) {
  assert.match(indexerTyping, new RegExp(`\\b${property}\\b`));
}
assert.match(indexerTyping, /'direct'/);

for (const heading of [
  '### Direct Download sources',
  '#### Configure the indexer',
  '#### Ordering, probing, and fallback',
  '#### Ebook-only limitation',
  '#### Staging, restart, and cleanup',
  '#### Security and operational requirements'
]) {
  assert.match(readme, new RegExp(heading.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));
}

for (const requiredPhrase of [
  'one absolute `http://` or `https://` URL per line',
  'The first occurrence remains in the configured order',
  'The API Key field is masked',
  'It does not contact the URLs',
  'up to three attempts',
  'On restart, Chaptarr reloads Direct state',
  'Only absolute HTTP and HTTPS URLs are accepted',
  'Direct Download accepts ebook searches only'
]) {
  assert.match(readme, new RegExp(requiredPhrase.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));
}

console.log('direct download API/UI contract checks passed');

async function requestJson(baseUrl, apiKey, endpoint, options = {}) {
  const response = await fetch(new URL(endpoint, baseUrl), {
    ...options,
    headers: {
      Accept: 'application/json',
      ...(apiKey ? { 'X-Api-Key': apiKey } : {}),
      ...(options.headers || {})
    },
    signal: AbortSignal.timeout(15000)
  });
  const body = await response.text();

  if (!response.ok) {
    throw new Error(`${options.method || 'GET'} ${endpoint} failed with ${response.status}`);
  }

  if (!body) {
    return null;
  }

  return JSON.parse(body);
}

function fieldValue(resource, fieldName) {
  return resource.fields?.find((field) => field.name === fieldName)?.value;
}

function setFieldValue(resource, fieldName, value) {
  const field = resource.fields?.find((candidate) => candidate.name === fieldName);
  assert.ok(field, `schema is missing ${fieldName}`);
  field.value = value;
}

async function runApiSmoke() {
  const baseUrl = process.env.DIRECT_DOWNLOAD_SMOKE_URL;
  if (!baseUrl) {
    console.log('direct download API smoke skipped (set DIRECT_DOWNLOAD_SMOKE_URL to run it)');
    return;
  }

  const apiKey = process.env.DIRECT_DOWNLOAD_SMOKE_API_KEY || '';
  let createdId = null;

  try {
    const schema = await requestJson(baseUrl, apiKey, '/api/v1/indexer/schema');
    const directSchema = schema.find((item) => item.protocol === 'direct');
    assert.ok(directSchema, 'schema must contain one direct indexer template');
    assert.strictEqual(schema.filter((item) => item.protocol === 'direct').length, 1);

    const createPayload = {
      ...directSchema,
      id: 0,
      name: `Direct Download smoke ${Date.now()}`,
      fields: directSchema.fields.map((field) => ({ ...field }))
    };
    setFieldValue(createPayload, 'urls', 'https://example.invalid/primary\nhttps://example.invalid/fallback');
    setFieldValue(createPayload, 'apiKey', 'contract-smoke-secret');

    const created = await requestJson(baseUrl, apiKey, '/api/v1/indexer', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(createPayload)
    });
    createdId = created.id;
    assert.ok(Number.isInteger(createdId));

    const reloaded = await requestJson(baseUrl, apiKey, `/api/v1/indexer/${createdId}`);
    assert.strictEqual(fieldValue(reloaded, 'urls'), 'https://example.invalid/primary\nhttps://example.invalid/fallback');
    assert.notStrictEqual(fieldValue(reloaded, 'apiKey'), 'contract-smoke-secret');

    const updatePayload = {
      ...reloaded,
      name: `${reloaded.name} updated`,
      fields: reloaded.fields.map((field) => ({ ...field }))
    };
    setFieldValue(updatePayload, 'urls', 'https://example.invalid/primary\nhttps://example.invalid/fallback');
    const maskedKey = fieldValue(updatePayload, 'apiKey');
    assert.ok(maskedKey === null || maskedKey === undefined || maskedKey !== 'contract-smoke-secret');

    await requestJson(baseUrl, apiKey, `/api/v1/indexer/${createdId}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(updatePayload)
    });

    const testResult = await requestJson(baseUrl, apiKey, '/api/v1/indexer/test?forceTest=true', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(updatePayload)
    });
    const testText = JSON.stringify(testResult || '');
    assert.ok(!testText.includes('contract-smoke-secret'));

    await requestJson(baseUrl, apiKey, `/api/v1/indexer/${createdId}`, { method: 'DELETE' });
    console.log('direct download API smoke passed (create, reload, masked update, test, delete)');
  } finally {
    if (createdId !== undefined) {
      await requestJson(baseUrl, apiKey, `/api/v1/indexer/${createdId}`, { method: 'DELETE' }).catch(() => undefined);
    }
  }
}

runApiSmoke().catch((error) => {
  console.error(`direct download API smoke failed: ${error.message}`);
  process.exitCode = 1;
});
