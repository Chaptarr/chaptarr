#!/usr/bin/env node
/**
 * Scan frontend for translate('Key') calls and report any keys missing from en.json.
 *
 * Usage:
 *   yarn check-translations             # scan all .js/.jsx/.ts/.tsx in frontend/src
 *   yarn check-translations --diff      # scan only files in the current git diff
 *
 * Exits non-zero if any missing keys are found.
 *
 * Runs alongside the react/jsx-no-literals ESLint rule:
 *   - ESLint catches bare JSX text (translate-not-called).
 *   - This script catches translate-called-but-key-missing.
 */
const fs = require('fs');
const path = require('path');
const { execSync } = require('child_process');

const REPO_ROOT = path.resolve(__dirname, '..', '..');
const EN_JSON = path.join(REPO_ROOT, 'src', 'NzbDrone.Core', 'Localization', 'Core', 'en.json');
const FRONTEND_SRC = path.join(REPO_ROOT, 'frontend', 'src');
const EXTS = new Set(['.js', '.jsx', '.ts', '.tsx']);
const KEY_RE = /translate\(['"]([^'"]+)['"]/g;

function walk(dir) {
  const out = [];
  for (const name of fs.readdirSync(dir)) {
    const full = path.join(dir, name);
    const stat = fs.statSync(full);
    if (stat.isDirectory()) {
      out.push(...walk(full));
    } else if (EXTS.has(path.extname(full))) {
      out.push(full);
    }
  }
  return out;
}

function getDiffFiles() {
  const out = execSync('git diff --name-only frontend/src', { cwd: REPO_ROOT, encoding: 'utf8' });
  return out.split('\n')
    .filter((p) => p && EXTS.has(path.extname(p)))
    .map((p) => path.join(REPO_ROOT, p));
}

function main() {
  const diffOnly = process.argv.includes('--diff');
  const existing = new Set(Object.keys(JSON.parse(fs.readFileSync(EN_JSON, 'utf8'))));
  const files = diffOnly ? getDiffFiles() : walk(FRONTEND_SRC);

  const missing = new Map();
  for (const f of files) {
    if (!fs.existsSync(f)) {
      continue;
    }
    const text = fs.readFileSync(f, 'utf8');
    let m;
    KEY_RE.lastIndex = 0;
    while ((m = KEY_RE.exec(text)) !== null) {
      const key = m[1];
      if (existing.has(key)) {
        continue;
      }
      const rel = path.relative(REPO_ROOT, f);
      if (!missing.has(key)) {
        missing.set(key, []);
      }
      missing.get(key).push(rel);
    }
  }

  if (missing.size === 0) {
    console.log(`OK: all translate() keys exist in en.json (${files.length} files scanned)`);
    process.exit(0);
  }

  console.log(`MISSING ${missing.size} translation keys:`);
  for (const key of [...missing.keys()].sort()) {
    const refs = missing.get(key);
    console.log(`  ${key}`);
    for (const r of refs.slice(0, 3)) {
      console.log(`      ${r}`);
    }
    if (refs.length > 3) {
      console.log(`      ... and ${refs.length - 3} more`);
    }
  }
  process.exit(1);
}

main();
