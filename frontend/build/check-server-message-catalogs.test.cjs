const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { readSourceCallsite, sha256 } = require('./check-server-message-catalogs.cjs');

function fixture(run) {
  const scratch = fs.mkdtempSync(path.join(os.tmpdir(), 'chaptarr-server-source-'));

  try {
    const root = path.join(scratch, 'repo');
    fs.mkdirSync(path.join(root, 'src'), { recursive: true });
    fs.writeFileSync(path.join(root, 'src/message.cs'), '  fixed application message\n');
    const site = {
      file: 'src/message.cs', line: 1, sourceLine: 'fixed application message',
      sourceLineSha256: sha256('fixed application message')
    };
    run({ root, site, scratch });
  } finally {
    fs.rmSync(scratch, { recursive: true, force: true });
  }
}

test('source evidence matches the exact current source line and fingerprint', () => fixture(({ root, site }) => {
  assert.equal(readSourceCallsite(root, site), site.sourceLine);
}));

test('source-copy changes and shifted source lines invalidate evidence', () => fixture(({ root, site }) => {
  assert.throws(() => readSourceCallsite(root, { ...site, line: 2 }), /changed/);
  fs.writeFileSync(path.join(root, site.file), 'changed application message\n');
  assert.throws(() => readSourceCallsite(root, site), /changed/);
}));

test('a source fingerprint cannot be changed independently of its text', () => fixture(({ root, site }) => {
  assert.throws(() => readSourceCallsite(root, { ...site, sourceLine: 'different message' }), /fingerprint/);
}));

test('absolute, traversal, backslash, and non-normalized paths are rejected', () => fixture(({ root, site }) => {
  for (const file of ['/etc/passwd', 'C:/Windows/win.ini', '../message.cs', 'src/../message.cs', 'src\\message.cs', './src/message.cs']) {
    assert.throws(() => readSourceCallsite(root, { ...site, file }), /normalized relative path/);
  }
}));

test('a symlink cannot carry source evidence outside the repository', () => fixture(({ root, site, scratch }) => {
  fs.writeFileSync(path.join(scratch, 'outside.cs'), `${site.sourceLine}\n`);
  fs.symlinkSync(path.join(scratch, 'outside.cs'), path.join(root, 'src/escape.cs'));
  assert.throws(() => readSourceCallsite(root, { ...site, file: 'src/escape.cs' }), /escapes/);
}));

test('invalid line numbers cannot select arbitrary source records', () => fixture(({ root, site }) => {
  for (const line of [0, -1, 1.5, '1']) {
    assert.throws(() => readSourceCallsite(root, { ...site, line }), /fingerprint/);
  }
}));
