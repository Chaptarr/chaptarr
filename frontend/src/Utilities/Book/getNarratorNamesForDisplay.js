export default function getNarratorNamesForDisplay(credits = [], fallbackNames = []) {
  const populated = (credits || []).filter((credit) => credit.name?.trim());
  const people = populated.filter((credit) => !credit.isCastLabel);
  return populated.length ? (people.length ? people : populated).map((credit) => credit.name) :
    (fallbackNames || []).filter((name) => typeof name === 'string' && name.trim());
}
