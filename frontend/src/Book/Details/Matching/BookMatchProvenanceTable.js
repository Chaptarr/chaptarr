import PropTypes from 'prop-types';
import React from 'react';
import translate from 'Utilities/String/translate';
import styles from './BookMatchProvenanceTable.css';

const dispositionPriority = { conflicting: 3, supporting: 2, neutral: 1 };
const dispositionDisplayOrder = { supporting: 0, conflicting: 1, neutral: 2 };
const signalOrder = {
  provider_identifier: 0,
  title: 1,
  author: 2,
  series_name: 3,
  series_position: 4,
  narrator: 5,
  duration: 6,
  publisher: 7,
  subtitle: 8,
  publication_year: 9,
  reading_format: 10,
  language: 11,
  tolerated_metadata: 12,
  unexplained_metadata: 13,
  edition_retarget: 14,
  edition_selection: 15,
  manual_selection: 16
};
const primarySignalTypes = new Set([
  'provider_identifier',
  'title',
  'author',
  'series_name',
  'series_position',
  'narrator',
  'duration',
  'publisher'
]);
const signalTranslationKeys = {
  provider_identifier: 'MatchEvidenceProviderIdentifier',
  author: 'Author',
  title: 'EditionTitle',
  subtitle: 'Subtitle',
  duration: 'Duration',
  publication_year: 'ReleaseYear',
  publisher: 'Publisher',
  narrator: 'Narrator',
  series_name: 'Series',
  series_position: 'SeriesPosition',
  reading_format: 'Format',
  language: 'Language',
  tolerated_metadata: 'MatchEvidenceToleratedMetadata',
  unexplained_metadata: 'MatchEvidenceUnexplainedMetadata',
  edition_retarget: 'MatchEvidenceEditionRetarget',
  edition_selection: 'MatchEvidenceEditionSelection',
  manual_selection: 'ManualSelection'
};

function getFileName(path) {
  return (path || '').split(/[\\/]/).filter(Boolean).pop() || path;
}

function humanize(value) {
  return value ?
    value
      .replace(/[/_]+/g, ' ')
      .replace(/\b\w/g, (character) => character.toUpperCase()) :
    '';
}

function getModeLabel(mode) {
  switch ((mode || '').toLowerCase()) {
    case 'aggressive':
      return translate('BookMatchingStrictnessAggressive');
    case 'balanced':
      return translate('BookMatchingStrictnessBalanced');
    case 'strict':
      return translate('BookMatchingStrictnessStrict');
    case 'manual':
      return translate('Manual');
    default:
      return mode || translate('Unknown');
  }
}

function getSignalLabel(type) {
  const key = signalTranslationKeys[type];
  return key ? translate(key) : humanize(type);
}

function formatSignalValue(signal, value) {
  if (signal.type !== 'duration') {
    return value;
  }

  const match = (/^(\d+) seconds$/).exec(value || '');
  if (!match) {
    return value;
  }

  const totalSeconds = Number(match[1]);
  if (!totalSeconds) {
    return '';
  }

  return [
    Math.floor(totalSeconds / 3600),
    Math.floor((totalSeconds % 3600) / 60),
    totalSeconds % 60
  ]
    .map((part) => String(part).padStart(2, '0'))
    .join(':');
}

function getRangeTitle(ranges) {
  return ranges
    .map((range) => {
      const label = getSignalLabel(range.type);
      return range.detail ? `${label}: ${range.detail}` : label;
    })
    .filter((value, index, values) => values.indexOf(value) === index)
    .join('\n');
}

function getRangeDisposition(ranges) {
  return ranges
    .map((range) => range.disposition)
    .sort(
      (left, right) =>
        (dispositionPriority[right] || 0) - (dispositionPriority[left] || 0)
    )[0];
}

function buildEvidenceSegments(value, ranges) {
  const validRanges = (ranges || []).filter(
    (range) =>
      Number.isInteger(range.start) &&
      Number.isInteger(range.end) &&
      range.start >= 0 &&
      range.end > range.start &&
      range.end <= value.length
  );

  if (!validRanges.length) {
    return [{ text: value, disposition: null, ranges: [] }];
  }

  const boundaries = Array.from(
    new Set([
      0,
      value.length,
      ...validRanges.flatMap((range) => [range.start, range.end])
    ])
  ).sort((left, right) => left - right);
  const segments = [];

  for (let index = 0; index < boundaries.length - 1; index++) {
    const start = boundaries[index];
    const end = boundaries[index + 1];
    if (end <= start) {
      // eslint-disable-next-line no-continue
      continue;
    }

    const activeRanges = validRanges.filter(
      (range) => range.start < end && range.end > start
    );
    const disposition = getRangeDisposition(activeRanges);
    const previous = segments[segments.length - 1];

    if (
      previous &&
      previous.disposition === disposition &&
      getRangeTitle(previous.ranges) === getRangeTitle(activeRanges)
    ) {
      previous.text += value.slice(start, end);
      // eslint-disable-next-line no-continue
      continue;
    }

    segments.push({
      text: value.slice(start, end),
      disposition,
      ranges: activeRanges
    });
  }

  return segments;
}

function getDispositionClassName(disposition) {
  switch (disposition) {
    case 'supporting':
      return styles.supportingText;
    case 'conflicting':
      return styles.conflictingText;
    case 'neutral':
      return styles.neutralText;
    default:
      return undefined;
  }
}

function getAllSignals(provenance) {
  return [
    ['supporting', provenance.supportingSignals || []],
    ['conflicting', provenance.conflictingSignals || []],
    ['neutral', provenance.neutralSignals || []]
  ].flatMap(([disposition, signals]) =>
    signals.map((signal) => ({ ...signal, disposition }))
  );
}

function getSignalKey(signal) {
  return [
    signal.disposition,
    signal.type,
    signal.scope,
    signal.source,
    signal.field,
    signal.observed,
    signal.expected,
    signal.detail
  ].join('|');
}

function distinctSignals(signals) {
  const seen = new Set();

  return signals.filter((signal) => {
    const key = getSignalKey(signal);
    if (seen.has(key)) {
      return false;
    }

    seen.add(key);
    return true;
  });
}

function getFactText(signal) {
  const observed = formatSignalValue(signal, signal.observed);
  const expected = formatSignalValue(signal, signal.expected);

  if (observed && expected) {
    return observed === expected ?
      observed :
      `${translate('Observed')}: ${observed} · ${translate(
        'Edition'
      )}: ${expected}`;
  }

  if (observed) {
    return observed;
  }

  if (expected) {
    return signal.disposition === 'neutral' ?
      `${translate('NotFoundInFileMetadata')} · ${translate(
        'Edition'
      )}: ${expected}` :
      expected;
  }

  return signal.detail || '';
}

function getPrimarySignalGroups(signals) {
  return Array.from(primarySignalTypes)
    .map((type) => {
      const seen = new Set();
      const values = signals
        .filter((signal) => signal.type === type)
        .map((signal) => ({ signal, text: getFactText(signal) }))
        .filter(({ signal, text }) => {
          const key = `${signal.disposition}|${text}`;
          if (!text || seen.has(key)) {
            return false;
          }

          seen.add(key);
          return true;
        })
        .sort(
          (left, right) =>
            (dispositionDisplayOrder[left.signal.disposition] ?? 99) -
            (dispositionDisplayOrder[right.signal.disposition] ?? 99)
        );

      return { type, values };
    })
    .filter((group) => group.values.length);
}

function getDecisionSignalKeys(provenance) {
  return new Set(
    getAllSignals(provenance)
      .filter((signal) => signal.type)
      .map((signal) => `${signal.type}|${signal.source || ''}`)
  );
}

function getVisibleEvidenceValues(provenance) {
  if (!provenance || provenance.schemaVersion < 2) {
    return [];
  }

  const signalKeys = getDecisionSignalKeys(provenance);

  return (provenance.evidenceValues || [])
    .map((evidenceValue) => {
      const ranges = evidenceValue.ranges || [];
      const decisionRanges = ranges.filter(
        (range) =>
          range.type !== 'tolerated_metadata' &&
          signalKeys.has(`${range.type}|${evidenceValue.source || ''}`)
      );

      if (!decisionRanges.length) {
        return null;
      }

      return {
        ...evidenceValue,
        ranges: ranges.filter(
          (range) =>
            range.type === 'tolerated_metadata' ||
            decisionRanges.includes(range)
        )
      };
    })
    .filter(Boolean);
}

function getRouteLabel(provenance) {
  const routes = [
    provenance?.route,
    ...(provenance?.mergedRoutes || [])
  ]
    .filter(Boolean)
    .join(' ')
    .toLowerCase();
  const evidenceSources = new Set(
    getVisibleEvidenceValues(provenance).map((value) => value.source)
  );
  const hasEmbeddedEvidence = evidenceSources.has('embedded_tag');
  const hasPathEvidence =
    evidenceSources.has('path') || evidenceSources.has('filename');

  if (routes.includes('manual')) {
    return translate('MatchedManually');
  }

  if (
    routes.includes('supplemental_path') ||
    (hasEmbeddedEvidence && hasPathEvidence)
  ) {
    return translate('MatchedFromEmbeddedTagsAndPath');
  }

  if (evidenceSources.has('filename') && !evidenceSources.has('path')) {
    return translate('MatchedFromFilename');
  }

  if (hasPathEvidence || routes.includes('path')) {
    return translate('MatchedFromFolderAndFileNames');
  }

  return translate('MatchedFromEmbeddedTags');
}

function getEvidenceFieldLabel(evidenceValue) {
  if (evidenceValue.source === 'filename') {
    return translate('Filename');
  }

  if (evidenceValue.source === 'path') {
    return translate('FolderPath');
  }

  return (evidenceValue.fields || []).join(', ') || translate('Field');
}

function getEvidenceSortOrder(evidenceValue) {
  return Math.min(
    ...(evidenceValue.ranges || []).map(
      (range) => signalOrder[range.type] ?? 999
    ),
    999
  );
}

function getEvidenceSignalTypes(evidenceValue) {
  return Array.from(
    new Set(
      (evidenceValue.ranges || [])
        .map((range) => range.type)
        .filter((type) => primarySignalTypes.has(type))
    )
  ).sort(
    (left, right) => (signalOrder[left] ?? 999) - (signalOrder[right] ?? 999)
  );
}

function AnnotatedEvidenceValue({ evidenceValue }) {
  const value = evidenceValue.value || '';
  const segments = buildEvidenceSegments(value, evidenceValue.ranges);

  return (
    <div className={styles.annotatedEvidence}>
      <div className={styles.evidenceFields}>
        {getEvidenceFieldLabel(evidenceValue)}
      </div>
      <div className={styles.evidenceText}>
        {segments.map((segment, index) => (
          <span
            key={index}
            className={
              getDispositionClassName(segment.disposition) ||
              styles.unmatchedText
            }
            title={getRangeTitle(segment.ranges)}
          >
            {segment.text}
          </span>
        ))}
      </div>
    </div>
  );
}

AnnotatedEvidenceValue.propTypes = {
  evidenceValue: PropTypes.object.isRequired
};

function EvidenceLegend() {
  return (
    <div className={styles.legend}>
      <span>
        <i className={styles.supportingDot} />
        {translate('EvidenceForMatch')}
      </span>
      <span>
        <i className={styles.conflictingDot} />
        {translate('EvidenceAgainstMatch')}
      </span>
      <span>
        <i className={styles.neutralDot} />
        {translate('EvidenceNeutral')}
      </span>
    </div>
  );
}

function getSummaryRows(provenance) {
  const coveredSignalKeys = new Set();
  const evidenceRows = getVisibleEvidenceValues(provenance)
    .map((evidenceValue) => {
      const types = getEvidenceSignalTypes(evidenceValue);
      if (!types.length) {
        return null;
      }

      types.forEach((type) => {
        coveredSignalKeys.add(`${type}|${evidenceValue.source || ''}`);
      });

      return {
        kind: 'evidence',
        order: getEvidenceSortOrder(evidenceValue),
        types,
        evidenceValue: {
          ...evidenceValue,
          ranges: (evidenceValue.ranges || []).filter(
            (range) =>
              range.type === 'tolerated_metadata' ||
              primarySignalTypes.has(range.type)
          )
        }
      };
    })
    .filter(Boolean);
  const fallbackSignals = distinctSignals(getAllSignals(provenance)).filter(
    (signal) =>
      primarySignalTypes.has(signal.type) &&
      !coveredSignalKeys.has(`${signal.type}|${signal.source || ''}`)
  );
  const factRows = getPrimarySignalGroups(fallbackSignals).map((group) => ({
    kind: 'fact',
    order: signalOrder[group.type] ?? 999,
    ...group
  }));

  return [...evidenceRows, ...factRows].sort((left, right) => {
    const orderDifference = left.order - right.order;
    if (orderDifference) {
      return orderDifference;
    }

    if (left.kind !== right.kind) {
      return left.kind === 'evidence' ? -1 : 1;
    }

    if (left.kind === 'evidence' && right.kind === 'evidence') {
      return getEvidenceFieldLabel(left.evidenceValue).localeCompare(
        getEvidenceFieldLabel(right.evidenceValue)
      );
    }

    return 0;
  });
}

function SignalSummary({ provenance }) {
  const rows = getSummaryRows(provenance);
  if (!rows.length) {
    return null;
  }

  const hasAnnotatedEvidence = rows.some((row) => row.kind === 'evidence');

  return (
    <section className={styles.summarySection}>
      <div className={styles.summaryHeader}>
        <h3 className={styles.sectionTitle}>{translate('MatchedBecause')}</h3>
        {hasAnnotatedEvidence ? <EvidenceLegend /> : null}
      </div>
      <div className={styles.signalList}>
        {rows.map((row, index) => {
          if (row.kind === 'evidence') {
            const label = row.types.map(getSignalLabel).join(' · ');

            return (
              <div
                key={`${row.evidenceValue.source}-${row.evidenceValue.value}-${index}`}
                className={styles.signalRow}
              >
                <div className={styles.signalLabel}>{label}</div>
                <div className={styles.signalValues}>
                  <AnnotatedEvidenceValue
                    evidenceValue={row.evidenceValue}
                  />
                </div>
              </div>
            );
          }

          return (
            <div key={row.type} className={styles.signalRow}>
              <div className={styles.signalLabel}>
                {getSignalLabel(row.type)}
              </div>
              <div className={styles.signalValues}>
                {row.values.map(({ signal, text }, valueIndex) => (
                  <div
                    key={`${signal.disposition}-${text}-${valueIndex}`}
                    className={getDispositionClassName(signal.disposition)}
                    title={signal.detail}
                  >
                    {text}
                  </div>
                ))}
              </div>
            </div>
          );
        })}
      </div>
    </section>
  );
}

SignalSummary.propTypes = {
  provenance: PropTypes.object.isRequired
};

function getOtherSignals(provenance) {
  return distinctSignals(getAllSignals(provenance))
    .filter((signal) => !primarySignalTypes.has(signal.type))
    .sort(
      (left, right) =>
        (signalOrder[left.type] ?? 999) - (signalOrder[right.type] ?? 999)
    );
}

function DecisionDetails({ provenance, items, showFiles }) {
  const matchedAt = provenance?.matchedAtUtc ?
    new Date(provenance.matchedAtUtc).toLocaleString() :
    null;
  const routes = Array.from(
    new Set(
      [provenance.route, ...(provenance.mergedRoutes || [])].filter(Boolean)
    )
  );
  const matchedVia = Array.from(
    new Set(
      [provenance.matchedVia, ...(provenance.mergedMatchedVia || [])]
        .filter(Boolean)
        .filter((value) => !routes.includes(value))
    )
  );
  const excludedFields = Array.from(
    new Set(
      (provenance.excludedSignals || [])
        .map((signal) => signal.field || getSignalLabel(signal.type))
        .filter(Boolean)
    )
  );
  const otherSignals = getOtherSignals(provenance);

  return (
    <details className={styles.decisionDetails}>
      <summary>{translate('DecisionDetails')}</summary>
      <div className={styles.decisionDetailsContent}>
        {matchedAt ? <div>{translate('MatchedOn', [matchedAt])}</div> : null}
        {routes.length ? (
          <div>
            <strong>{translate('Route')}:</strong> {routes.join(', ')}
          </div>
        ) : null}
        {matchedVia.length ? (
          <div>
            <strong>{translate('MatchedVia')}:</strong> {matchedVia.join(', ')}
          </div>
        ) : null}
        {provenance.matcherVersion ? (
          <div>
            <strong>{translate('Matcher')}:</strong>{' '}
            {provenance.matcherVersion}
          </div>
        ) : null}
        {provenance.decisionId ? (
          <div>
            <strong>{translate('DecisionId')}:</strong>{' '}
            {provenance.decisionId}
          </div>
        ) : null}
        {excludedFields.length ? (
          <div>
            <strong>
              {translate('IgnoredMetadataCount', [excludedFields.length])}:
            </strong>{' '}
            {excludedFields.join(', ')}
          </div>
        ) : null}
        {otherSignals.length ? (
          <div>
            <strong>{translate('OtherEvidence')}:</strong>{' '}
            {otherSignals
              .map((signal) => {
                const text = getFactText(signal);
                return text ? `${getSignalLabel(signal.type)} — ${text}` : null;
              })
              .filter(Boolean)
              .join('; ')}
          </div>
        ) : null}
        {showFiles ? (
          <div className={styles.fileAudit}>
            <strong>{translate('FilesTotal', [items.length])}</strong>
            <ul>
              {items.map((item) => (
                <li key={item.id || item.path} title={item.path}>
                  {getFileName(item.path)}
                </li>
              ))}
            </ul>
          </div>
        ) : null}
      </div>
    </details>
  );
}

DecisionDetails.propTypes = {
  provenance: PropTypes.object.isRequired,
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  showFiles: PropTypes.bool.isRequired
};

function mergeSignals(provenances, property) {
  const signals = provenances.flatMap((provenance) =>
    (provenance[property] || []).map((signal) => ({ ...signal }))
  );
  const seen = new Set();

  return signals.filter((signal) => {
    const key = [
      signal.type,
      signal.scope,
      signal.source,
      signal.field,
      signal.observed,
      signal.expected,
      signal.detail
    ].join('|');

    if (seen.has(key)) {
      return false;
    }

    seen.add(key);
    return true;
  });
}

function mergeEvidenceValues(provenances) {
  const values = new Map();

  provenances.forEach((provenance) => {
    (provenance.evidenceValues || []).forEach((evidenceValue) => {
      const key = `${evidenceValue.source || ''}\u0000${evidenceValue.value || ''}`;
      let mergedValue = values.get(key);

      if (!mergedValue) {
        mergedValue = {
          ...evidenceValue,
          fields: [],
          ranges: []
        };
        values.set(key, mergedValue);
      }

      mergedValue.fields = Array.from(
        new Set([
          ...mergedValue.fields,
          ...(evidenceValue.fields || [])
        ])
      ).sort((left, right) => left.localeCompare(right));

      const rangeKeys = new Set(
        mergedValue.ranges.map((range) => JSON.stringify(range))
      );
      (evidenceValue.ranges || []).forEach((range) => {
        const rangeKey = JSON.stringify(range);
        if (!rangeKeys.has(rangeKey)) {
          rangeKeys.add(rangeKey);
          mergedValue.ranges.push({ ...range });
        }
      });
    });
  });

  return Array.from(values.values());
}

function mergeProvenance(provenances) {
  const first = provenances[0];

  return {
    ...first,
    schemaVersion: Math.max(
      ...provenances.map((provenance) => provenance.schemaVersion || 0)
    ),
    supportingSignals: mergeSignals(provenances, 'supportingSignals'),
    conflictingSignals: mergeSignals(provenances, 'conflictingSignals'),
    neutralSignals: mergeSignals(provenances, 'neutralSignals'),
    excludedSignals: mergeSignals(provenances, 'excludedSignals'),
    evidenceValues: mergeEvidenceValues(provenances),
    mergedRoutes: provenances
      .map((provenance) => provenance.route)
      .filter(Boolean),
    mergedMatchedVia: provenances
      .map((provenance) => provenance.matchedVia)
      .filter(Boolean)
  };
}

function getDecisionGroupKey(item) {
  const provenance = item.matchProvenance;

  if (provenance?.decisionId) {
    return `decision:${provenance.decisionId}`;
  }

  if (provenance) {
    return `file:${item.id || item.path}`;
  }

  return 'unrecorded';
}

function groupItemsByDecision(items) {
  const groups = new Map();

  items.forEach((item) => {
    const key = getDecisionGroupKey(item);

    if (!groups.has(key)) {
      groups.set(key, []);
    }

    groups.get(key).push(item);
  });

  return Array.from(groups.values()).map((groupItems) => {
    const provenances = groupItems
      .map((item) => item.matchProvenance)
      .filter(Boolean);

    return {
      items: groupItems,
      provenance: provenances.length ? mergeProvenance(provenances) : null
    };
  });
}

function MatchDecision({ group, showFiles }) {
  const { items, provenance } = group;

  if (!provenance) {
    return (
      <div className={styles.decision}>
        <div className={styles.noExplanation}>
          {translate('NoRecordedMatchExplanation')}
        </div>
        {showFiles ? (
          <details className={styles.decisionDetails}>
            <summary>{translate('FilesTotal', [items.length])}</summary>
            <ul className={styles.unrecordedFiles}>
              {items.map((item) => (
                <li key={item.id || item.path}>{getFileName(item.path)}</li>
              ))}
            </ul>
          </details>
        ) : null}
      </div>
    );
  }

  return (
    <article className={styles.decision}>
      <header className={styles.decisionHeader}>
        <div className={styles.route}>{getRouteLabel(provenance)}</div>
        <div className={styles.mode}>
          {translate('MatchedUsingMode', [getModeLabel(provenance.mode)])}
        </div>
      </header>

      <SignalSummary provenance={provenance} />
      <DecisionDetails
        provenance={provenance}
        items={items}
        showFiles={showFiles}
      />
    </article>
  );
}

MatchDecision.propTypes = {
  group: PropTypes.shape({
    items: PropTypes.arrayOf(PropTypes.object).isRequired,
    provenance: PropTypes.object
  }).isRequired,
  showFiles: PropTypes.bool.isRequired
};

function BookMatchProvenanceTable({ items }) {
  if (!items.length) {
    return (
      <div className={styles.blankpad}>{translate('NoBookFilesToManage')}</div>
    );
  }

  const groups = groupItemsByDecision(items);

  return (
    <div className={styles.container}>
      {groups.map((group, index) => (
        <MatchDecision
          key={group.provenance?.decisionId || `unrecorded-${index}`}
          group={group}
          showFiles={group.items.length > 1 || groups.length > 1}
        />
      ))}
    </div>
  );
}

BookMatchProvenanceTable.propTypes = {
  items: PropTypes.arrayOf(PropTypes.object).isRequired
};

export default BookMatchProvenanceTable;
