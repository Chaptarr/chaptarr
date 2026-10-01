#!/usr/bin/env node

import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

export const RELEASE_NOTE_DIRECTORY = 'release-notes';

const CATEGORIES = new Map([
  ['added', 'Added'],
  ['changed', 'Changed'],
  ['fixed', 'Fixed'],
  ['security', 'Security'],
  ['removed', 'Removed'],
  ['deprecated', 'Deprecated'],
]);
const AUDIENCES = new Set(['users', 'operators']);
const FRONTMATTER_KEYS = new Set([
  'category',
  'audience',
  'area',
  'action',
  'breaking',
]);

export const isReleaseNoteFile = (file) =>
  file.startsWith(RELEASE_NOTE_DIRECTORY + '/') &&
  file.endsWith('.md') &&
  path.posix.dirname(file) === RELEASE_NOTE_DIRECTORY &&
  path.posix.basename(file).toLowerCase() !== 'readme.md';

export function parseReleaseNote(file, content) {
  const errors = [];
  const normalized = content.replace(/\r\n?/gu, '\n');
  const frontmatter = normalized.match(/^---\n([\s\S]*?)\n---\n?([\s\S]*)$/u);

  if (!frontmatter) {
    return { file, errors: ['must contain YAML frontmatter delimited by ---'] };
  }

  const metadata = new Map();
  for (const line of frontmatter[1].split('\n')) {
    if (!line.trim()) {
      continue;
    }

    const match = line.match(/^([a-z][a-z-]*):\s*(\S.*)$/u);
    if (!match) {
      errors.push('has invalid frontmatter: ' + line);
      continue;
    }
    if (metadata.has(match[1])) {
      errors.push('declares frontmatter key "' + match[1] + '" more than once');
    }
    metadata.set(match[1], match[2].trim());
  }

  for (const key of metadata.keys()) {
    if (!FRONTMATTER_KEYS.has(key)) {
      errors.push('frontmatter key "' + key + '" is not supported');
    }
  }

  const category = metadata.get('category')?.toLowerCase();
  if (!category || !CATEGORIES.has(category)) {
    errors.push('category must be one of: ' + [...CATEGORIES.keys()].join(', '));
  }

  const audienceParts = (metadata.get('audience') ?? '')
    .toLowerCase()
    .split(',')
    .map((part) => part.trim())
    .filter(Boolean);
  const invalidAudiences = audienceParts.filter((audience) => !AUDIENCES.has(audience));
  if (
    audienceParts.length === 0 ||
    invalidAudiences.length > 0 ||
    new Set(audienceParts).size !== audienceParts.length
  ) {
    errors.push('audience must list users, operators, or both exactly once');
  }

  const area = metadata.get('area')?.toLowerCase();
  if (!area || !/^[a-z][a-z0-9-]{1,31}$/u.test(area)) {
    errors.push('area must be a 2-32 character lowercase slug such as distribution or release-pipeline');
  }

  const action = metadata.get('action')?.trim().replace(/\s+/gu, ' ');
  if (!action) {
    errors.push('action is required; use none when no user or operator action is needed');
  } else if (action.toLowerCase() !== 'none') {
    if (action.length < 5 || action.length > 200) {
      errors.push('action must be 5-200 characters or exactly none');
    }
    if (/(?:<!--|--!?>|\b(?:todo|tbd|fill in)\b)/iu.test(action)) {
      errors.push('action contains a placeholder or HTML comment');
    }
  }

  const breaking = metadata.get('breaking')?.toLowerCase();
  if (breaking !== 'true' && breaking !== 'false') {
    errors.push('breaking must be either true or false');
  }
  if (breaking === 'true' && action?.toLowerCase() === 'none') {
    errors.push('breaking changes must describe an upgrade or operator action');
  }

  const body = frontmatter[2].trim().replace(/\s+/gu, ' ');
  if (body.length < 30 || body.length > 400) {
    errors.push('body must be 30-400 characters and describe the user impact');
  }
  if (/(?:<!--|--!?>|\b(?:todo|tbd|fill in)\b)/iu.test(body)) {
    errors.push('body contains a placeholder or HTML comment');
  }
  if (body && !/^[A-Z0-9*_]/u.test(body)) {
    errors.push('body must start with a capitalized sentence');
  }
  if (body && !/[.!?)]$/u.test(body)) {
    errors.push('body must end with sentence punctuation');
  }

  return {
    file,
    category,
    categoryTitle: CATEGORIES.get(category),
    audience: audienceParts,
    area,
    action: action?.toLowerCase() === 'none' ? 'none' : action,
    breaking: breaking === 'true',
    body,
    errors,
  };
}

export function changedReleaseNoteFiles(base, head, cwd = process.cwd()) {
  const output = execFileSync(
    'git',
    ['diff', '--name-status', '--find-renames', base, head, '--', RELEASE_NOTE_DIRECTORY],
    { cwd, encoding: 'utf8' }
  );

  return output
    .split('\n')
    .filter(Boolean)
    .map((line) => {
      const columns = line.split('\t');
      const status = columns[0] ?? '';
      const file = status.startsWith('R') ? columns[2] : columns[1];
      return { status: status.charAt(0), file };
    })
    .filter(({ file }) => file && isReleaseNoteFile(file));
}

export function latestReleaseTag(head, cwd = process.cwd()) {
  try {
    return execFileSync(
      'git',
      ['describe', '--tags', '--abbrev=0', '--match', 'v0.*', head],
      { cwd, encoding: 'utf8' }
    ).trim();
  } catch {
    return null;
  }
}

export function isReleaseNoteShipped(file, head, cwd = process.cwd()) {
  const tag = latestReleaseTag(head, cwd);
  if (!tag) {
    return false;
  }

  try {
    execFileSync('git', ['cat-file', '-e', tag + ':' + file], {
      cwd,
      stdio: 'ignore',
    });
    return true;
  } catch {
    return false;
  }
}

export function isShippedReleaseNoteChanged(file, head, cwd = process.cwd()) {
  const tag = latestReleaseTag(head, cwd);
  if (!tag) {
    return false;
  }

  let shippedContent;
  try {
    shippedContent = execFileSync('git', ['show', tag + ':' + file], {
      cwd,
      encoding: 'utf8',
    });
  } catch {
    return false;
  }

  try {
    const currentContent = execFileSync('git', ['show', head + ':' + file], {
      cwd,
      encoding: 'utf8',
    });
    return currentContent !== shippedContent;
  } catch {
    return true;
  }
}

export function readReleaseNotes(entries, cwd = process.cwd()) {
  const notes = [];
  const errors = [];

  for (const entry of entries) {
    const filePath = path.join(cwd, entry.file);
    let content;
    try {
      content = fs.readFileSync(filePath, 'utf8');
    } catch (error) {
      errors.push(entry.file + ': unable to read file (' + error.message + ')');
      continue;
    }

    const note = parseReleaseNote(entry.file, content);
    if (note.errors.length > 0) {
      errors.push(...note.errors.map((error) => entry.file + ': ' + error));
    } else {
      notes.push(note);
    }
  }

  return { notes, errors };
}

export function formatCuratedNotes(notes) {
  if (notes.length === 0) {
    return '';
  }

  const lines = ['### User-facing changes', ''];
  for (const [category, title] of CATEGORIES) {
    const categoryNotes = notes.filter((note) => note.category === category);
    if (categoryNotes.length === 0) {
      continue;
    }

    lines.push('#### ' + title, '');
    for (const note of categoryNotes) {
      const areaTitle = note.area
        .split('-')
        .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
        .join(' ');
      const breakingPrefix = note.breaking ? '**Breaking:** ' : '';
      lines.push('- **' + areaTitle + ':** ' + breakingPrefix + note.body);
      if (note.action && note.action !== 'none') {
        lines.push('  - **Action required:** ' + note.action);
      }
    }
    lines.push('');
  }

  return lines.join('\n').trim();
}

export function injectCuratedNotes(changelog, curatedNotes) {
  if (!curatedNotes) {
    return changelog;
  }

  const heading = /^## .+$/mu.exec(changelog);
  if (!heading || heading.index === undefined) {
    return curatedNotes + '\n\n' + changelog.trim() + '\n';
  }

  const insertionPoint = heading.index + heading[0].length;
  return (
    changelog.slice(0, insertionPoint) +
    '\n\n' +
    curatedNotes +
    '\n' +
    changelog.slice(insertionPoint).replace(/^\n*/u, '\n')
  );
}

export function hasExplicitNoReleaseNote(body) {
  return (
    /release-note\s*:\s*none/iu.test(body) ||
    /release-note-none-codeql-action/iu.test(body) ||
    /-\s*\[x\][^\n]*(?:internal-only|no user-facing release note)/iu.test(body)
  );
}

