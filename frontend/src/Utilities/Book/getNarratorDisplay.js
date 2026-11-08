export default function getNarratorDisplay(bookEdition) {
  const narratorNames = bookEdition.narratorNames || [];

  if (narratorNames.length > 1) {
    return `${narratorNames[0]}, +${narratorNames.length - 1}`;
  }

  if (narratorNames.length === 1) {
    return narratorNames[0];
  }

  return bookEdition.narrator;
}
