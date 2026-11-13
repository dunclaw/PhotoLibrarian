# Search

Search applies to the photos already included by the left-pane selection and header filters. Ordinary words keep the existing behavior: every word must match a word prefix in the filename, title, caption, tags, camera, or people index. Search clauses combine with those words and with each other using AND.

Field queries are local and read the metadata already indexed for each photo:

Numeric and date comparisons also accept natural forms such as `rating > 0`,
`Rating > 1`, `ISO > 800`, and `date >= 2026-04`. Field names are
case-insensitive; the colon is optional for comparisons, and spaces around
the colon and comparison operator are allowed. For example, `rating:>0`,
`rating > 0`, and `rating: > 0` are equivalent. Missing or invalid comparison
values show a syntax error rather than being interpreted as ordinary words.

| Field | Example | Matching behavior |
| --- | --- | --- |
| `rating` | `rating:>3` | Exact integer when written without an operator; also supports `>`, `>=`, `<`, and `<=`. The app's rating scale is 1-5. |
| `flag` | `flag:pick` | Matches flagged photos. Use `flag:unpick` (or `flag:unflagged`) for unflagged photos. |
| `iso` | `iso:>800` | Exact ISO value without an operator; also supports `>`, `>=`, `<`, and `<=`. |
| `camera` | `camera:x2` | Word-prefix search in the indexed camera make and model. |
| `date` | `date:2026-04` | Matches the indicated year (`YYYY`), month (`YYYY-MM`), or day (`YYYY-MM-DD`). `>=` includes the period and later; `>` starts after it; `<` starts before it; `<=` includes it and earlier. |
| `keyword` | `keyword:mountains` | Word-prefix search in hierarchical tag names. |
| `person` | `person:"Jane Doe"` | Word-prefix phrase search in assigned people names. Quote values containing spaces. |

Text field values support word-prefix matching; quoted multi-word values are treated as a phrase. Numeric and date fields accept comparison operators, while `flag` accepts only the listed values. Unknown fields, malformed values, and unterminated quotes show an inline syntax error and do not replace the current search.

Search syntax only filters the local index. It does not modify photo metadata, sidecars, or the library.
