import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Button from 'Components/Link/Button';
import Link from 'Components/Link/Link';
import Menu from 'Components/Menu/Menu';
import MenuContent from 'Components/Menu/MenuContent';
import { sizes } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import AddSpecificationPresetMenuItem from './AddSpecificationPresetMenuItem';
import styles from './AddSpecificationItem.css';

class AddSpecificationItem extends Component {

  //
  // Listeners

  onSpecificationSelect = () => {
    const { implementation } = this.props;
    this.props.onSpecificationSelect({ implementation });
  };

  //
  // Render

  render() {
    const {
      implementation,
      implementationName,
      description,
      presets,
      onSpecificationSelect
    } = this.props;

    const hasPresets = !!presets && !!presets.length;

    return (
      <div className={styles.specification}>
        <Link
          className={styles.underlay}
          onPress={this.onSpecificationSelect}
        />

        <div className={styles.overlay}>
          <div className={styles.name}>
            {implementationName}
          </div>

          <div className={styles.description}>
            {description}
          </div>

          <div className={styles.actions}>
            <Button
              size={sizes.SMALL}
              onPress={this.onSpecificationSelect}
            >
              {translate(hasPresets ? 'Custom' : 'Add')}
            </Button>

            {
              hasPresets &&
                <Menu className={styles.presetsMenu}>
                  <Button
                    className={styles.presetsMenuButton}
                    size={sizes.SMALL}
                  >
                    {translate('Presets')}
                  </Button>

                  <MenuContent>
                    {
                      presets.map((preset, index) => {
                        return (
                          <AddSpecificationPresetMenuItem
                            key={index}
                            name={preset.name}
                            implementation={implementation}
                            onPress={onSpecificationSelect}
                          />
                        );
                      })
                    }
                  </MenuContent>
                </Menu>
            }
          </div>
        </div>
      </div>
    );
  }
}

AddSpecificationItem.propTypes = {
  implementation: PropTypes.string.isRequired,
  implementationName: PropTypes.string.isRequired,
  description: PropTypes.string.isRequired,
  presets: PropTypes.arrayOf(PropTypes.object),
  onSpecificationSelect: PropTypes.func.isRequired
};

export default AddSpecificationItem;
