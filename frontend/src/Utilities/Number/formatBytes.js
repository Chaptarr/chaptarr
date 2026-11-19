import { filesize } from 'filesize';

function formatBytes(input, showBits = false, round = 1) {
  const size = Number(input);

  if (isNaN(size)) {
    return '';
  }

  return `${filesize(size, {
    base: 2,
    round,
    bits: showBits
  })}`;
}

export default formatBytes;
