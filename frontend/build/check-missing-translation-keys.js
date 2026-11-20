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
// Walk the requested argument so nested calls cannot terminate its condition early.
const CS_KEY_RE = /\b(GetLocalizedString|AddCountMessage|AddSummary)\s*\(/g;
// The C# argument walker is not a compiler: quoted char literals and generic
// type arguments containing commas remain known unsupported expressions.
// Verbatim C# strings (@"…") remain unsupported.
function backendKeyExpressions(text) {
  return Array.from(text.matchAll(CS_KEY_RE), (match) => {
    const wanted = match[1] === 'GetLocalizedString' ? 0 : 1;
    let start = match.index + match[0].length;
    let depth = 0;
    let argument = 0;
    let quoted = false;
    for (let i = start; i < text.length; i++) {
      const c = text[i];
      if (quoted && c === '\\') {
        i++;
      } else if (c === '"') {
        quoted = !quoted;
      } else if (!quoted) {
        if (c === '(') {
          depth++;
        } else if (c === ')' && depth > 0) {
          depth--;
        } else if ((c === ',' || c === ')') && depth === 0) {
          if (argument === wanted) {
            return text.slice(start, i).trim();
          }
          if (c === ')') {
            break;
          }
          argument++;
          start = i + 1;
        }
      }
    }
    return '';
  });
}

// JS/TS maps asserted with `as const` remain outside this literal-map resolver.
function literalValues(node, scope) {
  if (!node) {
    return [];
  }
  if (node.type === 'StringLiteral') {
    return [node.value];
  }
  if (node.type === 'MemberExpression' && node.object.type === 'Identifier') {
    const binding = scope.getBinding(node.object.name);
    const object = binding?.path.node.init;
    if (object?.type === 'ObjectExpression') {
      const key = node.computed ? node.property.value : node.property.name;
      return object.properties
        .filter((property) => property.type === 'ObjectProperty')
        .filter((property) => key === undefined ||
          (property.computed ? property.key.value : property.key.name ?? property.key.value) === key)
        .flatMap((property) => literalValues(property.value, scope));
    }
  }
  if (node.type === 'ConditionalExpression') {
    return [...literalValues(node.consequent, scope), ...literalValues(node.alternate, scope)];
  }
  if (node.type === 'LogicalExpression') {
    return [...literalValues(node.left, scope), ...literalValues(node.right, scope)];
  }
  if (node.type === 'BinaryExpression' && node.operator === '+') {
    return literalValues(node.left, scope).flatMap((left) => literalValues(node.right, scope).map((right) => left + right));
  }
  if (node.type === 'TemplateLiteral') {
    return node.quasis.reduce((values, part, index) => {
      const prefixes = values.map((value) => value + part.value.cooked);
      return index < node.expressions.length ?
        prefixes.flatMap((prefix) => literalValues(node.expressions[index], scope).map((value) => prefix + value)) :
        prefixes;
    }, ['']);
  }
  return [];
}

function translationKeys(text, filename) {
  if (path.extname(filename) === '.cs') {
    return backendKeyExpressions(text).flatMap((expression) => {
      const literal = (/^"([^"]+)"$/).exec(expression);
      return literal ? [literal[1]] : Array.from(expression.matchAll(/[?:]\s*"([^"]+)"/g), (branch) => branch[1]);
    });
  }
  if (!(/\btranslate\s*\(/).test(text)) {
    return [];
  }

  const keys = new Set();
  const ast = parseSync(text, {
    filename, configFile: false, babelrc: false,
    parserOpts: { plugins: ['jsx', ['typescript', { dts: filename.endsWith('.d.ts') }], 'decorators-legacy'] }
  });
  traverse(ast, {
    CallExpression({ node, scope }) {
      if (node.callee.type === 'Identifier' && node.callee.name === 'translate') {
        literalValues(node.arguments[0], scope).forEach((key) => keys.add(key));
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

if (require.main === module) {
  main();
}
