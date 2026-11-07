const assert = require('node:assert/strict');
const test = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const labels = require('./scheduled-task-labels.json');
const { getScheduledTaskLabel } = require('./scheduled-task-labels.cjs');
const english = require('../../../../src/NzbDrone.Core/Localization/Core/en.json');

test('every app-scheduled command has a source-current display label without altering its ID', () => {
  const taskManager = fs.readFileSync(path.resolve(__dirname, '../../../../src/NzbDrone.Core/Jobs/TaskManager.cs'), 'utf8');
  const commands = [...taskManager.matchAll(/TypeName = typeof\((\w+)Command\)/g)].map((match) => match[1]);
  assert.deepEqual(labels.map((record) => record.name), commands);
  assert.equal(labels.length, 15);
  const calls = [];
  for (const record of labels) {
    const original = JSON.stringify(record);
    assert.equal(record.source, english[record.id]);
    assert.equal(getScheduledTaskLabel(record.name, 'raw API label', (id, fallback) => {
      calls.push([id, fallback]);
      return `localized:${id}`;
    }), `localized:${record.id}`);
    assert.equal(JSON.stringify(record), original);
  }
  assert.equal(calls.length, 15);
  assert.equal(labels.filter((record) => record.id.startsWith('ui.task.')).length, 11);
});

test('unknown task IDs and inherited-property names retain their raw API label', () => {
  for (const taskName of ['FutureTask', 'constructor', 'toString', '__proto__', undefined]) {
    assert.equal(getScheduledTaskLabel(taskName, 'provider/Task {name} العربية', () => {
      assert.fail('Unknown task identifiers must not cross a translation boundary');
    }), 'provider/Task {name} العربية');
  }
});

test('known task labels can resolve the current language on each render with English fallback', () => {
  let language = 'en';
  const translate = (id, source) => language === 'ar' ? `عربي:${id}` : source;
  assert.equal(getScheduledTaskLabel('CheckHealth', 'raw', translate), 'Check Health');
  language = 'ar';
  assert.equal(getScheduledTaskLabel('CheckHealth', 'raw', translate), 'عربي:ui.task.checkHealth');
});
