# Producer fixtures

One small table written by each producer, so the readers are held to what real writers emit rather
than to what our own fixture builders do. The test that reads them is `ProducerFixtureTests.cs`.

| File | Written by |
|---|---|
| `closedxml.xlsx` | ClosedXML 0.105.1 |
| `openxml-sdk.xlsx` | DocumentFormat.OpenXml 3.5.1, cells built by hand as code using the SDK directly would |
| `epplus4.xlsx` | EPPlus 4.5.3.3 |
| `npoi.xlsx` | NPOI 2.7.6 |
| `miniexcel.xlsx` | MiniExcel 1.46.0 |
| `sylvan.xlsx` | Sylvan.Data.Excel 0.5.8 |
| `libreoffice.xlsx`, `libreoffice.ods`, `libreoffice.csv` | LibreOffice 26.8.0.3, from `closedxml.xlsx` |

The xlsx files come from `benchmarks/TriasDev.Tabular.Comparison`:

```sh
dotnet run -c Release --project benchmarks/TriasDev.Tabular.Comparison -- write-producers <folder>
```

The LibreOffice ones from its headless converter, with a German user interface (hence `WAHR`, `1,5`):

```sh
soffice --headless --convert-to 'xlsx:Calc Office Open XML' closedxml.xlsx
soffice --headless --convert-to ods closedxml.xlsx
soffice --headless --convert-to 'csv:Text - txt - csv (StarCalc):59,34,76,1,,0,false,true' closedxml.xlsx
```

Google Sheets and Numbers are not here: neither can be driven from a script, and a file saved by hand
cannot be regenerated when the table changes.
