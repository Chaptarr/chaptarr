import React from 'react';
import translate from 'Utilities/String/translate';
import FilterBuilderRowValue from './FilterBuilderRowValue';

const protocols = [
  { id: 'continuing', name: translate('Active') },
  { id: 'ended', name: translate('Dead') }
];

function AuthorStatusFilterBuilderRowValue(props) {
  return (
    <FilterBuilderRowValue
      tagList={protocols}
      {...props}
    />
  );
}

export default AuthorStatusFilterBuilderRowValue;
