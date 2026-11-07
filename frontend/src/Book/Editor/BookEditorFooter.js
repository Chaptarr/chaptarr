import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import CalibrePushModal from 'Calibre/CalibrePushModal';
import * as commandNames from 'Commands/commandNames';
import SelectInput from 'Components/Form/SelectInput';
import SpinnerButton from 'Components/Link/SpinnerButton';
import PageContentFooter from 'Components/Page/PageContentFooter';
import GrimmoryPushModal from 'Grimmory/GrimmoryPushModal';
import { kinds } from 'Helpers/Props';
import { executeCommand } from 'Store/Actions/commandActions';
import { fetchRootFolders } from 'Store/Actions/Settings/rootFolders';
import { fetchNotifications } from 'Store/Actions/settingsActions';
import createCommandExecutingSelector from 'Store/Selectors/createCommandExecutingSelector';
import translate from 'Utilities/String/translate';
import BookEditorFooterLabel from './BookEditorFooterLabel';
import DeleteBookModal from './Delete/DeleteBookModal';
import styles from './BookEditorFooter.css';

const NO_CHANGE = 'noChange';

class BookEditorFooter extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      monitored: NO_CHANGE,
      rootFolderPath: NO_CHANGE,
      savingTags: false,
      isDeleteBookModalOpen: false,
      isCalibrePushModalOpen: false,
      isGrimmoryPushModalOpen: false,
      isTagsModalOpen: false,
      isConfirmMoveModalOpen: false,
      destinationRootFolder: null
    };
  }

  componentDidMount() {
    this.props.fetchNotifications();
    this.props.fetchRootFolders();
  }

  componentDidUpdate(prevProps) {
    const {
      isSaving,
      saveError
    } = this.props;

    if (prevProps.isSaving && !isSaving && !saveError) {
      this.setState({
        monitored: NO_CHANGE,
        rootFolderPath: NO_CHANGE,
        savingTags: false
      });
    }
  }

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    this.setState({ [name]: value });

    if (value === NO_CHANGE) {
      return;
    }

    switch (name) {
      case 'monitored':
        this.props.onSaveSelected({ [name]: value === 'monitored' });
        break;
      default:
        this.props.onSaveSelected({ [name]: value });
    }
  };

  onResendToCalibrePress = () => {
    this.props.executeCommand({
      name: commandNames.REPUSH_BOOK,
      bookIds: this.props.bookIds
    });
  };

  onPushToCalibrePress = () => {
    this.setState({ isCalibrePushModalOpen: true });
  };

  onCalibrePushModalClose = () => {
    this.setState({ isCalibrePushModalOpen: false });
  };

  onCalibrePushConfirmed = (fields) => {
    this.setState({ isCalibrePushModalOpen: false });

    this.props.executeCommand({
      name: commandNames.PUSH_CALIBRE_METADATA,
      bookIds: this.props.bookIds,
      fields
    });
  };

  onDeleteSelectedPress = () => {
    this.setState({ isDeleteBookModalOpen: true });
  };

  onDeleteBookModalClose = () => {
    this.setState({ isDeleteBookModalOpen: false });
  };

  onPushToGrimmoryPress = () => {
    this.setState({ isGrimmoryPushModalOpen: true });
  };

  onGrimmoryPushModalClose = () => {
    this.setState({ isGrimmoryPushModalOpen: false });
  };

  onGrimmoryPushConfirmed = (fields) => {
    this.setState({ isGrimmoryPushModalOpen: false });

    this.props.executeCommand({
      name: commandNames.PUSH_GRIMMORY_METADATA,
      bookIds: this.props.bookIds,
      fields
    });
  };

  //
  // Render

  render() {
    const {
      bookIds,
      selectedCount,
      isSaving,
      isDeleting,
      isResendingToCalibre,
      showResendToCalibre,
      isPushingToCalibre,
      showPushToCalibre,
      isPushingToGrimmory,
      showPushToGrimmory
    } = this.props;

    const {
      monitored,
      isDeleteBookModalOpen,
      isCalibrePushModalOpen,
      isGrimmoryPushModalOpen
    } = this.state;

    const monitoredOptions = [
      { key: NO_CHANGE, value: translate('NoChange'), isDisabled: true },
      { key: 'monitored', value: translate('Monitored') },
      { key: 'unmonitored', value: translate('Unmonitored') }
    ];

    return (
      <PageContentFooter>
        <div className={styles.inputContainer}>
          <BookEditorFooterLabel
            label={translate('MonitorBook')}
            isSaving={isSaving && monitored !== NO_CHANGE}
          />

          <SelectInput
            name="monitored"
            value={monitored}
            values={monitoredOptions}
            isDisabled={!selectedCount}
            onChange={this.onInputChange}
          />
        </div>

        <div className={styles.buttonContainer}>
          <div className={styles.buttonContainerContent}>
            <BookEditorFooterLabel
              label={translate('SelectedCountBooksSelectedInterp', [selectedCount])}
              isSaving={false}
            />

            <div className={styles.buttons}>
              {
                showResendToCalibre &&
                  <SpinnerButton
                    className={styles.organizeSelectedButton}
                    kind={kinds.WARNING}
                    isSpinning={isResendingToCalibre}
                    isDisabled={!selectedCount || isResendingToCalibre}
                    onPress={this.onResendToCalibrePress}
                  >
                    {translate('ResendToCalibre')}
                  </SpinnerButton>
              }

              {
                showPushToCalibre &&
                  <SpinnerButton
                    className={styles.organizeSelectedButton}
                    kind={kinds.WARNING}
                    isSpinning={isPushingToCalibre}
                    isDisabled={!selectedCount || isPushingToCalibre}
                    onPress={this.onPushToCalibrePress}
                  >
                    {translate('PushMetadataToCalibre')}
                  </SpinnerButton>
              }

              {
                showPushToGrimmory ?
                  <SpinnerButton
                    className={styles.organizeSelectedButton}
                    kind={kinds.WARNING}
                    isSpinning={isPushingToGrimmory}
                    isDisabled={!selectedCount || isPushingToGrimmory}
                    onPress={this.onPushToGrimmoryPress}
                  >
                    {translate('PushChaptarrMetadataToGrimmory')}
                  </SpinnerButton> :
                  null
              }

              <SpinnerButton
                className={styles.deleteSelectedButton}
                kind={kinds.DANGER}
                isSpinning={isDeleting}
                isDisabled={!selectedCount || isDeleting}
                onPress={this.onDeleteSelectedPress}
              >
                {translate('Delete')}
              </SpinnerButton>
            </div>
          </div>
        </div>

        <CalibrePushModal
          isOpen={isCalibrePushModalOpen}
          bookCount={selectedCount}
          onPushPress={this.onCalibrePushConfirmed}
          onModalClose={this.onCalibrePushModalClose}
        />

        <DeleteBookModal
          isOpen={isDeleteBookModalOpen}
          bookIds={bookIds}
          onModalClose={this.onDeleteBookModalClose}
        />

        <GrimmoryPushModal
          isOpen={isGrimmoryPushModalOpen}
          bookCount={selectedCount}
          onPushPress={this.onGrimmoryPushConfirmed}
          onModalClose={this.onGrimmoryPushModalClose}
        />

      </PageContentFooter>
    );
  }
}

BookEditorFooter.propTypes = {
  isResendingToCalibre: PropTypes.bool.isRequired,
  showResendToCalibre: PropTypes.bool.isRequired,
  executeCommand: PropTypes.func.isRequired,
  fetchNotifications: PropTypes.func.isRequired,
  bookIds: PropTypes.arrayOf(PropTypes.number).isRequired,
  selectedCount: PropTypes.number.isRequired,
  isSaving: PropTypes.bool.isRequired,
  saveError: PropTypes.object,
  isDeleting: PropTypes.bool.isRequired,
  deleteError: PropTypes.object,
  isPushingToCalibre: PropTypes.bool.isRequired,
  showPushToCalibre: PropTypes.bool.isRequired,
  fetchRootFolders: PropTypes.func.isRequired,
  isPushingToGrimmory: PropTypes.bool.isRequired,
  showPushToGrimmory: PropTypes.bool.isRequired,
  onSaveSelected: PropTypes.func.isRequired
};

const selectIsResendingToCalibre = createCommandExecutingSelector(commandNames.REPUSH_BOOK);
const selectIsPushingToCalibre = createCommandExecutingSelector(commandNames.PUSH_CALIBRE_METADATA);
const selectIsPushingToGrimmory = createCommandExecutingSelector(commandNames.PUSH_GRIMMORY_METADATA);

function mapStateToProps(state) {
  return {
    isResendingToCalibre: selectIsResendingToCalibre(state),
    showResendToCalibre: state.settings.notifications.items.some((n) => n.implementation === 'CalibreContentServer'),
    isPushingToCalibre: selectIsPushingToCalibre(state),
    showPushToCalibre: state.settings.rootFolders.items.some((f) => f.isCalibreLibrary),
    isPushingToGrimmory: selectIsPushingToGrimmory(state),
    showPushToGrimmory: state.settings.notifications.items.some((n) => n.implementation === 'Grimmory')
  };
}

export default connect(mapStateToProps, { executeCommand, fetchNotifications, fetchRootFolders })(BookEditorFooter);
