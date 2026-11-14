#!/usr/bin/env node
/**
 * Scan frontend translate('Key') and backend GetLocalizedString("Key") calls and report any keys missing from en.json.
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
const { parseSync, traverse } = require('@babel/core');

const REPO_ROOT = path.resolve(__dirname, '..', '..');
const EN_JSON = path.join(REPO_ROOT, 'src', 'NzbDrone.Core', 'Localization', 'Core', 'en.json');
const FRONTEND_SRC = path.join(REPO_ROOT, 'frontend', 'src');
const EXTS = new Set(['.js', '.jsx', '.ts', '.tsx', '.cs']);
const BACKEND_SRC = path.join(REPO_ROOT, 'src');
const CS_KEY_RE = /GetLocalizedString\(\s*"([^"]+)"/g;

function literalValues(node) {
  if (!node) return [];
  if (node.type === 'StringLiteral') return [node.value];
  if (node.type === 'ConditionalExpression') return [...literalValues(node.consequent), ...literalValues(node.alternate)];
  if (node.type === 'LogicalExpression') return [...literalValues(node.left), ...literalValues(node.right)];
  if (node.type === 'BinaryExpression' && node.operator === '+') {
    return literalValues(node.left).flatMap((left) => literalValues(node.right).map((right) => left + right));
  }
  if (node.type === 'TemplateLiteral') {
    return node.quasis.reduce((values, part, index) => {
      const prefixes = values.map((value) => value + part.value.cooked);
      return index < node.expressions.length
        ? prefixes.flatMap((prefix) => literalValues(node.expressions[index]).map((value) => prefix + value))
        : prefixes;
    }, ['']);
  }
  return [];
}

function translationKeys(text, filename) {
  if (path.extname(filename) === '.cs') {
    return Array.from(text.matchAll(CS_KEY_RE), (match) => match[1]);
  }
  if (!/\btranslate\s*\(/.test(text)) return [];

  const keys = new Set();
  const ast = parseSync(text, {
    filename, configFile: false, babelrc: false,
    parserOpts: { plugins: ['jsx', ['typescript', { dts: filename.endsWith('.d.ts') }], 'decorators-legacy'] }
  });
  traverse(ast, {
    CallExpression({ node }) {
      if (node.callee.type === 'Identifier' && node.callee.name === 'translate') {
        literalValues(node.arguments[0]).forEach((key) => keys.add(key));
      }
    }
  });
  return [...keys];
}

function walk(dir) {
  const out = [];
  for (const name of fs.readdirSync(dir)) {
    const full = path.join(dir, name);
    const stat = fs.statSync(full);
    if (stat.isDirectory() && !['bin', 'obj', 'node_modules'].includes(name)) {
      out.push(...walk(full));
    } else if (EXTS.has(path.extname(full))) {
      out.push(full);
    }
  }
  return out;
}

function getDiffFiles() {
  const out = execSync('git diff --name-only frontend/src src', { cwd: REPO_ROOT, encoding: 'utf8' });
  return out.split('\n')
    .filter((p) => p && EXTS.has(path.extname(p)))
    .map((p) => path.join(REPO_ROOT, p));
}

function main() {
  const diffOnly = process.argv.includes('--diff');
  const existing = new Set(Object.keys(JSON.parse(fs.readFileSync(EN_JSON, 'utf8'))));
  const files = diffOnly ? getDiffFiles() : [...walk(FRONTEND_SRC), ...walk(BACKEND_SRC)];

  const missing = new Map();
  for (const f of files.filter((file) => fs.existsSync(file))) {
    const text = fs.readFileSync(f, 'utf8');
    for (const key of translationKeys(text, f)) {
      if (!existing.has(key)) {
        const rel = path.relative(REPO_ROOT, f);
        if (!missing.has(key)) {
          missing.set(key, []);
        }
        missing.get(key).push(rel);
      }
    }
  }

  if (missing.size === 0) {
    console.log(`OK: all literal translation keys exist in en.json (${files.length} files scanned)`);
    return;
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
  process.exitCode = 1;
}

module.exports = { translationKeys };

if (require.main === module) main();
