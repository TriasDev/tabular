# Streams, cancellation, progress and cultures

## Who closes the stream

One rule at every entry point: a stream handed over is closed — when the cursor or run is disposed,
and also when the call fails — unless the caller asked for it to stay open.

| Entry point | Closes the stream | Keep it open with |
|---|---|---|
| `new CsvCursor(stream, …)`, `new XlsxCursor(stream, …)` | on `Dispose`, or on a failed open | `leaveOpen: true` |
| `TabularFile.Open(stream, …)` | on `Dispose`, or on a failed open | `TabularOpenOptions.LeaveOpen` |
| `TabularImporter.Import(stream, …)` | on `Dispose` of the run, or when the call throws (a refused plan included) | `ImportOptions.Open.LeaveOpen` |
| `TabularImporter.Import(cursor, …)`, `TabularAnalyzer.Analyze`, `TabularExtractor.Start` | never — the cursor is the caller's | — |

`TabularOpenOptions` also carries the csv, xlsx and ods cursor options and the archive's bounds, so
ceilings can be changed without giving up format detection. The files inside an archive are read with
the same csv, xlsx and ods options as files on their own.

## Cancellation

`ReadRow` takes a `CancellationToken`, and the analyzer and importer hand theirs down rather than
only checking between rows. That distinction is the whole of it: the expensive things happen *inside*
a single read — a shared string table of a million entries loads on the first cell that refers to
one — and a check between rows never runs while that is happening. A timeout at the request level
cannot reach a thread that is inside such a call.

The token is checked on a stride rather than per character, because the check is cheap but not free
and a row is normally over in a few hundred characters.

Every operation that reads takes a token, last parameter, as the BCL's do: `Open`, `Analyze`,
`Start` and `Import` for the work they do up front, and `ExtractionSession.ReadRow`,
`ImportRun.Rows`, `InChunks` and `All` for the reading. The token a run was started with keeps
applying to every read of it, so either one stops the run — a plain `foreach` over the run, which
cannot pass a token, is stopped by the run's.

## Progress

Analysis of a multi-million-row file takes seconds to tens of seconds, and a screen waiting on it
wants to say how far it has got:

```csharp
IProgress<AnalysisProgress> progress = new Progress<AnalysisProgress>(p =>
    Console.WriteLine($"{p.SheetName}: {p.RowsRead:N0} rows, {p.Fraction:P0}"));

FileProfile profile = new TabularAnalyzer().Analyze(cursor, progress, cancellationToken);
```

The fraction is taken from how much of the file the reader has consumed — a csv's stream position
against its length, a workbook's worksheet bytes against their total, which the package directory
states up front — so nothing reads the file twice to have a denominator. It is exactly 1 in the final
report, which has `IsComplete` set; where the stream has no length it is null until then.

Reports go out when the fraction has moved by `AnalysisOptions.ProgressStep` (1% by default) **and**
at least `ProgressInterval` data rows (10,000) have passed since the last one. A five-million-row
file reports about a hundred times; a file of twenty thousand rows, read in milliseconds, once or
twice. With no length to measure, the row interval alone decides. `Progress<T>` posts each report to
the context it was created on; an `IProgress<T>` of your own is called on the analysing thread.

A console progress bar with Ctrl+C cancellation, runnable:
[`samples/TriasDev.Tabular.Samples.Progress`](https://github.com/TriasDev/tabular/tree/main/samples/TriasDev.Tabular.Samples.Progress).

## Cultures

Every text value is tried under each culture in `AnalysisOptions.Cultures`, by default
`["", "de-DE", "en-US"]` — invariant, German and US conventions — and the ranked hypotheses name the
culture that read a column — the empty string for the invariant one, the same spelling
`MappingPlan.Culture` takes, so a hypothesis's culture goes into a plan as it is. The default leans towards the files this library was first written for;
a caller whose files come from elsewhere should list its own (`["", "fr-FR"]`). An unknown name is
refused when the analyzer is created.

Under invariant globalization (`InvariantGlobalization` / `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT`,
common in slim container images) only the invariant culture exists. Analysis then leaves the named
cultures out instead of failing — the profile's `ParseCounts` show which cultures were used — and a
mapping that names one is refused by the validator and the precheck as `mapping.unknown-culture`.
German amounts such as `1.234,50` cannot be read as numbers in that mode.
