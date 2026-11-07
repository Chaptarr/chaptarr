import { filesize } from 'filesize';
import { getLocale } from 'Utilities/Localization/locale';

function formatBytes(input, showBits = false) {
  const size = Number(input);

  if (isNaN(size)) {
    return '';
  }

  return `${filesize(size, {
    base: 2,
    round: 1,
    bits: showBits,
    locale: getLocale()
  })}`;
}

export default formatBytes;
