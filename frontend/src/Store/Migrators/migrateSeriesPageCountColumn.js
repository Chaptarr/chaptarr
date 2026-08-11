import get from 'lodash/get';

export default function migrateSeriesPageCountColumn(persistedState) {
  const seriesColumns = get(persistedState, 'series.columns');

  if (seriesColumns && Array.isArray(seriesColumns)) {
    const pageCountColumn = seriesColumns.find((col) => col.name === 'pageCount');

    if (pageCountColumn) {
      // Hide the Pages column in Series tab for existing users
      pageCountColumn.isVisible = false;
    }
  }
}
