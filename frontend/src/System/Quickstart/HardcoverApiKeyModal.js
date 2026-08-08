import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import Alert from 'Components/Alert';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import Button from 'Components/Link/Button';
import Link from 'Components/Link/Link';
import SpinnerButton from 'Components/Link/SpinnerButton';
import Modal from 'Components/Modal/Modal';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { inputTypes, kinds, sizes } from 'Helpers/Props';
import { fetchHardcoverConfig } from 'Store/Actions/settingsActions';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import translate from 'Utilities/String/translate';
import styles from './HardcoverApiKeyModal.css';

class HardcoverApiKeyModal extends Component {
  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      apiKey: '',
      isSaving: false,
      saveError: null
    };
  }

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    this.setState({ [name]: value });
  };

  onSavePress = () => {
    const { apiKey } = this.state;

    if (!apiKey.trim()) {
      this.setState({ saveError: 'API key is required' });
      return;
    }

    this.setState({
      isSaving: true,
      saveError: null
    });

    // Clean up the API key - remove whitespace and handle Bearer prefix
    let cleanedKey = apiKey.trim();
    if (cleanedKey.startsWith('Bearer ')) {
      cleanedKey = cleanedKey.substring(7);
    }

    const request = createAjaxRequest({
      url: '/config/hardcover',
      method: 'POST',
      dataType: 'json',
      contentType: 'application/json',
      data: JSON.stringify({ token: cleanedKey })
    });

    request.request.done((data) => {
      this.setState({
        isSaving: false,
        apiKey: ''
      });

      this.props.fetchHardcoverConfig();

      if (this.props.onConnectionSuccess) {
        this.props.onConnectionSuccess();
      }

      this.props.onModalClose();
    });

    request.request.fail((xhr) => {
      let errorMessage = 'Failed to save API key';

      if (Array.isArray(xhr?.responseJSON)) {
        const messages = xhr.responseJSON
          .map((e) => e?.errorMessage || e?.message)
          .filter(Boolean);

        errorMessage = messages.length ? messages.join(' ') : errorMessage;
      } else if (xhr.responseJSON && typeof xhr.responseJSON === 'object') {
        errorMessage = xhr.responseJSON.error || xhr.responseJSON.message || errorMessage;
      } else if (xhr.status === 400) {
        errorMessage = 'Invalid API key. Please check your token and try again.';
      } else if (xhr.status >= 500) {
        errorMessage = 'Hardcover\'s API is currently unavailable. Please try again later.';
      }

      this.setState({
        isSaving: false,
        saveError: errorMessage
      });
    });
  };

  onModalClose = () => {
    this.setState({
      apiKey: '',
      isSaving: false,
      saveError: null
    });

    this.props.onModalClose();
  };

  //
  // Render

  render() {
    const {
      isOpen
    } = this.props;

    const {
      apiKey,
      isSaving,
      saveError
    } = this.state;

    return (
      <Modal
        isOpen={isOpen}
        size={sizes.MEDIUM}
      >
        <ModalContent onModalClose={this.onModalClose}>
          <ModalHeader>
            {translate('ConfigureHardcoverApi')}
          </ModalHeader>

          <ModalBody>
            <Form>
              <Alert
                className={styles.intro}
                kind={kinds.INFO}
              >
                {translate('HardcoverConnectIntro')}
              </Alert>

              <FormGroup>
                <FormLabel>{translate('ApiKey')}</FormLabel>
                <div className={styles.inputStack}>
                  <FormInputGroup
                    type={inputTypes.PASSWORD}
                    name="apiKey"
                    value={apiKey}
                    placeholder={translate('HardcoverApiKeyPlaceholder')}
                    onChange={this.onInputChange}
                  />
                  <div className={styles.helpText}>
                    {translate('HardcoverGetApiKeyFrom')}{' '}
                    <Link to="https://hardcover.app/account/api" target="_blank">
                      {translate('HardcoverApiSettingsLinkText')}
                    </Link>
                  </div>
                </div>
              </FormGroup>

              {saveError && (
                <FormGroup>
                  <Alert kind={kinds.DANGER} className={styles.saveError}>
                    {saveError}
                  </Alert>
                </FormGroup>
              )}
            </Form>
          </ModalBody>

          <ModalFooter>
            <Button
              onPress={this.onModalClose}
              kind={kinds.DEFAULT}
            >
              {translate('Cancel')}
            </Button>

            <SpinnerButton
              kind={kinds.PRIMARY}
              onPress={this.onSavePress}
              isDisabled={isSaving || !apiKey.trim()}
              isSpinning={isSaving}
            >
              {translate('Save')}
            </SpinnerButton>
          </ModalFooter>
        </ModalContent>
      </Modal>
    );
  }
}

HardcoverApiKeyModal.propTypes = {
  fetchHardcoverConfig: PropTypes.func.isRequired,
  isOpen: PropTypes.bool.isRequired,
  onModalClose: PropTypes.func.isRequired,
  onConnectionSuccess: PropTypes.func
};

const mapDispatchToProps = {
  fetchHardcoverConfig
};

export default connect(null, mapDispatchToProps)(HardcoverApiKeyModal);
