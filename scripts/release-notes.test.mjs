import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';

import { hasExplicitNoReleaseNote } from './release-notes.mjs';

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
