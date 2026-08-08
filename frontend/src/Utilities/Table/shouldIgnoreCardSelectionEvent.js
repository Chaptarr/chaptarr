export default function shouldIgnoreCardSelectionEvent(event) {
  const target = event.target;
  const currentTarget = event.currentTarget;

  if (!(target instanceof Element)) {
    return true;
  }

  // React bubbles events from portals (e.g. modals opened from the card)
  // through the component tree, so the DOM target can live outside the card.
  if (currentTarget instanceof Element && !currentTarget.contains(target)) {
    return true;
  }

  return target.closest('[data-select-exempt="true"]') != null;
}
