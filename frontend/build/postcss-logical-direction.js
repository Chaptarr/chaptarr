const postcss = require('postcss');

const logicalProperties = {
  'border-left': 'border-inline-start',
  'border-left-color': 'border-inline-start-color',
  'border-left-style': 'border-inline-start-style',
  'border-left-width': 'border-inline-start-width',
  'border-bottom-left-radius': 'border-end-start-radius',
  'border-bottom-right-radius': 'border-end-end-radius',
  'border-right': 'border-inline-end',
  'border-right-color': 'border-inline-end-color',
  'border-right-style': 'border-inline-end-style',
  'border-right-width': 'border-inline-end-width',
  'border-top-left-radius': 'border-start-start-radius',
  'border-top-right-radius': 'border-start-end-radius',
  'inset-left': 'inset-inline-start',
  'inset-right': 'inset-inline-end',
  left: 'inset-inline-start',
  'margin-left': 'margin-inline-start',
  'margin-right': 'margin-inline-end',
  'padding-left': 'padding-inline-start',
  'padding-right': 'padding-inline-end',
  right: 'inset-inline-end'
};

function expandBoxShorthand(decl, property) {
  const values = postcss.list.space(decl.value);

  if (values.length < 2 || values.length > 4) {
    return false;
  }

  let top;
  let right;
  let bottom;
  let left;

  if (values.length === 2) {
    [top, right] = values;
    bottom = top;
    left = right;
  } else if (values.length === 3) {
    [top, right, bottom] = values;
    left = right;
  } else {
    [top, right, bottom, left] = values;
  }

  decl.cloneBefore({ prop: property + '-inline', value: left + ' ' + right });
  decl.prop = property + '-block';
  decl.value = top + ' ' + bottom;

  return true;
}

function expandBorderRadius(decl) {
  const parts = decl.value.split('/');
  if (parts.length > 2) {
    return false;
  }

  const horizontal = postcss.list.space(parts[0]);
  const vertical = parts[1] ? postcss.list.space(parts[1]) : horizontal;
  if (horizontal.length < 1 || horizontal.length > 4 || vertical.length < 1 || vertical.length > 4) {
    return false;
  }

  const expand = ([first, second = first, third = first, fourth = second]) => [
    first, second, third, fourth
  ];
  const horizontalCorners = expand(horizontal);
  const verticalCorners = expand(vertical);
  const names = [
    'border-start-start-radius',
    'border-start-end-radius',
    'border-end-end-radius',
    'border-end-start-radius'
  ];
  const corners = names.map((name, index) => [
    name,
    horizontalCorners[index] === verticalCorners[index] ?
      horizontalCorners[index] :
      horizontalCorners[index] + ' ' + verticalCorners[index]
  ]);

  decl.cloneBefore({ prop: corners[0][0], value: corners[0][1] });
  for (const [property, value] of corners.slice(1)) {
    decl.cloneAfter({ prop: property, value });
  }
  decl.remove();

  return true;
}

function logicalDirectionPlugin() {
  return {
    postcssPlugin: 'postcss-logical-direction',
    Declaration(decl) {
      const property = decl.prop.toLowerCase();

      if ((property === 'margin' || property === 'padding' || property === 'inset') &&
          expandBoxShorthand(decl, property)) {
        return;
      }

      if (property === 'border-radius' && expandBorderRadius(decl)) {
        return;
      }

      const logicalProperty = logicalProperties[property];
      if (logicalProperty) {
        decl.prop = logicalProperty;
        return;
      }

      if (property === 'text-align') {
        if (decl.value === 'left') {
          decl.value = 'start';
        } else if (decl.value === 'right') {
          decl.value = 'end';
        }
        return;
      }

      if (property === 'float' || property === 'clear') {
        if (decl.value === 'left') {
          decl.value = 'inline-start';
        } else if (decl.value === 'right') {
          decl.value = 'inline-end';
        }
      }
    }
  };
}

logicalDirectionPlugin.postcss = true;
module.exports = logicalDirectionPlugin;
