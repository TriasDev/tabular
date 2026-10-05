# Getting started

The library reads and writes. This page walks through both: reading a file, importing it into your
own type through a mapping, and writing one.

## Install

```bash
dotnet add package TriasDev.Tabular
```

The package targets .NET 8 and .NET 10 and references nothing but the base class library.

## Read a file

`TabularFile.Open` decides the format from the file's bytes — csv, xlsx, ods, a zip or tar archive of
them, or a gzip-compressed file — and returns a cursor over its sheets and rows:

```csharp
using TriasDev.Tabular;

using FileStream file = File.OpenRead("orders.xlsx");
using ITabularCursor cursor = TabularFile.Open(file, "orders.xlsx");

foreach (SheetInfo sheet in cursor.Sheets)
{
    cursor.MoveToSheet(sheet.Index);

    while (cursor.ReadRow())
    {
        ReadOnlySpan<RawCell> row = cursor.CurrentRow;   // typed cells: text, number, date, boolean
    }
}
```

A row is a view over a reused buffer: read what you need from it before the next `ReadRow`.

## Import into your own type

The steps an upload goes through, from
[`samples/TriasDev.Tabular.Samples.Import`](https://github.com/TriasDev/tabular/tree/main/samples/TriasDev.Tabular.Samples.Import),
which the build compiles — so the code on this page is code that works.

**Declare the fields once.** They are both the schema and the accessors that read a row:

```csharp
--8<-- "samples/TriasDev.Tabular.Samples.Import/Program.cs:schema"
```

**Profile the file and build a plan from its headers.** In an application, a person confirms or
changes the plan on a mapping screen; the profile is what that screen shows:

```csharp
--8<-- "samples/TriasDev.Tabular.Samples.Import/Program.cs:profile"
```

**Check the plan against the profile**, before the file is read again:

```csharp
--8<-- "samples/TriasDev.Tabular.Samples.Import/Program.cs:precheck"
```

**Import:** typed rows, or errors that name their row, column and code:

```csharp
--8<-- "samples/TriasDev.Tabular.Samples.Import/Program.cs:import"
```

Run it with `dotnet run --project samples/TriasDev.Tabular.Samples.Import`: the sample file has a date
that is not one and an empty required amount, and both come back as row errors.

## Write a file

The other direction, from
[`samples/TriasDev.Tabular.Samples.Export`](https://github.com/TriasDev/tabular/tree/main/samples/TriasDev.Tabular.Samples.Export),
which the build compiles too.

**Declare the export once.** A column is a header and a lambda, whose type picks the column's type;
a style rule picks a style per cell, and the sheet options give the header a style, freeze it and put
an auto-filter on it:

```csharp
--8<-- "samples/TriasDev.Tabular.Samples.Export/Declared.cs:declare"
```

**Write it into any stream** — a file, a blob, an ASP.NET Core response; it need not seek. Chunks are
the fast source, and memory holds a chunk and about a megabyte:

```csharp
--8<-- "samples/TriasDev.Tabular.Samples.Export/Declared.cs:chunks"
```

`TabularFormat.Csv`, `Ods` and `Zip` (a zip of csv sheets) are the same call; csv ignores the styles.
The file is valid only once the write has returned: one that failed is incomplete, and you discard
it. A value the format cannot hold exactly — a double past 15 significant digits, a date xlsx cannot
store — fails the write with a `TabularWriteException` that names its sheet, row and column, rather
than being rounded.

Run it with `dotnet run --project samples/TriasDev.Tabular.Samples.Export`: it writes each kind of file
and reads it back.

## Next

- [How it works](concepts.md) — why analysis and import are two reads, what a profile measures, and how writing works
- [Importing](importing.md) — batches, rules of your own, translated fields, the precheck in depth
- [Exporting](exporting.md) — the row-by-row writer, data by column, styles, layout, ASP.NET Core responses, failures
- [Formats](formats.md) — what csv, xlsx, ods, zip, tar and gzip files read as, and what each format holds when written
