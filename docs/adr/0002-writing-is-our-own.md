# 0002 — Tabular files are written by our own writers, not by a writing library

Date: 2026-10-04
Status: Accepted

## Context

Export is the mirror of import: a `TabularWriter` produces csv, xlsx, ods or a zip of csv sheets
that this library reads back, and the file it wrote has to mean what the data meant. The design is in
`docs/superpowers/specs/2026-10-03-writing-design.md` and
`docs/superpowers/specs/2026-10-04-styled-columnar-export-design.md`. Four requirements decided
whether a writing library could sit underneath it:

- **A non-seekable, asynchronous target.** The usual destination is an HTTP response body. ASP.NET
  Core refuses synchronous writes to it, and it cannot seek back to patch a header.
- **Flat memory.** Five million rows must cost what ten thousand do. A writer that holds a workbook,
  a shared string table or a whole zip entry fails this before it is tested for speed.
- **Three formats, one contract.** Csv, xlsx and ods behave alike, and a round trip through our own
  reader is the proof.
- **No dependencies.** The package takes the base class library alone, as the reader does
  ([ADR-0001](0001-tabular-parsing-is-our-own-cursor.md)).

The format part of a writer is the small part: csv is quoting, xlsx and ods are XML written in a
fixed order. The part that decides is the zip, and `System.IO.Compression.ZipArchive` fails three of
the four requirements, for reasons that were each checked rather than assumed.

- **It writes synchronously.** An entry stream is a synchronous `Stream`; the asynchronous
  `ZipArchive` API exists only from .NET 10, and the library also targets net8.0.
- **With a seekable target it buffers whole entries.** To write sizes into a local header it holds
  the entry until it is complete, which is the opposite of flat memory.
- **Its streamed output is refused by LibreOffice.** Into a non-seekable stream it writes data
  descriptors, and for an ods that includes the `mimetype` entry. LibreOffice refused the ods that
  `ZipArchive` produced that way: the `mimetype` must be the first entry, stored, with its sizes in the
  local header.

## Decision

**Every format is written by a writer we own, built on the base class library alone, over a spill
buffer and a zip writer of ours. The library takes no third-party writing dependency.**

**The format writers write synchronously into a spill buffer.** Pooled 64 KB segments take the
bytes; only `FlushAsync` and `CompleteAsync` touch the target, and they do it asynchronously. The
caller flushes when the writer recommends it, so memory stays flat: about 57 to 59 MB peak for five
million csv rows, about 95 MB for ten thousand rows of 5,003 columns, in the provisional benchmarks.

**The zip writer is ours.** Small parts — the ods `mimetype`, the manifests, the styles — are stored
with their sizes up front. Large entries are deflated and streamed with data descriptors. Past 4 GB
it writes zip64; a 7.39 GB csv entry was checked with `unzip -t`, with `ZipArchive` and with our own
reader. Names are UTF-8 with flag `0x0800`, and the DOS date is fixed, so the same data gives the
same bytes. The CRC-32 is the one the gzip reader already uses, hardware-accelerated on ARM64.

**xlsx uses inline strings**, so no shared string table is held in memory. A cell's reference is
written only where the cell does not follow the one before it. Styles are resolved per (style, value
kind) on first use, and the styles part is written at the end, when everything that uses it is known.

**ods puts its styles in `styles.xml` as common styles**, so a style may first appear in the middle
of the stream; a LibreOffice probe confirmed that it is applied. `settings.xml` must declare
`xmlns:ooo`: without it LibreOffice ignored the frozen panes.

**The round trip is the contract.** Every value type is checked on the way out — doubles to 15
significant digits, decimals and longs exactly, dates, forbidden characters, text limits — so that an
export reads back through our own import. Precision loss is an error and never silent.

## Consequences

**We maintain three format writers, a zip writer and the style handling.** The zip writer is the most
delicate of them, and the large-entry and zip64 cases are tested against `unzip`, `ZipArchive` and our
reader because a small file never reaches them. The fixtures, and LibreOffice opening what we write,
are what say whether the files are right; that is paid in tests rather than in dependencies.

**The measured results are provisional**, from a single pass on a machine other work was using; see
[benchmarks](../benchmarks.md#writing), which is kept current and not this page. On csv we are faster
than CsvHelper and slower than Sylvan.Data.Csv. On xlsx we are faster than LargeXlsx in most
scenarios, and SpreadCheetah is faster than we are. MiniExcel is the slowest and allocates the most.
No other library measured writes ods or a zip of csv sheets. On the narrow files we hold the least
memory of the xlsx writers; on the wide ones we hold more, because 500 rows are batched by column.

**Limits.** The target is written once and forwards only. The checks cost a little time on the
numeric path; that cost is the price of a file that reads back.

## Alternatives considered

- **LargeXlsx or SpreadCheetah as a dependency.** Both are fast, streaming xlsx writers, and
  SpreadCheetah is faster than ours. Each adds a dependency, writes xlsx only — there is no ods and no
  csv, so the zip writer and two more format writers would remain ours — and neither has a round-trip
  contract with our reader. Rejected: the dependency would remove the smaller part of the work.
- **The Open XML SDK.** Its DOM mode materialises the document, and its SAX mode is verbose and
  heavy. xlsx only, so it fails the memory requirement and leaves ods open.
- **`ZipArchive` under our own format writers.** The reasons above: synchronous writes, whole entries
  buffered when seekable, and an ods LibreOffice refuses when streamed. Rejected for the zip only; the
  format writers are ours either way.
- **MiniExcel.** Its fast mode is not streaming, and its streaming mode is the slowest and most
  allocating of the writers measured.
