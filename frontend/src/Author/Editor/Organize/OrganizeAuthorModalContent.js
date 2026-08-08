import PropTypes from 'prop-types';
import React from 'react';
import Alert from 'Components/Alert';
import Icon from 'Components/Icon';
import Button from 'Components/Link/Button';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { icons, kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './OrganizeAuthorModalContent.css';

function getScopeLabel(selectedMediaType) {
  if (selectedMediaType === 'ebook') {
    return 'eBook files';
  }

  if (selectedMediaType === 'audiobook') {
    return 'audiobook files';
  }

  return 'files';
}

function OrganizeAuthorModalContent(props) {
  const {
    authorNames,
    selectedMediaType,
    onModalClose,
    onOrganizeAuthorPress
  } = props;

  const scope = getScopeLabel(selectedMediaType);
  return (
    <ModalContent onModalClose={onModalClose}>
      <ModalHeader>
        {translate('OrganizeSelectedAuthor')}
      </ModalHeader>

      <ModalBody>
        <Alert>
          {translate('OrganizeTipPreviewRename')}
          <Icon
            className={styles.renameIcon}
            name={icons.ORGANIZE}
          />
        </Alert>

        <div className={styles.message}>
          {translate('OrganizeConfirmAll', { scope, count: authorNames.length })}
        </div>

        <ul>
          {
            authorNames.map((authorName) => {
              return (
                <li key={authorName}>
                  {authorName}
                </li>
              );
            })
          }
        </ul>
      </ModalBody>

      <ModalFooter>
        <Button onPress={onModalClose}>
          {translate('Cancel')}
        </Button>

        <Button
          kind={kinds.DANGER}
          onPress={onOrganizeAuthorPress}
        >
          {translate('Organize')}
        </Button>
      </ModalFooter>
    </ModalContent>
  );
}

OrganizeAuthorModalContent.propTypes = {
  authorNames: PropTypes.arrayOf(PropTypes.string).isRequired,
  selectedMediaType: PropTypes.oneOf(['all', 'audiobook', 'ebook']),
  onModalClose: PropTypes.func.isRequired,
  onOrganizeAuthorPress: PropTypes.func.isRequired
};

export default OrganizeAuthorModalContent;
