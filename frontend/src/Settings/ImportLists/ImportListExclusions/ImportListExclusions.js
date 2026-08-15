import PropTypes from 'prop-types';
import React, { Component } from 'react';
import FieldSet from 'Components/FieldSet';
import IconButton from 'Components/Link/IconButton';
import SpinnerButton from 'Components/Link/SpinnerButton';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import PageSectionContent from 'Components/Page/PageSectionContent';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import TableRow from 'Components/Table/TableRow';
import { icons, kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import getSelectedIds from 'Utilities/Table/getSelectedIds';
import selectAll from 'Utilities/Table/selectAll';
import toggleSelected from 'Utilities/Table/toggleSelected';
import EditImportListExclusionModalConnector from './EditImportListExclusionModalConnector';
import ImportListExclusion from './ImportListExclusion';
import styles from './ImportListExclusions.css';

const columns = [
  {
    name: 'foreignId',
    className: styles.foreignId,
    label: () => translate('ForeignId'),
    isVisible: true,
    isSortable: false
  },
  {
    name: 'name',
    className: styles.name,
    label: () => translate('Name'),
    isVisible: true,
    isSortable: false
  },
  {
    className: styles.actions,
    name: 'actions',
    label: '',
    isVisible: true,
    isSortable: false
  }
];

class ImportListExclusions extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isAddImportListExclusionModalOpen: false,
      isDeleteSelectedImportListExclusionsModalOpen: false,
      allSelected: false,
      allUnselected: true,
      lastToggled: null,
      selectedState: {}
    };
  }

  componentDidUpdate(prevProps) {
    if (
      prevProps.isDeleting &&
      !this.props.isDeleting &&
      !this.props.deleteError
    ) {
      this.setState(selectAll(this.getVisibleSelectedState(), false));
    }
  }

  //
  // Control

  getVisibleSelectedState = () => {
    return this.props.items.reduce((acc, item) => {
      acc[item.id] = false;

      return acc;
    }, {});
  };

  //
  // Listeners

  onAddImportListExclusionPress = () => {
    this.setState({ isAddImportListExclusionModalOpen: true });
  };

  onModalClose = () => {
    this.setState({ isAddImportListExclusionModalOpen: false });
  };

  onDeleteSelectedImportListExclusionsPress = () => {
    this.setState({ isDeleteSelectedImportListExclusionsModalOpen: true });
  };

  onDeleteSelectedImportListExclusionsModalClose = () => {
    this.setState({ isDeleteSelectedImportListExclusionsModalOpen: false });
  };

  onConfirmDeleteSelectedImportListExclusions = () => {
    const selectedIds = getSelectedIds(this.state.selectedState);

    this.props.onConfirmDeleteSelectedImportListExclusions(selectedIds);
    this.setState({ isDeleteSelectedImportListExclusionsModalOpen: false });
  };

  onSelectAllChange = ({ value }) => {
    this.setState(selectAll(this.getVisibleSelectedState(), value));
  };

  // TableSelectCell omits shiftKey when registering or unregistering rows.
  // Real checkbox interactions always provide a boolean, so only they may
  // establish a range-selection anchor.
  onSelectedChange = ({ id, value, shiftKey }) => {
    this.setState((state) => {
      const nextState = toggleSelected(state, this.props.items, id, value, shiftKey);

      if (typeof shiftKey !== 'boolean') {
        nextState.lastToggled = state.lastToggled;
      }

      return nextState;
    });
  };

  //
  // Render

  render() {
    const {
      items,
      isDeleting,
      onConfirmDeleteImportListExclusion,
      ...otherProps
    } = this.props;

    const {
      allSelected,
      allUnselected,
      selectedState
    } = this.state;
    const selectedIds = getSelectedIds(selectedState);
    const selectedCount = selectedIds.length;

    return (
      <FieldSet legend={translate('ImportListExclusions')}>
        <PageSectionContent
          errorMessage={translate('UnableToLoadImportListExclusions')}
          {...otherProps}
        >
          <Table
            selectAll={true}
            allSelected={items.length > 0 && allSelected}
            allUnselected={allUnselected}
            columns={columns}
            canModifyColumns={false}
            onSelectAllChange={this.onSelectAllChange}
          >
            <TableBody>
              {
                items.map((item) => {
                  return (
                    <ImportListExclusion
                      key={item.id}
                      {...item}
                      {...otherProps}
                      isSelected={selectedState[item.id] || false}
                      onSelectedChange={this.onSelectedChange}
                      onConfirmDeleteImportListExclusion={onConfirmDeleteImportListExclusion}
                    />
                  );
                })
              }

              <TableRow>
                <TableRowCell colSpan={3}>
                  <SpinnerButton
                    kind={kinds.DANGER}
                    isSpinning={isDeleting}
                    isDisabled={!selectedCount}
                    onPress={this.onDeleteSelectedImportListExclusionsPress}
                  >
                    {translate('DeleteSelected')}
                  </SpinnerButton>
                </TableRowCell>

                <TableRowCell className={styles.actions}>
                  <IconButton
                    name={icons.ADD}
                    onPress={this.onAddImportListExclusionPress}
                  />
                </TableRowCell>
              </TableRow>
            </TableBody>
          </Table>

          <EditImportListExclusionModalConnector
            isOpen={this.state.isAddImportListExclusionModalOpen}
            onModalClose={this.onModalClose}
          />

          <ConfirmModal
            isOpen={this.state.isDeleteSelectedImportListExclusionsModalOpen}
            kind={kinds.DANGER}
            title={translate('DeleteSelectedImportListExclusions')}
            message={translate('DeleteSelectedImportListExclusionsMessageText', {
              count: selectedCount
            })}
            confirmLabel={translate('Delete')}
            onConfirm={this.onConfirmDeleteSelectedImportListExclusions}
            onCancel={this.onDeleteSelectedImportListExclusionsModalClose}
          />

        </PageSectionContent>
      </FieldSet>
    );
  }
}

ImportListExclusions.propTypes = {
  isFetching: PropTypes.bool.isRequired,
  isDeleting: PropTypes.bool.isRequired,
  deleteError: PropTypes.object,
  error: PropTypes.object,
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  onConfirmDeleteImportListExclusion: PropTypes.func.isRequired,
  onConfirmDeleteSelectedImportListExclusions: PropTypes.func.isRequired
};

export default ImportListExclusions;
