import PropTypes from 'prop-types';
import React, { Component } from 'react';
import BookFormats from 'Book/BookFormats';
import BookQuality from 'Book/BookQuality';
import IndexerFlags from 'Book/IndexerFlags';
import FileDetails from 'BookFile/FileDetails';
import Icon from 'Components/Icon';
import Label from 'Components/Label';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import TableRowCellButton from 'Components/Table/Cells/TableRowCellButton';
import TableSelectCell from 'Components/Table/Cells/TableSelectCell';
import TableRow from 'Components/Table/TableRow';
import Popover from 'Components/Tooltip/Popover';
import Tooltip from 'Components/Tooltip/Tooltip';
import { icons, kinds, sizes, tooltipPositions } from 'Helpers/Props';
import SelectAuthorModal from 'InteractiveImport/Author/SelectAuthorModal';
import SelectBookModal from 'InteractiveImport/Book/SelectBookModal';
import SelectEditionModal from 'InteractiveImport/Edition/SelectEditionModal';
import SelectIndexerFlagsModal from 'InteractiveImport/IndexerFlags/SelectIndexerFlagsModal';
import SelectQualityModal from 'InteractiveImport/Quality/SelectQualityModal';
import { getMediaTypeFromExtension } from 'Utilities/MediaFile/getMediaTypeFromExtension';
import formatBytes from 'Utilities/Number/formatBytes';
import translate from 'Utilities/String/translate';
import InteractiveImportRowCellPlaceholder from './InteractiveImportRowCellPlaceholder';
import styles from './InteractiveImportRow.css';

class InteractiveImportRow extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isDetailsModalOpen: false,
      isSelectAuthorModalOpen: false,
      isSelectBookModalOpen: false,
      isSelectEditionModalOpen: false,
      isSelectQualityModalOpen: false,
      isSelectIndexerFlagsModalOpen: false
    };
  }

  componentDidMount() {
    const {
      id,
      author,
      book,
      editionId,
      quality,
      size
    } = this.props;

    const hasAuthor = !!(author && author.id > 0);
    const hasBook = !!(book && book.id > 0);

    if (
      quality &&
      size > 0 &&
      hasAuthor && hasBook && editionId
    ) {
      this.props.onSelectedChange({ id, value: true });
    }
  }

  componentDidUpdate(prevProps) {
    const {
      id,
      author,
      suggestedForeignAuthorId,
      book,
      editionId,
      quality,
      isSelected,
      onValidRowChange
    } = this.props;

    if (
      prevProps.author === author &&
      prevProps.suggestedForeignAuthorId === suggestedForeignAuthorId &&
      prevProps.book === book &&
      prevProps.editionId === editionId &&
      prevProps.quality === quality &&
      prevProps.isSelected === isSelected
    ) {
      return;
    }

    const hasLocalAuthor = !!(author && author.id > 0);
    const hasSuggestedAuthor = !!suggestedForeignAuthorId;
    const hasBook = !!(book && book.id > 0);
    const isLocalReady = !!(hasLocalAuthor && hasBook && editionId);
    const isValid = !!(quality && (isLocalReady || hasSuggestedAuthor));

    if (isSelected && !isValid) {
      onValidRowChange(id, false);
    } else {
      onValidRowChange(id, true);
    }
  }

  //
  // Control

  selectRowAfterChange = (value) => {
    const {
      id,
      isSelected
    } = this.props;

    if (!isSelected && value === true) {
      this.props.onSelectedChange({ id, value });
    }
  };

  //
  // Listeners

  onDetailsPress = () => {
    this.setState({ isDetailsModalOpen: true });
  };

  onDetailsModalClose = () => {
    this.setState({ isDetailsModalOpen: false });
  };

  onSelectAuthorPress = () => {
    this.setState({ isSelectAuthorModalOpen: true });
  };

  onSelectBookPress = () => {
    this.setState({ isSelectBookModalOpen: true });
  };

  onSelectEditionPress = () => {
    this.setState({ isSelectEditionModalOpen: true });
  };

  onSelectQualityPress = () => {
    this.setState({ isSelectQualityModalOpen: true });
  };

  onSelectIndexerFlagsPress = () => {
    this.setState({ isSelectIndexerFlagsModalOpen: true });
  };

  getMediaTypeFromQuality = (quality) => {
    const mediaType = getMediaTypeFromExtension(this.props.path);
    return mediaType || undefined;
  };

  onSelectAuthorModalClose = (changed) => {
    this.setState({ isSelectAuthorModalOpen: false });
    this.selectRowAfterChange(changed);
  };

  onSelectBookModalClose = (changed) => {
    this.setState({ isSelectBookModalOpen: false });
    this.selectRowAfterChange(changed);
  };

  onSelectEditionModalClose = (changed) => {
    this.setState({ isSelectEditionModalOpen: false });
    this.selectRowAfterChange(changed);
  };

  onSelectQualityModalClose = (changed) => {
    this.setState({ isSelectQualityModalOpen: false });
    this.selectRowAfterChange(changed);
  };

  onSelectIndexerFlagsModalClose = (changed) => {
    this.setState({ isSelectIndexerFlagsModalOpen: false });
    this.selectRowAfterChange(changed);
  };

  //
  // Render

  render() {
    const {
      id,
      allowAuthorChange,
      path,
      author,
      suggestedForeignAuthorId,
      suggestedAuthorName,
      suggestedForeignBookId,
      suggestedBookTitle,
      suggestedForeignEditionId,
      suggestedEditionTitle,
      book,
      edition,
      editionId,
      foreignEditionId,
      quality,
      releaseGroup,
      size,
      customFormats = [],
      indexerFlags,
      rejections,
      warnings = [],
      columns,
      additionalFile,
      isSelected,
      isReprocessing,
      onSelectedChange,
      tags
    } = this.props;

    const {
      isDetailsModalOpen,
      isSelectAuthorModalOpen,
      isSelectBookModalOpen,
      isSelectEditionModalOpen,
      isSelectQualityModalOpen,
      isSelectIndexerFlagsModalOpen
    } = this.state;

    const authorName = author ? author.authorName : (suggestedAuthorName || suggestedForeignAuthorId || '');
    const hasAuthor = !!(author && author.id > 0);
    const hasSuggestedAuthor = !!suggestedForeignAuthorId;
    const hasBook = !!(book && book.id > 0);
    const hasEdition = !!((edition && edition.id > 0) || editionId > 0);
    const isLocalReady = hasAuthor && hasBook && hasEdition;
    const isMetadataSuggested = !isLocalReady && hasSuggestedAuthor;
    const suggestedBookLabel = suggestedBookTitle || suggestedForeignBookId || '';
    const suggestedEditionLabel = suggestedEditionTitle || '';
    let bookTitle = '';
    if (book) {
      bookTitle = book.disambiguation ? `${book.title} (${book.disambiguation})` : book.title;
    } else if (suggestedBookLabel) {
      bookTitle = suggestedBookLabel;
    }
    let editionTitle = '';
    if (edition) {
      editionTitle = edition.disambiguation ? `${edition.title} (${edition.disambiguation})` : edition.title;
    } else if (hasBook && editionId && foreignEditionId) {
      editionTitle = foreignEditionId;
    } else if (!hasEdition && suggestedEditionLabel) {
      editionTitle = suggestedEditionLabel;
    }

    const showAuthorPlaceholder = isSelected && !hasAuthor && !hasSuggestedAuthor;
    const showBookNumberPlaceholder = !isReprocessing && isSelected && hasAuthor && !hasBook && !suggestedBookLabel;
    const showEditionPlaceholder = isSelected && hasBook && !edition && !editionId;
    const showQualityPlaceholder = isSelected && !quality;
    const showIndexerFlagsPlaceholder = isSelected && !indexerFlags;

    const pathCellContents = (
      <div onClick={this.onDetailsPress}>
        {path}
      </div>
    );

    const pathCell = additionalFile ? (
      <Tooltip
        anchor={pathCellContents}
        tooltip='This file is already in your library for a release you are currently importing'
        position={tooltipPositions.TOP}
      />
    ) : pathCellContents;

    const fileDetails = (
      <FileDetails
        tags={tags}
        filename={path}
      />
    );

    const isIndexerFlagsColumnVisible = columns.find((c) => c.name === 'indexerFlags')?.isVisible ?? false;
    const hasRejections = !!rejections?.length;
    const hasWarnings = !!warnings?.length;
    const statusIcon = hasRejections ? icons.DANGER : icons.WARNING;
    const statusKind = hasRejections ? kinds.DANGER : kinds.WARNING;
    let statusTitle = 'Warnings';
    if (hasRejections && hasWarnings) {
      statusTitle = 'Rejections / Warnings';
    } else if (hasRejections) {
      statusTitle = translate('ReleaseRejected');
    }

    let authorCell = hasAuthor || hasSuggestedAuthor ? authorName : '';
    if (showAuthorPlaceholder) {
      authorCell = <InteractiveImportRowCellPlaceholder />;
    } else if (!hasAuthor && hasSuggestedAuthor) {
      authorCell = <span className={styles.suggestedValue}>{authorCell}</span>;
    }

    const bookCell = !hasBook && suggestedBookLabel ?
      <span className={styles.suggestedValue}>{bookTitle}</span> :
      bookTitle;

    const editionCell = !hasBook && !edition && suggestedEditionLabel ?
      <span className={styles.suggestedValue}>{editionTitle}</span> :
      (editionTitle || (!hasBook ? releaseGroup : ''));
    let bookTitleAttribute = undefined;
    if (hasAuthor) {
      bookTitleAttribute = translate('AuthorClickToChangeBook');
    } else if (suggestedBookLabel) {
      bookTitleAttribute = 'Select a local author to override the suggested book';
    }

    let editionTitleAttribute = undefined;
    if (hasBook) {
      editionTitleAttribute = translate('ClickToChangeEdition');
    } else if (suggestedEditionLabel) {
      editionTitleAttribute = 'Select a local book to override the suggested edition';
    }

    let actionLabel = 'Choose';
    let actionKind = kinds.WARNING;
    let actionTitle = 'Select a local author and book before importing';

    if (isLocalReady) {
      actionLabel = 'Local';
      actionKind = kinds.SUCCESS;
      actionTitle = 'Ready to import using your local library';
    } else if (hasAuthor && hasBook && !hasEdition) {
      actionTitle = 'Select a local edition before importing';
    } else if (isMetadataSuggested) {
      actionLabel = 'Select to Add';
      actionKind = kinds.INFO;
      actionTitle = suggestedForeignEditionId ?
        'Selecting this row explicitly adds and pins the suggested metadata edition' :
        'Selecting this row adds the suggested metadata work, then verifies it before import';
    }

    return (
      <TableRow
        className={additionalFile ? styles.additionalFile : undefined}
      >
        <TableSelectCell
          id={id}
          isSelected={isSelected}
          onSelectedChange={onSelectedChange}
        />

        <TableRowCell
          className={styles.path}
          title={path}
        >
          {pathCell}
        </TableRowCell>

        <TableRowCell className={styles.action}>
          <Label
            kind={actionKind}
            title={actionTitle}
          >
            {actionLabel}
          </Label>
        </TableRowCell>

        <TableRowCellButton
          isDisabled={!allowAuthorChange}
          title={allowAuthorChange ? translate('AllowAuthorChangeClickToChangeAuthor') : undefined}
          onPress={this.onSelectAuthorPress}
        >
          {
            authorCell
          }
        </TableRowCellButton>

        <TableRowCellButton
          isDisabled={!hasAuthor}
          title={bookTitleAttribute}
          onPress={this.onSelectBookPress}
        >
          {
            showBookNumberPlaceholder ? <InteractiveImportRowCellPlaceholder /> : bookCell
          }
        </TableRowCellButton>

        <TableRowCellButton
          isDisabled={!hasBook}
          title={editionTitleAttribute}
          onPress={this.onSelectEditionPress}
        >
          {
            showEditionPlaceholder ?
              <InteractiveImportRowCellPlaceholder /> :
              editionCell
          }
        </TableRowCellButton>

        <TableRowCell
          className={styles.quality}
        >
          {
            showQualityPlaceholder &&
              <InteractiveImportRowCellPlaceholder />
          }

          {
            !showQualityPlaceholder && !!quality &&
              <BookQuality
                className={styles.label}
                quality={quality}
              />
          }
        </TableRowCell>

        <TableRowCell>
          {formatBytes(size)}
        </TableRowCell>

        <TableRowCell>
          {
            customFormats.length ?
              <Popover
                anchor={
                  <Icon name={icons.INTERACTIVE} />
                }
                title={translate('Formats')}
                body={
                  <div className={styles.customFormatTooltip}>
                    <BookFormats formats={customFormats} />
                  </div>
                }
                position={tooltipPositions.LEFT}
              /> :
              null
          }
        </TableRowCell>

        {isIndexerFlagsColumnVisible ? (
          <TableRowCellButton
            title={translate('ClickToChangeIndexerFlags')}
            onPress={this.onSelectIndexerFlagsPress}
          >
            {showIndexerFlagsPlaceholder ? (
              <InteractiveImportRowCellPlaceholder isOptional={true} />
            ) : (
              <>
                {indexerFlags ? (
                  <Popover
                    anchor={<Icon name={icons.FLAG} kind={kinds.PRIMARY} />}
                    title={translate('IndexerFlags')}
                    body={<IndexerFlags indexerFlags={indexerFlags} />}
                    position={tooltipPositions.LEFT}
                  />
                ) : null}
              </>
            )}
          </TableRowCellButton>
        ) : null}

        <TableRowCell>
          {
            hasRejections || hasWarnings ?
              <Popover
                anchor={
                  <Icon
                    name={statusIcon}
                    kind={statusKind}
                  />
                }
                title={statusTitle}
                body={
                  <div>
                    {
                      hasRejections ?
                        <ul>
                          {
                            rejections.map((rejection, index) => {
                              return (
                                <li key={`rejection-${index}`}>
                                  {rejection.reason}
                                </li>
                              );
                            })
                          }
                        </ul> :
                        null
                    }

                    {
                      hasWarnings ?
                        <ul>
                          {
                            warnings.map((warning, index) => {
                              return (
                                <li key={`warning-${index}`}>
                                  {warning}
                                </li>
                              );
                            })
                          }
                        </ul> :
                        null
                    }
                  </div>
                }
                position={tooltipPositions.LEFT}
                canFlip={false}
              /> :
              null
          }
        </TableRowCell>

        <ConfirmModal
          isOpen={isDetailsModalOpen}
          title={translate('FileDetails')}
          message={fileDetails}
          size={sizes.LARGE}
          kind={kinds.DEFAULT}
          hideCancelButton={true}
          confirmLabel={translate('Close')}
          onConfirm={this.onDetailsModalClose}
          onCancel={this.onDetailsModalClose}
        />

        <SelectAuthorModal
          isOpen={isSelectAuthorModalOpen}
          ids={[id]}
          onModalClose={this.onSelectAuthorModalClose}
        />

        <SelectBookModal
          isOpen={isSelectBookModalOpen}
          ids={[id]}
          authorId={hasAuthor ? author.id : null}
          mediaType={this.getMediaTypeFromQuality(quality)}
          onModalClose={this.onSelectBookModalClose}
        />

        <SelectEditionModal
          isOpen={isSelectEditionModalOpen}
          importIdsByBook={hasBook ? { [book.id]: [id] } : {}}
          books={hasBook ? [{ book, bookId: book.id }] : []}
          onModalClose={this.onSelectEditionModalClose}
        />

        <SelectQualityModal
          isOpen={isSelectQualityModalOpen}
          ids={[id]}
          qualityId={quality ? quality.quality.id : 0}
          proper={quality ? quality.revision.version > 1 : false}
          real={quality ? quality.revision.real > 0 : false}
          onModalClose={this.onSelectQualityModalClose}
        />

        <SelectIndexerFlagsModal
          isOpen={isSelectIndexerFlagsModalOpen}
          ids={[id]}
          indexerFlags={indexerFlags ?? 0}
          onModalClose={this.onSelectIndexerFlagsModalClose}
        />
      </TableRow>
    );
  }

}

InteractiveImportRow.propTypes = {
  id: PropTypes.number.isRequired,
  allowAuthorChange: PropTypes.bool.isRequired,
  path: PropTypes.string.isRequired,
  author: PropTypes.object,
  suggestedForeignAuthorId: PropTypes.string,
  suggestedAuthorName: PropTypes.string,
  suggestedForeignBookId: PropTypes.string,
  suggestedBookTitle: PropTypes.string,
  suggestedForeignEditionId: PropTypes.string,
  suggestedEditionTitle: PropTypes.string,
  book: PropTypes.object,
  edition: PropTypes.object,
  editionId: PropTypes.number,
  foreignEditionId: PropTypes.string,
  releaseGroup: PropTypes.string,
  quality: PropTypes.object,
  size: PropTypes.number.isRequired,
  customFormats: PropTypes.arrayOf(PropTypes.object),
  indexerFlags: PropTypes.number.isRequired,
  rejections: PropTypes.arrayOf(PropTypes.object).isRequired,
  warnings: PropTypes.arrayOf(PropTypes.string),
  columns: PropTypes.arrayOf(PropTypes.object).isRequired,
  tags: PropTypes.object,
  additionalFile: PropTypes.bool.isRequired,
  isReprocessing: PropTypes.bool,
  isSelected: PropTypes.bool,
  onSelectedChange: PropTypes.func.isRequired,
  onValidRowChange: PropTypes.func.isRequired
};

export default InteractiveImportRow;
