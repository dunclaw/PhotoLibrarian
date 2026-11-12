# Search

Search applies to the photos already included by the left-pane selection and header filters. Ordinary words keep the existing behavior: every word must match a word prefix in the filename, title, caption, tags, camera, or people index. Search clauses combine with those words and with each other using AND.

Field queries are local and read the metadata already indexed for each photo:

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
