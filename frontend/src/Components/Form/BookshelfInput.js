import _ from 'lodash';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import TableSelectCell from 'Components/Table/Cells/TableSelectCell';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import TableRow from 'Components/Table/TableRow';
import tagShape from 'Helpers/Props/Shapes/tagShape';
import getSelectedIds from 'Utilities/Table/getSelectedIds';
import selectAll from 'Utilities/Table/selectAll';
import toggleSelected from 'Utilities/Table/toggleSelected';
import translate from 'Utilities/String/translate';
import FormInputButton from './FormInputButton';
import TextInput from './TextInput';
import styles from './BookshelfInput.css';

const columns = [
  {
    name: 'name',
    label: 'Bookshelf',
    isSortable: false,
    isVisible: true
  }
];

function normalizeShelfIds(value) {
  if (Array.isArray(value)) {
    return _.uniq(value.map((v) => `${v}`.trim()).filter((v) => v.length > 0));
  }

  if (typeof value === 'string') {
    return _.uniq(value.split(',').map((v) => v.trim()).filter((v) => v.length > 0));
  }

  return [];
}

class BookshelfInput extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    const initialSelection = _.mapValues(_.keyBy(normalizeShelfIds(props.value)), () => true);

    this.state = {
      allSelected: false,
      allUnselected: false,
      selectedState: initialSelection,
      newShelfName: ''
    };
  }

  componentDidUpdate(prevProps, prevState) {
    const {
      name,
      onChange
    } = this.props;

    // If upstream value changes (e.g. edit modal re-opens or provider is reloaded),
    // re-hydrate local selection state so the checkboxes reflect persisted values.
    const prevValue = normalizeShelfIds(prevProps.value).sort();
    const nextValue = normalizeShelfIds(this.props.value).sort();

    if (!_.isEqual(prevValue, nextValue)) {
      const currentSelected = this.getSelectedIds().sort();

      if (!_.isEqual(currentSelected, nextValue)) {
        this.setState({
          allSelected: false,
          allUnselected: nextValue.length === 0,
          selectedState: _.mapValues(_.keyBy(nextValue), () => true)
        });

        return;
      }
    }

    const oldSelected = getSelectedIds(prevState.selectedState, { parseIds: false }).sort();
    const newSelected = this.getSelectedIds().sort();

    if (!_.isEqual(oldSelected, newSelected)) {
      if (!_.isEqual(newSelected, normalizeShelfIds(this.props.value).sort())) {
        onChange({
          name,
          value: newSelected
        });
      }
    }
  }

  //
  // Control

  getSelectedIds = () => {
    return getSelectedIds(this.state.selectedState, { parseIds: false });
  };

  //
  // Listeners

  onSelectAllChange = ({ value }) => {
    this.setState((state, props) => {
      const selectedState = { ...state.selectedState };

      (props.items || []).forEach((item) => {
        if (!(item.id in selectedState)) {
          selectedState[item.id] = false;
        }
      });

      return selectAll(selectedState, value);
    });
  };

  onSelectedChange = ({ id, value, shiftKey = false }) => {
    // TableSelectCell sends `value: null` on unmount to let tables clean up their selection state.
    // For a form input, that would incorrectly clear the persisted selection (and can write an
    // empty value back into Redux right as the modal closes).
    if (value == null) {
      return;
    }

    this.setState((state, props) => {
      const items = props.items || [];
      const knownIds = _.keyBy(items, 'id');
      const displayItems = [...items];

      Object.keys(state.selectedState).forEach((selectedId) => {
        if (!knownIds[selectedId]) {
          displayItems.push({ id: selectedId, name: selectedId });
        }
      });

      return toggleSelected(state, displayItems, id, value, shiftKey);
    });
  };

  onNewShelfNameChange = ({ value }) => {
    this.setState({ newShelfName: value });
  };

  onAddShelfPress = () => {
    const shelf = (this.state.newShelfName || '').trim();
    if (!shelf) {
      return;
    }

    this.setState((state) => {
      return {
        ...state,
        allSelected: false,
        allUnselected: false,
        newShelfName: '',
        selectedState: {
          ...state.selectedState,
          [shelf]: true
        }
      };
    });
  };

  //
  // Render

  render() {
    const {
      className,
      helptext,
      items,
      user,
      isFetching,
      isPopulated,
      error
    } = this.props;

    const {
      allSelected,
      allUnselected,
      selectedState,
      newShelfName
    } = this.state;

    const displayItems = [...items];
    const knownIds = _.keyBy(items, 'id');

    Object.keys(selectedState).forEach((id) => {
      if (!knownIds[id]) {
        displayItems.push({ id, name: id });
      }
    });

    const showManualAdd = !isFetching && (!!error || (isPopulated && !items.length));

    return (
      <div className={className}>
        {
          isFetching &&
            <LoadingIndicator />
        }

        {
          !isPopulated && !isFetching && !error &&
            <div>
              {translate('BookshelfInputEnterUserIdPrompt')}
            </div>
        }

        {
          !isFetching && !!error &&
            <div>
              {translate('BookshelfInputFetchError')}
            </div>
        }

        {
          isPopulated && !isFetching && !user &&
            <div>
              {translate('BookshelfInputFetchError')}
            </div>
        }

        {
          isPopulated && !isFetching && user && !items.length &&
            <div>
              {translate('BookshelfInputNoShelvesForUser', { user })}
            </div>
        }

        {
          !isFetching && !!displayItems.length &&
            <div className={className}>
              {helptext}
              <Table
                columns={columns}
                selectAll={true}
                allSelected={allSelected}
                allUnselected={allUnselected}
                onSelectAllChange={this.onSelectAllChange}
              >
                <TableBody>
                  {
                    displayItems.map((item) => {
                      return (
                        <TableRow
                          key={item.id}
                        >
                          <TableSelectCell
                            id={item.id}
                            isSelected={!!selectedState[item.id]}
                            onSelectedChange={this.onSelectedChange}
                          />

                          <TableRowCell
                            className={styles.relativePath}
                            title={item.name}
                          >
                            {item.name}
                          </TableRowCell>
                        </TableRow>
                      );
                    })
                  }
                </TableBody>
              </Table>
            </div>
        }

        {
          showManualAdd &&
            <div className={styles.manualAddRow}>
              <TextInput
                name="newShelfName"
                value={newShelfName}
                placeholder={translate('BookshelfInputManualAddPlaceholder')}
                className={styles.manualAddInput}
                hasButton={true}
                onChange={this.onNewShelfNameChange}
              />

              <FormInputButton
                isLastButton={true}
                onPress={this.onAddShelfPress}
              >
                {translate('Add')}
              </FormInputButton>
            </div>
        }
      </div>
    );
  }
}

BookshelfInput.propTypes = {
  className: PropTypes.string.isRequired,
  name: PropTypes.string.isRequired,
  value: PropTypes.oneOfType([
    PropTypes.arrayOf(PropTypes.oneOfType([PropTypes.number, PropTypes.string])),
    PropTypes.string
  ]).isRequired,
  helptext: PropTypes.string.isRequired,
  user: PropTypes.string.isRequired,
  items: PropTypes.arrayOf(PropTypes.shape(tagShape)).isRequired,
  hasError: PropTypes.bool,
  hasWarning: PropTypes.bool,
  isFetching: PropTypes.bool.isRequired,
  isPopulated: PropTypes.bool.isRequired,
  error: PropTypes.any,
  onChange: PropTypes.func.isRequired
};

BookshelfInput.defaultProps = {
  className: styles.bookshelfInputWrapper,
  inputClassName: styles.input,
  isPopulated: false
};

export default BookshelfInput;
