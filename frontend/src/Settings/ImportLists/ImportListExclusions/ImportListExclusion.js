import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Icon from 'Components/Icon';
import Link from 'Components/Link/Link';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import TableSelectCell from 'Components/Table/Cells/TableSelectCell';
import TableRow from 'Components/Table/TableRow';
import { icons, kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import EditImportListExclusionModalConnector from './EditImportListExclusionModalConnector';
import styles from './ImportListExclusion.css';

class ImportListExclusion extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isEditImportListExclusionModalOpen: false,
      isDeleteImportListExclusionModalOpen: false
    };
  }

  //
  // Listeners

  onEditImportListExclusionPress = () => {
    this.setState({ isEditImportListExclusionModalOpen: true });
  };

  onEditImportListExclusionModalClose = () => {
    this.setState({ isEditImportListExclusionModalOpen: false });
  };

  onDeleteImportListExclusionPress = () => {
    this.setState({
      isEditImportListExclusionModalOpen: false,
      isDeleteImportListExclusionModalOpen: true
    });
  };

  onDeleteImportListExclusionModalClose = () => {
    this.setState({ isDeleteImportListExclusionModalOpen: false });
  };

  onConfirmDeleteImportListExclusion = () => {
    this.props.onConfirmDeleteImportListExclusion(this.props.id);
  };

  //
  // Render

  render() {
    const {
      id,
      authorName,
      foreignId,
      isSelected,
      onSelectedChange
    } = this.props;

    return (
      <TableRow>
        <TableSelectCell
          id={id}
          isSelected={isSelected}
          onSelectedChange={onSelectedChange}
        />

        <TableRowCell className={styles.foreignId}>{foreignId}</TableRowCell>
        <TableRowCell className={styles.name}>{authorName}</TableRowCell>

        <TableRowCell className={styles.actions}>
          <Link
            onPress={this.onEditImportListExclusionPress}
          >
            <Icon name={icons.EDIT} />
          </Link>
        </TableRowCell>

        <EditImportListExclusionModalConnector
          id={id}
          isOpen={this.state.isEditImportListExclusionModalOpen}
          onModalClose={this.onEditImportListExclusionModalClose}
          onDeleteImportListExclusionPress={this.onDeleteImportListExclusionPress}
        />

        <ConfirmModal
          isOpen={this.state.isDeleteImportListExclusionModalOpen}
          kind={kinds.DANGER}
          title={translate('DeleteImportListExclusion')}
          message={translate('DeleteImportListExclusionMessageText')}
          confirmLabel={translate('Delete')}
          onConfirm={this.onConfirmDeleteImportListExclusion}
          onCancel={this.onDeleteImportListExclusionModalClose}
        />
      </TableRow>
    );
  }
}

ImportListExclusion.propTypes = {
  id: PropTypes.number.isRequired,
  authorName: PropTypes.string.isRequired,
  foreignId: PropTypes.string.isRequired,
  isSelected: PropTypes.bool.isRequired,
  onSelectedChange: PropTypes.func.isRequired,
  onConfirmDeleteImportListExclusion: PropTypes.func.isRequired
};

export default ImportListExclusion;
