import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import * as commandNames from 'Commands/commandNames';
import SelectInput from 'Components/Form/SelectInput';
import SpinnerButton from 'Components/Link/SpinnerButton';
import PageContentFooter from 'Components/Page/PageContentFooter';
import { kinds } from 'Helpers/Props';
import { executeCommand } from 'Store/Actions/commandActions';
import { fetchRootFolders } from 'Store/Actions/Settings/rootFolders';
import createCommandExecutingSelector from 'Store/Selectors/createCommandExecutingSelector';
import translate from 'Utilities/String/translate';
import CalibrePushModal from 'Calibre/CalibrePushModal';
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
      isTagsModalOpen: false,
      isConfirmMoveModalOpen: false,
      destinationRootFolder: null
    };
  }

  componentDidMount() {
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

  //
  // Render

  render() {
    const {
      bookIds,
      selectedCount,
      isSaving,
      isDeleting,
      isPushingToCalibre,
      showPushToCalibre
    } = this.props;

    const {
      monitored,
      isDeleteBookModalOpen,
      isCalibrePushModalOpen
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
                showPushToCalibre ?
                  <SpinnerButton
                    className={styles.organizeSelectedButton}
                    kind={kinds.WARNING}
                    isSpinning={isPushingToCalibre}
                    isDisabled={!selectedCount || isPushingToCalibre}
                    onPress={this.onPushToCalibrePress}
                  >
                    {translate('PushMetadataToCalibre')}
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

      </PageContentFooter>
    );
  }
}

BookEditorFooter.propTypes = {
  bookIds: PropTypes.arrayOf(PropTypes.number).isRequired,
  selectedCount: PropTypes.number.isRequired,
  isSaving: PropTypes.bool.isRequired,
  saveError: PropTypes.object,
  isDeleting: PropTypes.bool.isRequired,
  deleteError: PropTypes.object,
  isPushingToCalibre: PropTypes.bool.isRequired,
  showPushToCalibre: PropTypes.bool.isRequired,
  executeCommand: PropTypes.func.isRequired,
  fetchRootFolders: PropTypes.func.isRequired,
  onSaveSelected: PropTypes.func.isRequired
};

const selectIsPushingToCalibre = createCommandExecutingSelector(commandNames.PUSH_CALIBRE_METADATA);

function mapStateToProps(state) {
  return {
    isPushingToCalibre: selectIsPushingToCalibre(state),
    showPushToCalibre: state.settings.rootFolders.items.some((f) => f.isCalibreLibrary)
  };
}

export default connect(mapStateToProps, { executeCommand, fetchRootFolders })(BookEditorFooter);
