const records = require('./scheduled-task-labels.json');

const labels = new Map(records.map((record) => [record.name, record]));

// The task ID remains protocol data. Only this closed display-label map translates.
function getScheduledTaskLabel(taskName, fallback, translateWithFallback) {
  const record = labels.get(taskName);
  return record ? translateWithFallback(record.id, record.source) : fallback;
}

module.exports = { getScheduledTaskLabel };
