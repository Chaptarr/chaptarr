import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import TextInput from 'Components/Form/TextInput';
import Button from 'Components/Link/Button';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import Scroller from 'Components/Scroller/Scroller';
import { kinds, scrollDirections } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import SelectAuthorRow from './SelectAuthorRow';
import styles from './SelectAuthorModalContent.css';

class SelectAuthorModalContent extends Component {

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
      onAuthorSelect,
      onModalClose
    } = this.props;

    const filter = this.state.filter;
    const filterLower = filter.toLowerCase();
    const filteredItems = items.filter((item) => item.authorName.toLowerCase().includes(filterLower));

    return (
      <ModalContent onModalClose={onModalClose}>
        <ModalHeader>
          {translate('ManualImportSelectAuthor')}
        </ModalHeader>

        <ModalBody
          className={styles.modalBody}
          scrollDirection={scrollDirections.NONE}
        >
          <TextInput
            className={styles.filterInput}
            placeholder={translate('FilterAuthor')}
            name="filter"
            value={filter}
            autoFocus={true}
            onChange={this.onFilterChange}
          />

          <Scroller className={styles.scroller} autoFocus={false}>
            {
              !items.length &&
                <Alert kind={kinds.INFO} role="status">
                  {translate('ManualImportNoAuthorsAvailable')}
                </Alert>
            }

            {
              !!items.length && !filteredItems.length &&
                <Alert
                  className={styles.emptyState}
                  kind={kinds.INFO}
                  role="status"
                >
                  <span>{translate('ManualImportNoAuthorsMatchFilter')}</span>
                  <Button size="small" onPress={this.onClearFilterPress}>
                    {translate('ClearFilter')}
                  </Button>
                </Alert>
            }

            {
              filteredItems.map((item) => (
                <SelectAuthorRow
                  key={item.id}
                  id={item.id}
                  authorName={item.authorName}
                  onAuthorSelect={onAuthorSelect}
                />
              ))
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

SelectAuthorModalContent.propTypes = {
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  onAuthorSelect: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default SelectAuthorModalContent;
