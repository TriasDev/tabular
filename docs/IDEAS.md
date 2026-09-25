# Ideas

Things the design would accommodate but that nobody has asked for. No issues, no plan, no promise —
this file exists so an idea worth half an hour of thought is not re-thought from scratch in a year.

An entry is deleted when it is built, or when it stops being a good idea. Neither has happened yet.

---

## A zip of csv files, read as a workbook

Several csv files in one archive, presented the way a workbook's sheets are: one entry per sheet,
its name from the file's name, its columns profiled independently.

**Why it fits.** `ITabularCursor` already says `Sheets` and `MoveToSheet(index)`. A workbook is a zip
with one stream per sheet; this is a zip with one stream per sheet. It is not a new kind of file — it
is a third implementation of an interface that already exists, and nothing above the cursor
(profiling, mapping, precheck, import) would learn about it.

**Why it might be worth more than "several files at once".** The 572 MB csv fixture is 60–80 MB
zipped, read as a stream without ever being unpacked to disk. For gigabyte files that is a larger
argument than the multi-file part.

**The one thing that actually costs something.** Zip entry streams cannot seek — measured, and it is
not only the compressed ones:

```
a.csv (deflate): CanSeek=False, DeflateStream
b.csv (stored):  CanSeek=False, SubReadStream
```

`CsvDialectDetector.Detect` reads a probe of the first bytes to decide encoding and delimiter, and
rewinds. Against an entry it cannot. So either the probe is buffered and re-joined to the front of
the stream, or the detector's contract changes to *take* a probe rather than take a stream it may
rewind. The second is cleaner and is a change to code that already works — which is the whole of the
cost, and the reason this is not a free afternoon.

**Format detection stops being about the first four bytes.** `TabularFile.Detect` decides by the zip
signature today, and with this both formats carry it. The archive's central directory answers
instead: a workbook has `[Content_Types].xml` at its root and an archive of csv files does not.
Reading it is cheap — it sits at the end of the file. This also fixes the mirror case, a workbook
renamed to `.zip`.

**What will come up while building it.** Directories inside the archive (is the sheet name the path
or the file name?); entries that are not csv at all (`.DS_Store`, `__MACOSX/`, a readme) — skipped
silently or shown; a different encoding and delimiter per entry, which is fine and which the detector
settles on its own; and the decompression bound, which already exists for xlsx and applies here word
for word.

**What it does not solve.** Sheet order in an archive is entry order, which is arbitrary, whereas a
workbook's tab order is meaningful and the person who uploaded it saw it. "The third sheet" would
mean different things in the two formats. Small, but it argues for addressing a sheet by name rather
than by index from the first line of code.

**Status.** Designed in `docs/superpowers/specs/2026-09-25-archive-as-workbook-design.md`. Its
breaking half — per-sheet format, source, dialect and diagnostics, and a plan that records its
sheet — shipped before v0.1; the archive cursor itself is additive and comes after it.

## Say whether a sheet is hidden

A workbook marks sheets `state="hidden"` or `state="veryHidden"` — lookup tables, scratch space, a
template's internals. The cursor lists them like any other sheet, which is the right default: hiding
is presentation, and the data is still there. Sylvan.Data.Excel skips them instead, which is how the
comparison in #8 noticed.

What a caller cannot do today is tell them apart. A mapping screen offering "which sheet?" should
probably show the visible ones first, or mark the others. `SheetInfo` would carry a `Visibility`
(`Visible`, `Hidden`, `VeryHidden`) read from the same `<sheet>` element the name comes from — no
extra part, no extra cost. A csv's single sheet is always visible.
