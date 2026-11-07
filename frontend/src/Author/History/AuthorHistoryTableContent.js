import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import Icon from 'Components/Icon';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import { icons, kinds } from 'Helpers/Props';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';
import AuthorHistoryRowConnector from './AuthorHistoryRowConnector';
import styles from './AuthorHistoryTableContent.css';

const columns = [
  {
    name: 'eventType',
    isVisible: true
  },
  {
    name: 'book',
    label: () => translate('Book'),
    isVisible: true
  },
  {
    name: 'sourceTitle',
    label: () => translate( 'SourceTitle'),
    isVisible: true
  },
  {
    name: 'quality',
    label: () => translate('Quality'),
    isVisible: true
  },
  {
    name: 'customFormats',
    label: () => translate('CustomFormats'),
    isSortable: false,
    isVisible: true
  },
  {
    name: 'customFormatScore',
    label: React.createElement(Icon, {
      name: icons.SCORE,
      title: () => translate('CustomFormatScore')
    }),
    isSortable: true,
    isVisible: true
  },
  {
    name: 'date',
    label: () => translate('Date'),
    isVisible: true
  },
  {
    name: 'actions',
    isVisible: true
  }
];

class AuthorHistoryTableContent extends Component {

  //
  // Render

  render() {
    const {
      bookId,
      isFetching,
      isPopulated,
      error,
      items,
      onRetryPress,
      onMarkAsFailedPress
    } = this.props;

    const fullAuthor = bookId == null;
    const hasItems = !!items.length;

    return (
      <div>
        {
          isFetching &&
            <LoadingIndicator />
        }

        {
          !isFetching && !!error &&
            <Alert kind={kinds.DANGER} role="alert">
              <div>{getErrorMessage(error, translate('UnableToLoadHistory'))}</div>
              <Button size="small" onPress={onRetryPress}>
                {translate('Retry')}
              </Button>
            </Alert>
        }

        {
          isPopulated && !hasItems && !error &&
            <div className={styles.blankpad}>
              {translate('NoHistory')}
            </div>
        }

        {
          isPopulated && hasItems && !error &&
            <Table columns={columns}>
              <TableBody>
                {
                  items.map((item) => {
                    return (
                      <AuthorHistoryRowConnector
                        key={item.id}
                        fullAuthor={fullAuthor}
                        {...item}
                        onMarkAsFailedPress={onMarkAsFailedPress}
                      />
                    );
                  })
                }
              </TableBody>
            </Table>
        }
      </div>
    );
  }
}

AuthorHistoryTableContent.propTypes = {
  bookId: PropTypes.number,
  isFetching: PropTypes.bool.isRequired,
  isPopulated: PropTypes.bool.isRequired,
  error: PropTypes.object,
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  onRetryPress: PropTypes.func.isRequired,
  onMarkAsFailedPress: PropTypes.func.isRequired
};

export default AuthorHistoryTableContent;
