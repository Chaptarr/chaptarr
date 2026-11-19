import classNames from 'classnames';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Measure from 'Components/Measure';
import Menu from 'Components/Menu/Menu';
import MenuContent from 'Components/Menu/MenuContent';
import MenuItem from 'Components/Menu/MenuItem';
import ToolbarMenuButton from 'Components/Menu/ToolbarMenuButton';
import SpinnerIcon from 'Components/SpinnerIcon';
import { forEach } from 'Helpers/elementChildren';
import { align, icons } from 'Helpers/Props';
import dimensions from 'Styles/Variables/dimensions';
import translate from 'Utilities/String/translate';
import styles from './PageToolbarSection.css';

const BUTTON_WIDTH = parseInt(dimensions.toolbarButtonWidth);
const SEPARATOR_MARGIN = parseInt(dimensions.toolbarSeparatorMargin);
const SEPARATOR_WIDTH = 2 * SEPARATOR_MARGIN + 1;

function calculateOverflowItems(children, isMeasured, width, collapseButtons) {
  let buttonCount = 0;
  let separatorCount = 0;
  let pinnedButtonCount = 0;
  const validChildren = [];

  forEach(children, (child) => {
    if (Object.keys(child.props).length === 0) {
      separatorCount++;
    } else {
      buttonCount++;
      if (!child.props.label) {
        pinnedButtonCount++;
      }
    }

    validChildren.push(child);
  });

  const buttonsWidth = buttonCount * BUTTON_WIDTH;
  const separatorsWidth = separatorCount * SEPARATOR_WIDTH;
  const totalWidth = buttonsWidth + separatorsWidth;

  // If the width of buttons and separators is less than
  // the available width return all valid children.

  if (
    !isMeasured ||
    !collapseButtons ||
    totalWidth < width
  ) {
    return {
      buttons: validChildren,
      buttonCount,
      pinnedButtonCount,
      overflowItems: []
    };
  }

  // Keep wrapped menus intact and reserve a slot for More. At phone widths,
  // More may be the only action that fits beside the pinned controls.
  const maxButtons = Math.max(Math.floor(width / BUTTON_WIDTH) - pinnedButtonCount - 1, 0);
  const buttons = [];
  const overflowItems = [];
  let actualButtons = 0;

  // A More button takes the same space as one remaining action.
  if (buttonCount * BUTTON_WIDTH <= width) {
    return {
      buttons: validChildren.filter((child) => Object.keys(child.props).length !== 0),
      buttonCount,
      pinnedButtonCount,
      overflowItems: []
    };
  }

  validChildren.forEach((child) => {
    const isSeparator = Object.keys(child.props).length === 0;

    if (!isSeparator && !child.props.label) {
      buttons.push(child);
    } else if (actualButtons < maxButtons) {
      if (!isSeparator) {
        buttons.push(child);
        actualButtons++;
      }
    } else if (!isSeparator) {
      overflowItems.push(child.props);
    }
  });

  return {
    buttons,
    buttonCount,
    pinnedButtonCount,
    overflowItems
  };
}

class PageToolbarSection extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isMeasured: false,
      width: 0
    };
  }

  //
  // Listeners

  onMeasure = ({ width }) => {
    this.setState({
      isMeasured: true,
      width
    });
  };

  //
  // Render

  render() {
    const {
      children,
      alignContent,
      collapseButtons
    } = this.props;

    const {
      isMeasured,
      width
    } = this.state;

    const {
      buttons,
      buttonCount,
      pinnedButtonCount,
      overflowItems
    } = calculateOverflowItems(children, isMeasured, width, collapseButtons);

    return (
      <Measure
        onMeasure={this.onMeasure}
        className={styles.sectionContainer}
        style={{
          minWidth: BUTTON_WIDTH * (pinnedButtonCount + (buttonCount > pinnedButtonCount ? 1 : 0)),
          flexGrow: collapseButtons ? buttonCount : 0,
          flexShrink: collapseButtons ? 1 : 0
        }}
      >
        {
          isMeasured ?
            <div className={classNames(
              styles.section,
              styles[alignContent]
            )}
            >
              {
                buttons.map((button) => {
                  return button;
                })
              }

              {
                !!overflowItems.length &&
                  <Menu>
                    <ToolbarMenuButton
                      className={styles.overflowMenuButton}
                      iconName={icons.OVERFLOW}
                      text={translate('More')}
                    />

                    <MenuContent>
                      {
                        overflowItems.map((item) => {
                          const {
                            iconName,
                            spinningName,
                            label,
                            isDisabled,
                            isSpinning,
                            ...otherProps
                          } = item;

                          return (
                            <MenuItem
                              key={label}
                              isDisabled={isDisabled || isSpinning}
                              {...otherProps}
                            >
                              <SpinnerIcon
                                className={styles.overflowMenuItemIcon}
                                name={iconName}
                                spinningName={spinningName}
                                isSpinning={isSpinning}
                              />
                              {label}
                            </MenuItem>
                          );
                        })
                      }
                    </MenuContent>
                  </Menu>
              }
            </div> :
            null
        }
      </Measure>
    );
  }

}

PageToolbarSection.propTypes = {
  children: PropTypes.node,
  alignContent: PropTypes.oneOf([align.LEFT, align.CENTER, align.RIGHT]),
  collapseButtons: PropTypes.bool.isRequired
};

PageToolbarSection.defaultProps = {
  alignContent: align.LEFT,
  collapseButtons: true
};

export default PageToolbarSection;
