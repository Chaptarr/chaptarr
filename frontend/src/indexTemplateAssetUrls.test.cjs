const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const ejs = require('ejs');

const template = fs.readFileSync(path.join(__dirname, 'index.ejs'), 'utf8');

function render(js = [], css = []) {
  return ejs.render(template, {
    cacheBuster: 'fixture',
    htmlWebpackPlugin: { files: { js, css } }
  });
}

test('bundled JavaScript and stylesheets emit same-origin paths', () => {
  const html = render(
    ['/Content/app.js?v=1#entry', 'https://chaptarr.invalid/Content/vendor.js'],
    ['/Content/app.css?v=2']
  );

  assert.match(html, /src="\/Content\/app\.js\?v=1#entry"/);
  assert.match(html, /src="\/Content\/vendor\.js"/);
  assert.match(html, /href="\/Content\/app\.css\?v=2"/);
});

test('bundled assets reject external origins and invalid URL values', () => {
  for (const asset of [
    'https://attacker.example/payload.js',
    '//attacker.example/payload.css',
    'javascript:alert(1)',
    'data:text/javascript,alert(1)',
    '',
    42,
    'x'.repeat(2049)
  ]) {
    assert.throws(
      () => render([asset]),
      /Invalid bundled asset URL|same-origin/
    );
  }

  assert.throws(
    () => render([], ['https://attacker.example/payload.css']),
    /same-origin/
  );
});
