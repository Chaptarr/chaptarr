const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const records = require('./server-messages.json');
const sourceInventory = JSON.parse(fs.readFileSync(path.resolve(__dirname, '../../../../docs/localization/server-message-sources.json'), 'utf8'));
const { compileServerMessageMatchers, matchServerMessage } = require('./server-message-match.cjs');

function record(id, source, parameters = []) {
  return {
    id,
    source,
    parameters: parameters.map((name) => ({ name })),
    patterns: [source]
  };
}

test('every registry pattern preserves every dynamic value exactly', () => {
  const matchers = compileServerMessageMatchers(records);

  for (const entry of records) {
    const tokens = Object.fromEntries(entry.parameters.map(({ name }) => [name, `DYNAMIC_${name}_العربية`]));

    for (const pattern of entry.patterns) {
      const message = pattern.replace(/\{value\d+\}/g, (token) => tokens[token.slice(1, -1)]);
      const actual = matchServerMessage(message, matchers);

      assert.equal(actual?.id, entry.id, `${entry.id}: ${pattern}`);
      assert.deepEqual(actual.tokens, tokens, entry.id);
    }
  }
});

test('fixed source matches are anchored and unknown messages remain unknown', () => {
  const matchers = compileServerMessageMatchers([record('fixed', 'A category is recommended')]);

  assert.equal(matchServerMessage('A category is recommended', matchers).id, 'fixed');
  assert.equal(matchServerMessage('prefix A category is recommended', matchers), null);
  assert.equal(matchServerMessage('A category is recommended suffix', matchers), null);
  assert.equal(matchServerMessage('A user wrote this message', matchers), null);
});

test('captured external data is not recursively interpreted or normalized', () => {
  const matchers = compileServerMessageMatchers([
    record('file', 'File \'{value0}\' does not exist', ['value0']),
    record('other', 'A category is recommended')
  ]);
  const value = 'A category is recommended / الكتب / file{0}.epub';
  const actual = matchServerMessage(`File '${value}' does not exist`, matchers);

  assert.equal(actual.id, 'file');
  assert.equal(actual.tokens.value0, value);
});

test('literal protocol examples with braces remain literal', () => {
  const source = 'Only Hardcover series IDs are currently supported (expected hc:{id})';
  const matchers = compileServerMessageMatchers([record('literal', source)]);

  assert.equal(matchServerMessage(source, matchers).id, 'literal');
  assert.equal(matchServerMessage(source.replace('{id}', '123'), matchers), null);
});

test('the most specific fixed wording wins over a broader template', () => {
  const matchers = compileServerMessageMatchers([
    record('broad', 'Unable to connect: {value0}', ['value0']),
    record('specific', 'Unable to connect: import list {value0}', ['value0'])
  ]);

  assert.equal(matchServerMessage('Unable to connect: import list external text', matchers).id, 'specific');
});

test('equally specific ambiguous messages are left unchanged', () => {
  const matchers = compileServerMessageMatchers([
    record('left', 'A{value0}B', ['value0']),
    record('right', 'A{value0}B', ['value0'])
  ]);

  assert.equal(matchServerMessage('AexternalB', matchers), null);
});

test('ambiguous interior delimiters are rejected without changing any data', () => {
  const matchers = compileServerMessageMatchers([
    record('two', 'From \'{value0}\' to \'{value1}\'', ['value0', 'value1'])
  ]);

  assert.equal(matchServerMessage('From \'a\' to \'b\' to \'c\'', matchers), null);
  assert.deepEqual(matchServerMessage('From \'a\' to \'b\'', matchers).tokens, { value0: 'a', value1: 'b' });
});

test('empty optional details and multiline external data are preserved', () => {
  const matchers = compileServerMessageMatchers([
    record('details', 'Test failed: {value0}', ['value0'])
  ]);

  assert.equal(matchServerMessage('Test failed: ', matchers).tokens.value0, '');
  assert.equal(matchServerMessage('Test failed: first\nsecond', matchers).tokens.value0, 'first\nsecond');
});

test('non-string and oversized inputs never enter matching', () => {
  const matchers = compileServerMessageMatchers([record('details', 'Test failed: {value0}', ['value0'])]);

  for (const value of [undefined, null, 1, {}, [], '']) {
    assert.equal(matchServerMessage(value, matchers), null);
  }

  assert.equal(matchServerMessage(`Test failed: ${'a'.repeat(8192)}`, matchers), null);
});

test('wholly dynamic patterns and adjacent captures are refused', () => {
  assert.throws(() => compileServerMessageMatchers([record('dynamic', '{value0}: {value1}', ['value0', 'value1'])]), /fixed application wording/);
  assert.throws(() => compileServerMessageMatchers([record('adjacent', 'Message {value0}{value1}', ['value0', 'value1'])]), /Adjacent/);
});

test('missing, duplicate, or unsafe parameter declarations are refused', () => {
  assert.throws(() => compileServerMessageMatchers([record('missing', 'Message {value0}', ['value0', 'value1'])]), /every parameter once/);
  assert.throws(() => compileServerMessageMatchers([record('repeated', 'Message {value0} {value0}', ['value0'])]), /every parameter once/);
  assert.throws(() => compileServerMessageMatchers([record('unsafe', 'Message {value0}', ['value0|.*'])]), /Invalid server-message parameters/);
});

test('duplicate IDs and conflicting exact patterns are refused', () => {
  assert.throws(() => compileServerMessageMatchers([record('same', 'First'), record('same', 'Second')]), /duplicate/);
  assert.throws(() => compileServerMessageMatchers([record('first', 'Same'), record('second', 'Same')]), /Conflicting/);
});

test('undeclared valueN examples stay literal and never become captures', () => {
  const matchers = compileServerMessageMatchers([{
    id: 'example.literalValue', source: 'Use {value0} literally', parameters: [], patterns: ['Use {value0} literally']
  }]);
  assert.deepEqual(matchServerMessage('Use {value0} literally', matchers).tokens, {});
  assert.equal(matchServerMessage('Use private-provider-data literally', matchers), null);
});

test('real concatenated and FluentValidation outputs capture expanded values', () => {
  const matchers = compileServerMessageMatchers(records);
  const state = matchServerMessage('Unknown download state: pausedUP', matchers);
  assert.equal(state.id, 'server.message.m0a3d9d1876f5');
  assert.deepEqual(state.tokens, { value0: 'pausedUP' });
  const folder = '/opt/Books [01]/كتاب {id}';
  const descendant = matchServerMessage(`Must not be a descendant of '${folder}'`, matchers);
  assert.equal(descendant.id, 'server.message.mc78af077e86e');
  assert.deepEqual(descendant.tokens, { value0: folder });
});

test('app-authored folder relationships select fixed variants instead of leaking English captures', () => {
  const matchers = compileServerMessageMatchers(records);
  const path = '/books/Book 01 - كتاب';
  const system = '/opt/chaptarr';
  const cases = [
    [`Path '${path}' is set to configured recycle bin folder`, 'server.message.m23e7ac60ea9c', { value0: path }],
    [`Path '${path}' is child of configured recycle bin folder`, 'server.message.m23e7ac60ea9c.child', { value0: path }],
    [`Path '${path}' cannot be set to the start up folder`, 'server.message.mca83d096fea2', { value0: path }],
    [`Path '${path}' cannot be child of the start up folder`, 'server.message.mca83d096fea2.child', { value0: path }],
    [`Path '${path}' is set to system folder ${system}`, 'server.message.mccd996f9f042', { value0: path, value1: system }],
    [`Path '${path}' is child of system folder ${system}`, 'server.message.mccd996f9f042.child', { value0: path, value1: system }],
    [`Path '${path}' is an ancestor of an existing author`, 'server.message.m2dffb9a40e48', { value0: path }],
    [`Path '${path}' is an ancestor of an existing root folder`, 'server.message.m6b84bc76853e', { value0: path }]
  ];
  for (const [message, id, tokens] of cases) {
    const result = matchServerMessage(message, matchers);
    assert.equal(result.id, id);
    assert.deepEqual(result.tokens, tokens);
  }
});

test('optional environment hints capture only opaque diagnostic fields', () => {
  const matchers = compileServerMessageMatchers(records);
  const environment = 'PUID=1000 PGID=1001 UMASK=002';
  const entry = records.find(({ id }) => id === 'server.message.m301553d117f5');
  const text = entry.patterns[0].replace('{value0}', '/books').replace('{value1}', 'keith (uid=1000)');
  assert.deepEqual(matchServerMessage(text, matchers).tokens, { value0: '/books', value1: 'keith (uid=1000)' });
  const withEnvironment = `${text} (env: ${environment})`;
  const result = matchServerMessage(withEnvironment, matchers);
  assert.equal(result.id, 'server.message.m301553d117f5.environment');
  assert.deepEqual(result.tokens, { value0: '/books', value1: 'keith (uid=1000)', value2: environment });
});

test('real grab/provenance/notification outputs match their fixed templates and preserve opaque captures', () => {
  const matchers = compileServerMessageMatchers(records);
  const cases = [
    ['Difference 7 seconds; matching tolerance 120 seconds.', 'Difference {value0} seconds; matching tolerance {value1} seconds.', { value0: '7', value1: '120' }],
    ['No tag names a narrator; the length matched the pinned edition. Difference 7 seconds; matching tolerance 120 seconds.', 'No tag names a narrator; the length matched the pinned edition. Difference {value0} seconds; matching tolerance {value1} seconds.', { value0: '7', value1: '120' }],
    ['No tag names a narrator; the length matched the pinned edition. Difference 7 seconds; matching tolerance 120 seconds; nearest other narration 300 seconds away.', 'No tag names a narrator; the length matched the pinned edition. Difference {value0} seconds; matching tolerance {value1} seconds; nearest other narration {value2} seconds away.', { value0: '7', value1: '120', value2: '300' }],
    ['No enabled Torrent download client is configured. Add and enable a Torrent-capable download client, then retry.', 'No enabled {value0} download client is configured. Add and enable a {value1}-capable download client, then retry.', { value0: 'Torrent', value1: 'Torrent' }],
    ['Unable to send text message: provider URL http://example.test/a/{value0}?q=العربية', 'Unable to send text message: {value0}', { value0: 'provider URL http://example.test/a/{value0}?q=العربية' }]
  ];
  for (const [message, source, tokens] of cases) {
    const result = matchServerMessage(message, matchers);
    assert.equal(result?.source, source, message);
    assert.deepEqual(result.tokens, tokens, message);
  }
  assert.equal(matchServerMessage('titleProof=true; provider diagnostic payload', matchers), null);
});

test('OAuth and provider API wrappers match without translating captured values', () => {
  const matchers = compileServerMessageMatchers(records);
  const missingLocation = sourceInventory.records.find(({ source }) => source === 'AudioBookShelf OIDC redirect missing Location header.');
  assert.deepEqual(missingLocation.protectedLiterals, ['AudioBookShelf', 'Location', 'OIDC']);
  const callback = matchServerMessage('QueryParam callbackUrl invalid.', matchers);
  assert.equal(callback.id, 'server.message.m90ee48cf606f');
  assert.deepEqual(callback.tokens, {});

  const status = matchServerMessage(
    'AudioBookShelf OIDC did not redirect (status 502 BadGateway). Ensure OpenID Connect is enabled and configured on your AudioBookShelf server.',
    matchers
  );
  assert.equal(status.id, 'server.message.ma6e1d81614c2');
  assert.deepEqual(status.tokens, { value0: '502 BadGateway' });

  const submittedId = '123 / كتاب {id}';
  const hardcover = matchServerMessage(`Invalid Hardcover series id: ${submittedId}`, matchers);
  assert.equal(hardcover.id, 'server.message.mccef5f1c0beb');
  assert.deepEqual(hardcover.tokens, { value0: 'series', value1: submittedId });
});
