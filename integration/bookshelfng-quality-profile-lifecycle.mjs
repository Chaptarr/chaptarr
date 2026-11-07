import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { compareResponses, classifyMissingProfileDiagnosticRedaction } from './bookshelfng-api-contract.mjs';

// Run only against the two disposable services owned by the differential harness.
// The adapters return {status, value, error}; native GETs use the fourth argument.
export async function runQualityProfileLifecycle({ sourceRequest, targetRequest, onCase, afterCreate, afterUpdate, minimumNewProfileId = 1 }) {
  assert.ok(Number.isInteger(minimumNewProfileId) && minimumNewProfileId > 0);
  const name = 'Synthetic profile lifecycle ' + randomUUID();
  const sourceBefore = await sourceRequest('GET', '/qualityprofile');
  const targetBefore = await targetRequest('GET', '/qualityprofile');
  const nativeBefore = await targetRequest('GET', '/qualityprofile', undefined, true);
  assert.equal(sourceBefore.status, 200);
  assert.equal(targetBefore.status, 200);
  assert.equal(nativeBefore.status, 200);
  let sourceId;
  let targetId;

  const normalizeTarget = (value) => {
    const normalized = structuredClone(value);
    for (const profile of Array.isArray(normalized) ? normalized : [normalized]) {
      if (profile && profile.id === targetId) profile.id = sourceId;
    }
    return normalized;
  };

  function record(caseId, method, source, target, expectedStatus, normalize = true, missingProfileId) {
    const comparison = compareResponses(source.value, normalize ? normalizeTarget(target.value) : target.value);
    const policyDifferences = method === 'PUT' && caseId.startsWith('created.unexposed-native.') && caseId.endsWith('.update')
      ? classifyMissingProfileDiagnosticRedaction(source, target, missingProfileId) : [];
    const reservedIdentities = caseId !== 'create' ||
      (Number.isInteger(sourceId) && sourceId >= minimumNewProfileId && Number.isInteger(targetId) && targetId >= minimumNewProfileId);
    const passed = reservedIdentities && source.status === expectedStatus && target.status === expectedStatus &&
      source.error === null && target.error === null &&
      (comparison.mismatchPaths.length === 0 || policyDifferences.length > 0);
    onCase({
      caseId: 'quality-profile.lifecycle.' + caseId, method,
      sourceStatus: source.status, targetStatus: target.status,
      sourceError: source.error, targetError: target.error,
      mismatchPaths: comparison.mismatchPaths,
      additivePaths: comparison.additivePaths,
      providerPresentationDifferenceCount: 0,
      expectedPolicyDifferences: policyDifferences,
      generatedIdentityNormalization: normalize && sourceId && targetId ? { sourceId, targetId } : null,
      minimumNewProfileId: caseId === 'create' ? minimumNewProfileId : undefined,
      result: passed ? (policyDifferences.length ? 'expected-policy-difference' : 'pass') : 'fail'
    });
    assert.ok(passed, `${caseId}: source ${source.status}, target ${target.status}, mismatches: ${comparison.mismatchPaths.join(',') || 'none'}`);
  }

  async function paired(caseId, method, sourcePath, targetPath, expectedStatus, sourceBody, targetBody, missingProfileId) {
    if (missingProfileId !== undefined) {
      assert.equal(sourcePath, '/qualityprofile/' + missingProfileId);
      assert.equal(targetPath, sourcePath);
    }
    const [source, target] = await Promise.all([
      sourceRequest(method, sourcePath, sourceBody), targetRequest(method, targetPath, targetBody)
    ]);
    record(caseId, method, source, target, expectedStatus, true, missingProfileId);
    return { source, target };
  }

  try {
    const schema = await sourceRequest('GET', '/qualityprofile/schema');
    assert.equal(schema.status, 200);
    const payload = structuredClone(Array.isArray(schema.value) ? schema.value[0] : schema.value);
    payload.id = 0;
    payload.name = name;
    payload.cutoff = 12; // BookshelfNG .76 M4B, using its own complete schema order.
    for (const item of payload.items) item.allowed = [3, 10, 12].includes(item.quality?.id);
    const [sourceCreated, targetCreated] = await Promise.all([
      sourceRequest('POST', '/qualityprofile', payload), targetRequest('POST', '/qualityprofile', payload)
    ]);
    sourceId = sourceCreated.value?.id;
    targetId = targetCreated.value?.id;
    record('create', 'POST', sourceCreated, targetCreated, 201);
    assert.ok(Number.isInteger(sourceId) && sourceId > 0);
    assert.ok(Number.isInteger(targetId) && targetId > 0);
    assert.ok(!sourceBefore.value.some((profile) => profile.id === sourceId), 'Source reused an existing profile ID');
    assert.ok(!targetBefore.value.some((profile) => profile.id === targetId), 'Target reused an existing wire profile ID');
    await paired('created.readback', 'GET', '/qualityprofile/' + sourceId, '/qualityprofile/' + targetId, 200);
    await paired('created.list', 'GET', '/qualityprofile', '/qualityprofile', 200);

    const nativeCreated = await targetRequest('GET', '/qualityprofile', undefined, true);
    const previousIds = new Set(nativeBefore.value.map((profile) => profile.id));
    const added = nativeCreated.value.filter((profile) => !previousIds.has(profile.id));
    assert.equal(added.length, 2, 'A generic profile requires exactly two native counterparts');
    assert.ok(added.some((profile) => ['audiobook', '1'].includes(String(profile.profileType).toLowerCase())));
    assert.ok(added.some((profile) => ['ebook', '2'].includes(String(profile.profileType).toLowerCase())));
    for (const counterpart of added) {
      // A hidden native ID is not a second legacy address for this profile.
      // Skip a numeric collision with a real source ID in an independently
      // advanced fixture; the fresh differential fixture exercises both IDs.
      const hiddenId = counterpart.id;
      if (hiddenId === sourceId || hiddenId === targetId || sourceBefore.value.some((profile) => profile.id === hiddenId)) continue;
      const media = ['audiobook', '1'].includes(String(counterpart.profileType).toLowerCase()) ? 'audiobook' : 'ebook';
      await paired('created.unexposed-native.' + media + '.readback', 'GET', '/qualityprofile/' + hiddenId, '/qualityprofile/' + hiddenId, 404);
      const missing = { ...payload, id: hiddenId, name: name + ' absent ' + media };
      await paired('created.unexposed-native.' + media + '.update', 'PUT', '/qualityprofile/' + hiddenId, '/qualityprofile/' + hiddenId, 404, missing, missing, hiddenId);
      await paired('created.unexposed-native.' + media + '.delete', 'DELETE', '/qualityprofile/' + hiddenId, '/qualityprofile/' + hiddenId, 200);
    }
    const nativeAfterMissingRequests = await targetRequest('GET', '/qualityprofile', undefined, true);
    assert.equal(nativeAfterMissingRequests.status, 200);
    assert.deepEqual(nativeAfterMissingRequests.value, nativeCreated.value, 'Missing legacy IDs changed native profile rows');
    if (afterCreate) {
      await afterCreate({ sourceId, targetId, nativeProfiles: added });
      await paired('restart.readback', 'GET', '/qualityprofile/' + sourceId, '/qualityprofile/' + targetId, 200);
      const restarted = await targetRequest('GET', '/qualityprofile', undefined, true);
      assert.deepEqual(restarted.value.filter((profile) => added.some((item) => item.id === profile.id)).map((profile) => profile.id),
        added.map((profile) => profile.id), 'Native profile identities changed after restart');
    }

    payload.name += ' updated';
    payload.upgradeAllowed = true;
    payload.cutoff = 3;
    payload.items.find((item) => item.quality?.id === 1).allowed = true;
    await paired('update.mixed-qualities', 'PUT', '/qualityprofile/' + sourceId, '/qualityprofile/' + targetId, 202,
      { ...payload, id: sourceId }, { ...payload, id: targetId });
    await paired('updated.readback', 'GET', '/qualityprofile/' + sourceId, '/qualityprofile/' + targetId, 200);
    const nativeUpdated = await targetRequest('GET', '/qualityprofile', undefined, true);
    const updatedCounterparts = nativeUpdated.value.filter((profile) => added.some((item) => item.id === profile.id));
    assert.deepEqual(updatedCounterparts.map((profile) => profile.id), added.map((profile) => profile.id), 'Native identities changed during the shared update');
    for (const counterpart of updatedCounterparts) {
      assert.equal(counterpart.name, payload.name);
      assert.equal(counterpart.cutoff, payload.cutoff);
      assert.equal(counterpart.items.find((item) => item.quality?.id === 1)?.allowed, true);
    }

    if (afterUpdate) await afterUpdate({ sourceId, targetId, nativeProfiles: updatedCounterparts });
    await paired('delete', 'DELETE', '/qualityprofile/' + sourceId, '/qualityprofile/' + targetId, 200);
    await paired('deleted.readback', 'GET', '/qualityprofile/' + sourceId, '/qualityprofile/' + targetId, 404);
    await paired('delete.again', 'DELETE', '/qualityprofile/' + sourceId, '/qualityprofile/' + targetId, 200);
    sourceId = undefined;
    targetId = undefined;

    const [sourceAfter, targetAfter, nativeAfter] = await Promise.all([
      sourceRequest('GET', '/qualityprofile'), targetRequest('GET', '/qualityprofile'),
      targetRequest('GET', '/qualityprofile', undefined, true)
    ]);
    assert.deepEqual(sourceAfter.value, sourceBefore.value, 'Source profile rows changed after cleanup');
    assert.deepEqual(targetAfter.value, targetBefore.value, 'Legacy profile rows changed after cleanup');
    assert.deepEqual(nativeAfter.value, nativeBefore.value, 'Native counterparts or original profiles changed after cleanup');
    record('cleanup.list', 'GET', sourceAfter, targetAfter, 200, false);
  } finally {
    // Only the IDs returned by this run may be cleaned up. Failures retain their
    // database/HTTP evidence through the owning differential harness.
    const cleanup = [];
    if (Number.isInteger(sourceId) && sourceId > 0) cleanup.push(sourceRequest('DELETE', '/qualityprofile/' + sourceId));
    if (Number.isInteger(targetId) && targetId > 0) cleanup.push(targetRequest('DELETE', '/qualityprofile/' + targetId));
    await Promise.allSettled(cleanup);
  }
}
