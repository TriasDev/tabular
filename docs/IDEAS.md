# Ideas

Things the design would accommodate but that nobody has asked for yet. No promise — this file exists
so an idea worth half an hour of thought is not re-thought from scratch in a year. Where one has
become an issue, its entry says so.

An entry is deleted when it is built, or when it stops being a good idea.

---

## Say whether a sheet is hidden

A workbook marks sheets `state="hidden"` or `state="veryHidden"` — lookup tables, scratch space, a
template's internals. The cursor lists them like any other sheet, which is the right default: hiding
is presentation, and the data is still there. Sylvan.Data.Excel skips them instead, which is how the
comparison in #8 noticed.

What a caller cannot do today is tell them apart. A mapping screen offering "which sheet?" should
probably show the visible ones first, or mark the others. `SheetInfo` would carry a `Visibility`
(`Visible`, `Hidden`, `VeryHidden`) read from the same `<sheet>` element the name comes from — no
extra part, no extra cost. A csv's single sheet is always visible.
