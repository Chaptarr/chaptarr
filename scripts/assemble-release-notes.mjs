#!/usr/bin/env node

import fs from 'node:fs';
import process from 'node:process';

import {
  changedReleaseNoteFiles,
  formatCuratedNotes,
  injectCuratedNotes,
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

const previousTag = args.get('--previous-tag');
const head = args.get('--head');
const changelogPath = args.get('--changelog');
const outputPath = args.get('--output');

if (!previousTag || !head || !changelogPath || !outputPath) {
  console.error(
    'Usage: assemble-release-notes.mjs --previous-tag <tag> --head <ref> --changelog <file> --output <file>'
  );
  process.exit(2);
}

const entries = changedReleaseNoteFiles(previousTag, head);
const deleted = entries.filter((entry) => entry.status === 'D');
const modifiedShipped = entries.filter(
  (entry) => entry.status !== 'A' && isShippedReleaseNoteChanged(entry.file, head)
);
const updatedUnshipped = entries
  .filter((entry) => entry.status === 'M' && !isReleaseNoteShipped(entry.file, head))
  .map((entry) => ({ ...entry, status: 'A' }));

if (deleted.length > 0 || modifiedShipped.length > 0) {
  console.error('Release-note fragments are append-only:');
  for (const entry of deleted) {
    console.error('- deleted: ' + entry.file);
  }
  for (const entry of modifiedShipped) {
    console.error('- modified shipped fragment: ' + entry.file);
  }
  process.exit(1);
}

const { notes, errors } = readReleaseNotes([
  ...entries.filter((entry) => entry.status === 'A'),
  ...updatedUnshipped,
]);
if (errors.length > 0) {
  console.error('Release-note validation failed:');
  for (const error of errors) {
    console.error('- ' + error);
  }
  process.exit(1);
}

const changelog = fs.readFileSync(changelogPath, 'utf8');
fs.writeFileSync(outputPath, injectCuratedNotes(changelog, formatCuratedNotes(notes)));
console.log('Assembled ' + notes.length + ' curated release-note fragment(s).');

