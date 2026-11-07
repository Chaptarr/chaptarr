#!/usr/bin/env node

import process from 'node:process';

import {
  changedReleaseNoteFiles,
  formatCuratedNotes,
  isReleaseNoteShipped,
  isShippedReleaseNoteChanged,
  readReleaseNotes,
} from './release-notes.mjs';

const args = new Map();
for (let index = 2; index < process.argv.length; index += 1) {
  const argument = process.argv[index];
  if (!argument.startsWith('--')) {
    continue;
  }
  args.set(argument, process.argv[index + 1]);
  index += 1;
}

const base = args.get('--base');
const head = args.get('--head');

if (!base || !head) {
  console.error('Usage: preview-release-notes.mjs --base <ref> --head <ref>');
  process.exit(2);
}

const entries = changedReleaseNoteFiles(base, head);
const deleted = entries.filter((entry) => entry.status === 'D');
const modified = entries.filter(
  (entry) => entry.status !== 'A' && isShippedReleaseNoteChanged(entry.file, head)
);
const updatedUnshipped = entries
  .filter((entry) => entry.status === 'M' && !isReleaseNoteShipped(entry.file, head))
  .map((entry) => ({ ...entry, status: 'A' }));
const { notes, errors } = readReleaseNotes([
  ...entries.filter((entry) => entry.status === 'A'),
  ...updatedUnshipped,
]);

if (deleted.length > 0 || modified.length > 0 || errors.length > 0) {
  console.error('Release-note preview failed validation:');
  for (const entry of deleted) {
    console.error('- ' + entry.file + ': fragments are append-only; do not delete a fragment');
  }
  for (const entry of modified) {
    console.error('- ' + entry.file + ': fragments are append-only; add a new fragment instead');
  }
  for (const error of errors) {
    console.error('- ' + error);
  }
  process.exit(1);
}

if (notes.length === 0) {
  console.log('No new release-note fragments were found in this range.');
} else {
  console.log(formatCuratedNotes(notes));
}

