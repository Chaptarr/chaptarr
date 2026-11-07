import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import TextInput from 'Components/Form/TextInput';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import Scroller from 'Components/Scroller/Scroller';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import { kinds, scrollDirections } from 'Helpers/Props';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';
import SelectBookRow from './SelectBookRow';
import styles from './SelectBookModalContent.css';

const columns = [
  {
    name: 'mediaType',
    label: () => translate('Type'),
    isVisible: true
  },
  {
    name: 'title',
    label: () => translate('BookTitle'),
    isSortable: true,
    isVisible: true
  },
  {
    name: 'releaseDate',
    label: () => translate('ReleaseDate'),
    isSortable: true,
    isVisible: true
  },
  {
    name: 'status',
    label: () => translate('BookStatus'),
    isVisible: true
  }
];

class SelectBookModalContent extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      filter: ''
    };
  }

  //
  // Listeners

  onFilterChange = ({ value }) => {
    this.setState({ filter: value });
  };

  onClearFilterPress = () => {
    this.setState({ filter: '' });
  };

  //
  // Render

  render() {
    const {
      items,
      onBookSelect,
      onModalClose,
      isFetching,
      isPopulated,
      error,
      ...otherProps
    } = this.props;

    const filter = this.state.filter;
    const filterLower = filter.toLowerCase();
    const filteredItems = items.filter((item) => item.title.toLowerCase().includes(filterLower));
    const isLoading = isFetching || (!isPopulated && !error);

    return (
      <ModalContent onModalClose={onModalClose}>
        <ModalHeader>
          {translate('ManualImportSelectBook')}
        </ModalHeader>

        <ModalBody
          className={styles.modalBody}
          scrollDirection={scrollDirections.NONE}
        >
          {
            isLoading &&
              <LoadingIndicator />
          }

          <TextInput
            className={styles.filterInput}
            placeholder={translate('FilterPlaceHolder')}
            name="filter"
            value={filter}
            autoFocus={true}
            onChange={this.onFilterChange}
          />

          <Scroller className={styles.scroller} autoFocus={false}>
            {
              !isLoading && error &&
                <Alert kind={kinds.DANGER} role="alert">
                  <div className={styles.emptyState}>
                    <span>{getErrorMessage(error, translate('UnableToLoadBooksForManualImport'))}</span>
                    <Button size="small" onPress={this.props.onRetryPress}>
                      {translate('Retry')}
                    </Button>
                  </div>
                </Alert>
            }

            {
              !isLoading && !error && !items.length &&
                <Alert kind={kinds.INFO} role="status">
                  {translate('ManualImportNoBooksAvailable')}
                </Alert>
            }

            {
              !isLoading && !error && !!items.length && !filteredItems.length &&
                <Alert
                  className={styles.emptyState}
                  kind={kinds.INFO}
                  role="status"
                >
                  <span>{translate('ManualImportNoBooksMatchFilter')}</span>
                  <Button size="small" onPress={this.onClearFilterPress}>
                    {translate('ClearFilter')}
                  </Button>
                </Alert>
            }

            {
              !isLoading && !error && !!filteredItems.length &&
                <Table
                  columns={columns}
                  {...otherProps}
                >
                  <TableBody>
                    {
                      filteredItems.map((item) => (
                        <SelectBookRow
                          key={item.id}
                          columns={columns}
                          onBookSelect={onBookSelect}
                          {...item}
                        />
                      ))
                    }
                  </TableBody>
                </Table>
            }
          </Scroller>
        </ModalBody>

        <ModalFooter>
          <Button onPress={onModalClose}>
            {translate('Cancel')}
          </Button>
        </ModalFooter>
      </ModalContent>
    );
  }
}

SelectBookModalContent.propTypes = {
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  isPopulated: PropTypes.bool.isRequired,
  isFetching: PropTypes.bool.isRequired,
  error: PropTypes.object,
  onBookSelect: PropTypes.func.isRequired,
  onRetryPress: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default SelectBookModalContent;
