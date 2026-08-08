import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Icon from 'Components/Icon';
import Label from 'Components/Label';
import Link from 'Components/Link/Link';
import { icons, sizes } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import AddNewSeriesModal from './AddNewSeriesModal';
import styles from './AddNewSeriesSearchResult.css';

class AddNewSeriesSearchResult extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isNewAddSeriesModalOpen: false
    };
  }

  //
  // Listeners

  onPress = () => {
    this.setState({ isNewAddSeriesModalOpen: true });
  };

  onAddSeriesModalClose = () => {
    this.setState({ isNewAddSeriesModalOpen: false });
  };

  onMBLinkPress = (event) => {
    event.stopPropagation();
  };

  //
  // Render

  render() {
    const {
      foreignSeriesId,
      title,
      titleSlug,
      description,
      workCount,
      primaryWorkCount,
      images,
      isExistingSeries
    } = this.props;

    const {
      isNewAddSeriesModalOpen
    } = this.state;

    const linkProps = isExistingSeries ? { to: `/series/${titleSlug}` } : { onPress: this.onPress };

    return (
      <div className={styles.searchResult}>
        <Link
          className={styles.underlay}
          {...linkProps}
        />

        <div className={styles.overlay}>
          {
            images && images.length > 0 ?
              <div className={styles.posterContainer}>
                <div className={styles.stackedCovers}>
                  {images.slice(0, 3).map((image, index) => (
                    <div
                      key={index}
                      className={`${styles.cover} ${styles[`cover${index + 1}`]}`}
                      style={{
                        backgroundImage: `url(${image.url})`,
                        zIndex: 3 - index
                      }}
                    />
                  ))}
                </div>
              </div> :
              <div className={styles.posterContainer}>
                <div className={styles.loadingCovers}>
                  <div className={styles.loadingText}>{translate('Loading')}{'...'}</div>
                </div>
              </div>
          }
          <div className={styles.content}>
            <div className={styles.titleRow}>
              <div className={styles.titleContainer}>
                <div className={styles.title}>
                  {title}
                </div>
              </div>

              <div className={styles.icons}>
                {
                  isExistingSeries ?
                    <Icon
                      className={styles.alreadyExistsIcon}
                      name={icons.CHECK_CIRCLE}
                      size={36}
                      title={translate('AlreadyInYourLibrary')}
                    /> :
                    null
                }

                <Link
                  className={styles.mbLink}
                  to={`https://hardcover.app/series/${titleSlug}`}
                  onPress={(e) => e.stopPropagation()}
                >
                  <Icon
                    className={styles.mbLinkIcon}
                    name={icons.EXTERNAL_LINK}
                    size={28}
                  />
                </Link>
              </div>
            </div>

            <div>
              <Label size={sizes.LARGE}>
                {workCount} {translate(workCount === 1 ? 'Book' : 'Books')}
              </Label>
              {
                primaryWorkCount !== workCount &&
                  <Label size={sizes.LARGE}>
                    {translate('PrimaryWorkCount', { count: primaryWorkCount })}
                  </Label>
              }
            </div>

            {
              description &&
                <div className={styles.overview}>
                  {description}
                </div>
            }
          </div>
        </div>

        <AddNewSeriesModal
          isOpen={isNewAddSeriesModalOpen && !isExistingSeries}
          foreignSeriesId={foreignSeriesId}
          title={title}
          titleSlug={titleSlug}
          description={description}
          workCount={workCount}
          primaryWorkCount={primaryWorkCount}
          images={images}
          books={this.props.books || []}
          onModalClose={this.onAddSeriesModalClose}
        />
      </div>
    );
  }
}

AddNewSeriesSearchResult.propTypes = {
  foreignSeriesId: PropTypes.string.isRequired,
  title: PropTypes.string.isRequired,
  titleSlug: PropTypes.string,
  description: PropTypes.string,
  workCount: PropTypes.number,
  primaryWorkCount: PropTypes.number,
  images: PropTypes.arrayOf(PropTypes.object),
  books: PropTypes.arrayOf(PropTypes.object),
  isExistingSeries: PropTypes.bool
};

export default AddNewSeriesSearchResult;