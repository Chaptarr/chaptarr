const assert = require('node:assert/strict');
const test = require('node:test');
const postcss = require('postcss');
const logicalDirectionPlugin = require('./postcss-logical-direction');

test('rewrites physical layout declarations to direction-aware properties', async () => {
  const result = await postcss([logicalDirectionPlugin()]).process(
    '.sample { margin-left: 4px; margin: 1px 2px 3px; padding: 1px 2px 3px 4px; border-top-right-radius: 3px; border-radius: 1px 2px 3px 4px; border-radius: 1px 2px / 3px 4px; left: 0; text-align: right; float: left; background-position: left; }',
    { from: undefined }
  );

  assert.match(result.css, /margin-inline-start: 4px/);
  assert.match(result.css, /margin-inline: 2px 2px/);
  assert.match(result.css, /margin-block: 1px 3px/);
  assert.match(result.css, /padding-inline: 4px 2px/);
  assert.match(result.css, /padding-block: 1px 3px/);
  assert.match(result.css, /border-start-end-radius: 2px/);
  assert.match(result.css, /border-end-start-radius: 4px/);
  assert.match(result.css, /border-start-start-radius: 1px 3px/);
  assert.match(result.css, /border-start-end-radius: 2px 4px/);
  assert.match(result.css, /inset-inline-start: 0/);
  assert.match(result.css, /text-align: end/);
  assert.match(result.css, /float: inline-start/);
  assert.match(result.css, /background-position: left/);
});
