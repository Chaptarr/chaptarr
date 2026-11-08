import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';

import {
  hasExplicitNoReleaseNote,
  prepareReleaseBodies,
} from './release-notes.mjs';

const pullRequestTemplate = fs.readFileSync(
  new URL('../.github/pull_request_template.md', import.meta.url),
  'utf8'
);

test('the default unchecked internal-only option is not an opt-out', () => {
  assert.equal(hasExplicitNoReleaseNote(pullRequestTemplate), false);
});

test('the checked internal-only option is an explicit opt-out', () => {
  const checkedTemplate = pullRequestTemplate.replace(
    '- [ ] This change is internal-only',
    '- [x] This change is internal-only'
  );

  assert.equal(hasExplicitNoReleaseNote(checkedTemplate), true);
});

test('a standalone release-note trailer is accepted', () => {
  assert.equal(hasExplicitNoReleaseNote('release-note: none'), true);
});

test('the undocumented release-note:none spelling is rejected', () => {
  assert.equal(hasExplicitNoReleaseNote('release-note:none'), false);
});

test('release-note markers inside HTML comments are ignored', () => {
  assert.equal(hasExplicitNoReleaseNote('<!-- release-note: none -->'), false);
  assert.equal(hasExplicitNoReleaseNote('<!-- release-note: none'), false);
});

test('release-note markers inside fenced code examples are ignored', () => {
  assert.equal(hasExplicitNoReleaseNote('```text\nrelease-note: none\n```'), false);
  assert.equal(hasExplicitNoReleaseNote('~~~\nrelease-note: none\n~~~'), false);
});

test('the CodeQL internal-note marker remains supported', () => {
  assert.equal(hasExplicitNoReleaseNote('release-note-none-codeql-action'), true);
});

test('release bodies below the limit retain the complete changelog', () => {
  const changelog = '# Changelog\n\n## 0.9.978\n\n- Fixed one issue.\n';
  const bodies = prepareReleaseBodies(changelog, '### User-facing changes\n\n- One fix.', {
    maxLength: 500,
    overflowAssetName: 'chaptarrng-v0.9.978-full-changelog.md',
  });

  assert.equal(bodies.overflowed, false);
  assert.equal(bodies.releaseBody, bodies.fullBody);
  assert.match(bodies.releaseBody, /One fix/u);
  assert.match(bodies.releaseBody, /Fixed one issue/u);
});

test('oversized release bodies retain curated notes and attach the complete changelog', () => {
  const changelog = '# Changelog\n\n## 0.9.978\n\n' + '- Commit detail.\n'.repeat(100);
  const bodies = prepareReleaseBodies(
    changelog,
    '### User-facing changes\n\n- Migration and rollback are ready.',
    {
      maxLength: 300,
      overflowAssetName: 'chaptarrng-v0.9.978-full-changelog.md',
    }
  );

  assert.equal(bodies.overflowed, true);
  assert.ok(bodies.releaseBody.length <= 300);
  assert.match(bodies.releaseBody, /Migration and rollback are ready/u);
  assert.match(bodies.releaseBody, /chaptarrng-v0\.9\.978-full-changelog\.md/u);
  assert.match(bodies.fullBody, /Commit detail/u);
  assert.ok(bodies.fullBody.length > bodies.releaseBody.length);
});

test('oversized curated notes fail instead of being silently truncated', () => {
  assert.throws(
    () =>
      prepareReleaseBodies('# Changelog\n\n## 0.9.978\n\n' + 'x'.repeat(500), 'y'.repeat(500), {
        maxLength: 200,
        overflowAssetName: 'chaptarrng-v0.9.978-full-changelog.md',
      }),
    /Curated release notes exceed/u
  );
});
