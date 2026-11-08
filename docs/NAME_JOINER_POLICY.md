# Name and credit separators

One runtime authority, `AuthorNameMatcher.NameJoiners`, supplies the maintained conjunction spellings. `AuthorNameMatcher.SplitCredits` and prepared name comparisons share these words and the same punctuation boundary expression. This policy separates credit evidence; it does not infer authorship, provider identity, aliases, or a complete cast from unrestricted text.

A literal complete name can appear anywhere in a field. Expanding a given initial after an undelimited ordinary word is ambiguous and cannot borrow that initial for a different person. Stored initial spellings remain literal evidence. No casing or surrounding-word dictionary decides whether the preceding word is a label or a person.

Shared-family credits can contain the requested person in either position. Every supplied name part must remain compatible; a different supplied family name cannot be discarded. A joined group is not equivalent to a different joined group merely because one member overlaps. These comparisons do not replace complete-cast checks.

The raw splitter returns supplied text without inventing surnames. Word separators need credits on both sides; leading given names remain intact. Uppercase or dotted single-letter spellings remain initials. A hyphen inside a name is retained; a whitespace-surrounded dash is a credit boundary. Middle dots (`·`) and bullets (`•`) are credit boundaries only when whitespace-surrounded. Other shared boundaries are `; ； & ＆ / | + 、 ， ×`. ASCII comma is ambiguous with inverted names: the one-argument splitter retains a single-comma group. With known names, exact complete spelling can retain an inverted name or separate known comma-delimited credits. Initial compatibility cannot resolve that ambiguity. `with` is ordinary text.

## Provenance

The table records each runtime spelling. The official [Unicode CLDR source](https://github.com/unicode-org/cldr/tree/7cdeb9e7fac52c59f22fc7248b05f989ee5689c2/common/main) is pinned at revision `7cdeb9e7fac52c59f22fc7248b05f989ee5689c2`; this is a source revision, not a released-version claim. The cited entries use the standard two-item or end list pattern unless stated otherwise.

| Spelling | Verified language/use | Primary source at the pinned revision |
| --- | --- | --- |
| `and` | English | [en.xml](https://raw.githubusercontent.com/unicode-org/cldr/7cdeb9e7fac52c59f22fc7248b05f989ee5689c2/common/main/en.xml) |
| `und` | German | [de.xml](https://raw.githubusercontent.com/unicode-org/cldr/7cdeb9e7fac52c59f22fc7248b05f989ee5689c2/common/main/de.xml) |
| `et` | French | [fr.xml](https://raw.githubusercontent.com/unicode-org/cldr/7cdeb9e7fac52c59f22fc7248b05f989ee5689c2/common/main/fr.xml) |
| `y` | Spanish | [es.xml](https://raw.githubusercontent.com/unicode-org/cldr/7cdeb9e7fac52c59f22fc7248b05f989ee5689c2/common/main/es.xml) |
| `e` | Portuguese / Italian | [pt.xml](https://raw.githubusercontent.com/unicode-org/cldr/7cdeb9e7fac52c59f22fc7248b05f989ee5689c2/common/main/pt.xml), [it.xml](https://raw.githubusercontent.com/unicode-org/cldr/7cdeb9e7fac52c59f22fc7248b05f989ee5689c2/common/main/it.xml) |
| `en` | Dutch | [nl.xml](https://raw.githubusercontent.com/unicode-org/cldr/7cdeb9e7fac52c59f22fc7248b05f989ee5689c2/common/main/nl.xml) |
| `och` | Swedish | [sv.xml](https://raw.githubusercontent.com/unicode-org/cldr/7cdeb9e7fac52c59f22fc7248b05f989ee5689c2/common/main/sv.xml) |
| `og` | Danish | [da.xml](https://raw.githubusercontent.com/unicode-org/cldr/7cdeb9e7fac52c59f22fc7248b05f989ee5689c2/common/main/da.xml) |
| `i` | Polish | [pl.xml](https://raw.githubusercontent.com/unicode-org/cldr/7cdeb9e7fac52c59f22fc7248b05f989ee5689c2/common/main/pl.xml) |
| `и` | Russian | [ru.xml](https://raw.githubusercontent.com/unicode-org/cldr/7cdeb9e7fac52c59f22fc7248b05f989ee5689c2/common/main/ru.xml) |
| `ja` | Finnish | [fi.xml](https://raw.githubusercontent.com/unicode-org/cldr/7cdeb9e7fac52c59f22fc7248b05f989ee5689c2/common/main/fi.xml) |
| `ed` | Italian euphonic variant | [Treccani, e / ed](https://www.treccani.it/vocabolario/e/) |
| `és` | Hungarian | [hu.xml](https://raw.githubusercontent.com/unicode-org/cldr/7cdeb9e7fac52c59f22fc7248b05f989ee5689c2/common/main/hu.xml) |
| `ve` | Turkish | [tr.xml](https://raw.githubusercontent.com/unicode-org/cldr/7cdeb9e7fac52c59f22fc7248b05f989ee5689c2/common/main/tr.xml) |
| `și` | Romanian | [ro.xml](https://raw.githubusercontent.com/unicode-org/cldr/7cdeb9e7fac52c59f22fc7248b05f989ee5689c2/common/main/ro.xml) |
| `a` | Czech | [cs.xml](https://raw.githubusercontent.com/unicode-org/cldr/7cdeb9e7fac52c59f22fc7248b05f989ee5689c2/common/main/cs.xml) |
| `та` | Ukrainian additive conjunction | [Словник української мови, ТА²](https://slovnyk.ua/index.php?swrd=%D1%82%D0%B0) |
| `і` | Ukrainian | [uk.xml](https://raw.githubusercontent.com/unicode-org/cldr/7cdeb9e7fac52c59f22fc7248b05f989ee5689c2/common/main/uk.xml) |
| `&` | Abbreviated English conjunction, standard-short list | [en.xml](https://raw.githubusercontent.com/unicode-org/cldr/7cdeb9e7fac52c59f22fc7248b05f989ee5689c2/common/main/en.xml) |
| `και` | Greek | [el.xml](https://raw.githubusercontent.com/unicode-org/cldr/7cdeb9e7fac52c59f22fc7248b05f989ee5689c2/common/main/el.xml) |
| `ir` | Lithuanian | [lt.xml](https://raw.githubusercontent.com/unicode-org/cldr/7cdeb9e7fac52c59f22fc7248b05f989ee5689c2/common/main/lt.xml) |
| `un` | Latvian | [lv.xml](https://raw.githubusercontent.com/unicode-org/cldr/7cdeb9e7fac52c59f22fc7248b05f989ee5689c2/common/main/lv.xml) |
| `in` | Slovene | [sl.xml](https://raw.githubusercontent.com/unicode-org/cldr/7cdeb9e7fac52c59f22fc7248b05f989ee5689c2/common/main/sl.xml) |
| `dan` | Indonesian / Malay | [id.xml](https://raw.githubusercontent.com/unicode-org/cldr/7cdeb9e7fac52c59f22fc7248b05f989ee5689c2/common/main/id.xml), [ms.xml](https://raw.githubusercontent.com/unicode-org/cldr/7cdeb9e7fac52c59f22fc7248b05f989ee5689c2/common/main/ms.xml) |
| `và` | Vietnamese | [vi.xml](https://raw.githubusercontent.com/unicode-org/cldr/7cdeb9e7fac52c59f22fc7248b05f989ee5689c2/common/main/vi.xml) |
| `dhe` | Albanian | [sq.xml](https://raw.githubusercontent.com/unicode-org/cldr/7cdeb9e7fac52c59f22fc7248b05f989ee5689c2/common/main/sq.xml) |
| `agus` | Irish | [ga.xml](https://raw.githubusercontent.com/unicode-org/cldr/7cdeb9e7fac52c59f22fc7248b05f989ee5689c2/common/main/ga.xml) |
| `eta` | Basque | [eu.xml](https://raw.githubusercontent.com/unicode-org/cldr/7cdeb9e7fac52c59f22fc7248b05f989ee5689c2/common/main/eu.xml) |
| `və` | Azerbaijani | [az.xml](https://raw.githubusercontent.com/unicode-org/cldr/7cdeb9e7fac52c59f22fc7248b05f989ee5689c2/common/main/az.xml) |

These are 29 spellings, not 29 languages. Comparison keys use the existing shared Unicode folding. Whitespace-delimited credit data and the tested cased-script connectors define the verified scope. Attached conjunctions and caseless-script words are not claimed as supported merely by adding a dictionary spelling; their separator/initial ambiguity still needs scoped evidence and a general rule. Unhandled connector words and attached forms remain ordinary tokens. Existing missing-middle compatibility can therefore match multiple contributors as one expected person; this is an acceptance limit, not a safe rejection. A recognised conjunction spelling can also be a given name, so unrestricted preceding text does not independently prove a separate credit; joined-initial expansion retains that unresolved ambiguity.

Maintenance must verify missing spellings against primary sources and add positive, given-name collision, initial, and false-person controls together. The test inventory pins all entries independently of the runtime array, so silently deleting an entry fails. No external dictionary is loaded at runtime.

Narrator refresh uses the existing transactional link writer. A unique resolved current lead replaces an old primary; tied or unresolved lead evidence retains an existing primary for the same edition/person pair and does not choose a new primary.
