import PropTypes from 'prop-types';
import React from 'react';
import Label from 'Components/Label';
import Link from 'Components/Link/Link';
import { kinds, sizes } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './AuthorDetailsLinks.css';

// Helper function to check if URL is an image
function isImageUrl(url) {
  if (!url) return false;
  const imageExtensions = ['.jpg', '.jpeg', '.png', '.gif', '.webp', '.bmp', '.svg'];
  const urlLower = url.toLowerCase();
  return imageExtensions.some(ext => urlLower.endsWith(ext));
}

function AuthorDetailsLinks(props) {
  const {
    links
  } = props;

  if (!links || links.length === 0) {
    return (
      <div className={styles.links}>
        <Label
          className={styles.linkLabel}
          kind={kinds.DEFAULT}
          size={sizes.LARGE}
        >
          {translate('NoLinks')}
        </Label>
      </div>
    );
  }

  return (
    <div className={styles.links}>

      {links.map((link, index) => {
        const isImage = isImageUrl(link.url);
        
        return (
          <span key={index}>
            {isImage ? (
              // Non-clickable label for image URLs
              <Label
                className={styles.linkLabel}
                kind={kinds.WARNING}
                size={sizes.LARGE}
                title={translate('LinkImageUrlTooltip', { url: link.url })}
              >
                {translate('LinkNameImageSuffix', { name: link.name })}
              </Label>
            ) : (
              // Clickable link for valid URLs
              <Link className={styles.link}
                to={link.url}
                key={index}
              >
                <Label
                  className={styles.linkLabel}
                  kind={kinds.INFO}
                  size={sizes.LARGE}
                >
                  {link.name}
                </Label>
              </Link>
            )}
            {(index > 0 && index % 5 === 0) &&
              <br />
            }

          </span>
        );
      })}

    </div>

  );
}

AuthorDetailsLinks.propTypes = {
  links: PropTypes.arrayOf(PropTypes.object).isRequired
};

export default AuthorDetailsLinks;
