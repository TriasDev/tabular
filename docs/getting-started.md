# Getting started

## Install

```bash
dotnet add package TriasDev.Tabular
```

The package targets .NET 8 and .NET 10 and references nothing but the base class library.

## Read a file

`TabularFile.Open` decides the format from the file's bytes — csv, xlsx, ods, or a zip archive of
them — and returns a cursor over its sheets and rows:

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

## Next

- [How it works](concepts.md) — why analysis and import are two reads, and what a profile measures
- [Importing](importing.md) — batches, rules of your own, translated fields, the precheck in depth
- [Formats](formats.md) — what csv, xlsx, ods and zip files read as
