import translate from 'Utilities/String/translate';

export default function getNoResultsMessage(result, fallbackKey) {
  const { indexersQueried = 0, indexersFailed = 0 } = result;

  if (indexersQueried > 0 && indexersFailed === indexersQueried) {
    return translate('SearchAllIndexersFailed');
  }

  if (indexersFailed > 0) {
    return translate('SearchNoResultsWithIndexerFailures', { failed: indexersFailed, total: indexersQueried });
  }

  return translate(fallbackKey);
}
