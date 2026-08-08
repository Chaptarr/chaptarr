import React from 'react';
import { useSelector } from 'react-redux';
import createIndexerFlagsSelector from 'Store/Selectors/createIndexerFlagsSelector';

interface IndexerFlagsProps {
  indexerFlags: number;
}

const FREELEECH_FLAG = 1;
const VIP_EXCLUSIVE_FLAG = 128;
const VIP_FREELEECH_FLAG = 256;

function IndexerFlags({ indexerFlags = 0 }: IndexerFlagsProps) {
  const allIndexerFlags = useSelector(createIndexerFlagsSelector);

  const hasVipFreeleech =
    // eslint-disable-next-line no-bitwise
    (indexerFlags & VIP_FREELEECH_FLAG) === VIP_FREELEECH_FLAG;

  const flags = allIndexerFlags.items
    .filter(
      // eslint-disable-next-line no-bitwise
      (item) => (indexerFlags & item.id) === item.id
    )
    .filter(
      (item) =>
        !(
          hasVipFreeleech &&
          (item.id === FREELEECH_FLAG || item.id === VIP_EXCLUSIVE_FLAG)
        )
    )
    .map((item) => {
      if (hasVipFreeleech && item.id === VIP_FREELEECH_FLAG) {
        return {
          ...item,
          name: 'VIP/Freeleech',
        };
      }

      return item;
    });

  return flags.length ? (
    <ul>
      {flags.map((flag, index) => {
        return <li key={index}>{flag.name}</li>;
      })}
    </ul>
  ) : null;
}

export default IndexerFlags;
