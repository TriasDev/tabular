# Ideas

Things the design would accommodate but that nobody has asked for yet. No promise — this file exists
so an idea worth half an hour of thought is not re-thought from scratch in a year. Where one has
become an issue, its entry says so.

An entry is deleted when it is built, or when it stops being a good idea.

---

## Opting out of the header-width floor

Every workbook column is at least as wide as its header (#114). A caller who wants the opening
program's default width instead — a dense sheet of short codes under long headers, say — has no way
to ask for it. A `SheetOptions` flag would do, and would leave `WriteColumn.Width = null` meaning
"the header's width" as it does now; a sentinel width would be harder to read. Nobody has needed it:
a wrapped header style already lets a long header sit over a narrow column, since only its longest
word sets the floor.
