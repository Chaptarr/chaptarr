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
      selectedState: {}
    };
  }

  componentDidUpdate(prevProps) {
    if (
      prevProps.isDeleting &&
      !this.props.isDeleting &&
      !this.props.deleteError
    ) {
      this.setState({ selectedState: {} });
    }
  }

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
    const selectedState = this.props.items.reduce((acc, item) => {
      acc[item.id] = value;

      return acc;
    }, {});

    this.setState({ selectedState });
  };

  onSelectedChange = ({ id, value }) => {
    this.setState((state) => {
      const selectedState = {
        ...state.selectedState
      };

      if (value == null) {
        delete selectedState[id];
      } else {
        selectedState[id] = value;
      }

      return { selectedState };
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

    const selectedIds = getSelectedIds(this.state.selectedState);
    const selectedCount = selectedIds.length;
    const allSelected = items.length > 0 && items.every((item) => {
      return this.state.selectedState[item.id];
    });
    const allUnselected = items.every((item) => {
      return !this.state.selectedState[item.id];
    });

    return (
      <FieldSet legend={translate('ImportListExclusions')}>
        <PageSectionContent
          errorMessage={translate('UnableToLoadImportListExclusions')}
          {...otherProps}
        >
          <Table
            selectAll={true}
            allSelected={allSelected}
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
                      isSelected={this.state.selectedState[item.id] || false}
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
