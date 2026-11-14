import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageToolbar from 'Components/Page/Toolbar/PageToolbar';
import PageToolbarButton from 'Components/Page/Toolbar/PageToolbarButton';
import PageToolbarSection from 'Components/Page/Toolbar/PageToolbarSection';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import TableOptionsModalWrapper from 'Components/Table/TableOptions/TableOptionsModalWrapper';
import TablePager from 'Components/Table/TablePager';
import { align, icons, kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import AuthorsRow from './AuthorsRow';

class Authors extends Component {
  state = { confirmation: null };

  componentDidUpdate(prevProps) {
    if (this.props.saveError && !prevProps.saveError && this.scroller) {
      this.scroller.scrollTop = 0;
    }
  }

  scroller = null;

  registerScroller = (scroller) => {
    this.scroller = scroller;
  };

  onCancelPress = (id) => this.setState({ confirmation: { id, name: 'cancel' } });

  onDeletePress = (id) => this.setState({ confirmation: { id, name: 'delete' } });

  onConfirmationClose = () => this.setState({ confirmation: null });

  onConfirmationConfirmed = () => {
    const { id, name } = this.state.confirmation;
    if (name === 'delete') {
      this.props.onDeleteConfirmed(id);
    } else {
      this.props.onCancelConfirmed(id);
    }
    this.onConfirmationClose();
  };

  render() {
    const { isFetching, isPopulated, error, saveError, items, columns, totalRecords, pendingAction, onReloadPress, onRetryPress, ...tableProps } = this.props;
    const { confirmation } = this.state;
    const confirmedAuthor = items.find((item) => item.id === confirmation?.id);
    const isDelete = confirmation?.name === 'delete';

    return (
      <PageContent title={translate('AuthorRequests')}>
        <PageToolbar>
          <PageToolbarSection>
            <PageToolbarButton
              label={translate('Reload')}
              iconName={icons.REFRESH}
              isSpinning={isFetching}
              onPress={onReloadPress}
            />
          </PageToolbarSection>

          <PageToolbarSection alignContent={align.RIGHT}>
            <TableOptionsModalWrapper columns={columns} {...tableProps}>
              <PageToolbarButton label={translate('Options')} iconName={icons.TABLE} />
            </TableOptionsModalWrapper>
          </PageToolbarSection>
        </PageToolbar>

        <PageContentBody registerScroller={this.registerScroller}>
          {isFetching && !isPopulated && <LoadingIndicator />}

          {!isFetching && error && <Alert kind={kinds.DANGER}>{translate('ErrorFetchingPendingAuthorImports')}</Alert>}

          {saveError && <Alert kind={kinds.DANGER}>{translate('ErrorUpdatingPendingAuthorImport')}</Alert>}

          {isPopulated && !isFetching && !error && totalRecords === 0 && <Alert kind={kinds.INFO}>{translate('NoPendingAuthorImports')}</Alert>}

          {
            isPopulated && !error && totalRecords > 0 &&
              <div>
                <Table columns={columns} {...tableProps}>
                  <TableBody>
                    {items.map((item) => (
                      <AuthorsRow
                        key={item.id}
                        {...item}
                        columns={columns}
                        pendingAction={pendingAction}
                        onRetryPress={onRetryPress}
                        onCancelPress={this.onCancelPress}
                        onDeletePress={this.onDeletePress}
                      />
                    ))}
                  </TableBody>
                </Table>

                <TablePager totalRecords={totalRecords} isFetching={isFetching}
                  {...tableProps}
                />
              </div>
          }

          <ConfirmModal
            isOpen={!!confirmation}
            kind={kinds.DANGER}
            title={translate(isDelete ? 'DeletePendingAuthorImport' : 'CancelPendingAuthorImport')}
            message={translate(isDelete ? 'DeletePendingAuthorImportMessage' : 'CancelPendingAuthorImportMessage', { authorName: confirmedAuthor?.authorName || confirmedAuthor?.providerId || translate('Unknown') })}
            confirmLabel={translate(isDelete ? 'Delete' : 'CancelImport')}
            onConfirm={this.onConfirmationConfirmed}
            onCancel={this.onConfirmationClose}
          />
        </PageContentBody>
      </PageContent>
    );
  }
}

Authors.propTypes = {
  isFetching: PropTypes.bool.isRequired,
  isPopulated: PropTypes.bool.isRequired,
  error: PropTypes.object,
  saveError: PropTypes.object,
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  columns: PropTypes.arrayOf(PropTypes.object).isRequired,
  totalRecords: PropTypes.number,
  pendingAction: PropTypes.object,
  onReloadPress: PropTypes.func.isRequired,
  onRetryPress: PropTypes.func.isRequired,
  onCancelConfirmed: PropTypes.func.isRequired,
  onDeleteConfirmed: PropTypes.func.isRequired
};

export default Authors;
