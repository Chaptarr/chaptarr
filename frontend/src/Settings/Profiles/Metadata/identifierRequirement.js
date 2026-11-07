// Maps the identifier requirement choice onto the two stored profile flags.
// Missing both:   keep editions that have an ISBN or an ASIN.
// Missing ASIN:   keep editions that have an ASIN.
// Missing either: keep editions that have both an ISBN and an ASIN.

export const identifierRequirements = {
  none: { skipMissingIsbn: false, skipMissingAsin: false },
  both: { skipMissingIsbn: true, skipMissingAsin: false },
  asin: { skipMissingIsbn: false, skipMissingAsin: true },
  either: { skipMissingIsbn: true, skipMissingAsin: true }
};

export function getIdentifierRequirement(skipMissingIsbn, skipMissingAsin) {
  const isbn = !!skipMissingIsbn;
  const asin = !!skipMissingAsin;

  if (isbn && asin) {
    return 'either';
  }

  if (isbn) {
    return 'both';
  }

  return asin ? 'asin' : 'none';
}
