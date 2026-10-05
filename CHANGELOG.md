# Changelog

## [0.7.0](https://github.com/TriasDev/tabular/compare/v0.6.0...v0.7.0) (2026-10-05)


### Features

* **write:** a workbook column is never narrower than its header ([#115](https://github.com/TriasDev/tabular/issues/115)) ([59dfdc4](https://github.com/TriasDev/tabular/commit/59dfdc44a9045b7f535ceb21ca92910c7fc1741e))

## [0.6.0](https://github.com/TriasDev/tabular/compare/v0.5.0...v0.6.0) (2026-10-05)


### ⚠ BREAKING CHANGES

* the concrete cursors (CsvCursor, XlsxCursor, OdsCursor, ArchiveCursor, GzipCursor) and CsvDialectDetector are internal, so TabularFile.Open is the only way to a cursor; every public type is in the TriasDev.Tabular namespace, the format options included; CsvCursorOptions.Dialect is replaced by the Delimiter, Encoding and Quote hints and CsvDialect is output-only; the writer reports its limits as TabularWriteException (write.too-many-rows, write.too-many-merges, write.too-many-styles) instead of TabularLimitException; row numbers are int on both sides (TabularExport returns ValueTask<int>, AnalysisProgress.RowsRead is int); HorizontalAlignment is CellHorizontalAlignment, TabularWriter.Style is RegisterStyle, CellStyle.Number and Date are NumberFormat and DateFormat; NumberFormat.Parse and DateFormat.Parse throw FormatException; typed import fields come only from their factories; CursorDiagnostics has no public constructor.

### Features

* count a column's rows that fall in a set of values ([5b773ae](https://github.com/TriasDev/tabular/commit/5b773ae7b1d0d4ab8bb48992186c6c446d768744))
* declare field groups as alternatives in priority order ([d819eff](https://github.com/TriasDev/tabular/commit/d819eff3885a2ca2762a50a21681e7f558f0d993))
* hand a mapper the group that locates its row ([5b6ba1d](https://github.com/TriasDev/tabular/commit/5b6ba1db2afbabba9b845086f98ab31ecc3fbcd9))
* judge field alternatives per row during extraction ([8adec9f](https://github.com/TriasDev/tabular/commit/8adec9f1c806ff757ed7eacec9f5e37cc26ba655))
* precheck what a mapping decides about field alternatives ([a27d6c6](https://github.com/TriasDev/tabular/commit/a27d6c6d159eaf37300a4e78534aca28f36d5f34))
* read gzip-compressed files as the file inside them ([#80](https://github.com/TriasDev/tabular/issues/80)) ([78e1223](https://github.com/TriasDev/tabular/commit/78e12232f899fe402c00c5e8db72a029fbfeb77a))
* read tar and tar.gz archives as one workbook ([#82](https://github.com/TriasDev/tabular/issues/82)) ([faf0be7](https://github.com/TriasDev/tabular/commit/faf0be791bb5a8ea58e4b12358c3c603b7814866))
* report how each set of alternatives covers the rows ([d691f7c](https://github.com/TriasDev/tabular/commit/d691f7c07f36fe4945263ed649b1f95f4f16a057))
* review a whole file through a mapping before importing it ([ac2758c](https://github.com/TriasDev/tabular/commit/ac2758caff0532dab820be1310c6ea061e061696))
* **write:** cell styles for xlsx and ods ([#90](https://github.com/TriasDev/tabular/issues/90)) ([45dbca8](https://github.com/TriasDev/tabular/commit/45dbca8a38bfae9999ddba52ee87808e55ec40cf))
* **write:** ColumnBatch — rows given by column, typed, with a style per column ([#93](https://github.com/TriasDev/tabular/issues/93)) ([13f2807](https://github.com/TriasDev/tabular/commit/13f28070b0824b320b8031b850602ec9b117272d))
* **write:** sheet layout — header style, frozen panes, auto-filter, merged cells ([#91](https://github.com/TriasDev/tabular/issues/91)) ([c158848](https://github.com/TriasDev/tabular/commit/c158848f15a9f28709de36ba2fcc8db0e6f4010a))
* **write:** TabularExport&lt;T&gt; — an export declared once with typed columns ([#84](https://github.com/TriasDev/tabular/issues/84)) ([78f2379](https://github.com/TriasDev/tabular/commit/78f2379a62ffb8922380a5878fd32ad3dd4ba069))
* **write:** TabularFormat.Zip — a zip of csv sheets ([#94](https://github.com/TriasDev/tabular/issues/94)) ([1e8e23c](https://github.com/TriasDev/tabular/commit/1e8e23c735b6d8cf7148e490c3e47f2d7bfcb96f))
* **write:** write csv through TabularWriter, round-tripping through the import ([#77](https://github.com/TriasDev/tabular/issues/77)) ([5de3450](https://github.com/TriasDev/tabular/commit/5de345065b61d2bd098e6757dbd2e063a15bbb79))
* **write:** write OpenDocument spreadsheets through TabularWriter ([#83](https://github.com/TriasDev/tabular/issues/83)) ([8567fa1](https://github.com/TriasDev/tabular/commit/8567fa1aef5f049fecfc4fd3057cf18419fa6267))
* **write:** write xlsx through TabularWriter, with our own streaming zip writer ([#81](https://github.com/TriasDev/tabular/issues/81)) ([6451e59](https://github.com/TriasDev/tabular/commit/6451e59f37c720c92438385e6a03d3a38d8ece62))


### Bug Fixes

* an invalid value locates nothing, an unneeded one costs nothing, and the precheck does not block AllOrNothing for rows that import ([547cc20](https://github.com/TriasDev/tabular/commit/547cc20e1d73671b78c4ae47564b40c67b7aa332))
* **archive:** refuse a tar metadata entry that claims more than it can hold, instead of letting TarReader's InvalidOperationException escape ([#99](https://github.com/TriasDev/tabular/issues/99)) ([7363a42](https://github.com/TriasDev/tabular/commit/7363a4224959645a3cc31c34f788010acbc3c75f)), closes [#92](https://github.com/TriasDev/tabular/issues/92)
* **csv:** find the delimiter when the dialect probe ends inside a multi-line quoted field ([#101](https://github.com/TriasDev/tabular/issues/101)) ([916adf8](https://github.com/TriasDev/tabular/commit/916adf86832a5a230d9cb3fb543eaac99ae3f823))
* the deferred archive and OpenDocument findings ([#102](https://github.com/TriasDev/tabular/issues/102)) ([d88ea71](https://github.com/TriasDev/tabular/commit/d88ea710a7957b3bb57ec85101c7f683f85f67b6))
* **write:** keep a failed write's exception on close; sheet options for a declared export ([#98](https://github.com/TriasDev/tabular/issues/98)) ([0fea097](https://github.com/TriasDev/tabular/commit/0fea097b901177dab86c2bc5b0a2e85d0409c91d))


### Code Refactoring

* the public API's last breaking window before 0.6.0 ([#108](https://github.com/TriasDev/tabular/issues/108)) ([8d6825a](https://github.com/TriasDev/tabular/commit/8d6825a70f86d8c04f5c97bab1216aea9b04b27c))

## [0.5.0](https://github.com/TriasDev/tabular/compare/v0.4.0...v0.5.0) (2026-09-29)


### Features

* count the rows of every distinct value, and give precheck rule findings their exact affected rows ([#64](https://github.com/TriasDev/tabular/issues/64)) ([52ef740](https://github.com/TriasDev/tabular/commit/52ef740e60c8c59d268f76a81cfd4d053855f58e))


### Bug Fixes

* **analysis:** a date written with a culture's own separator ranks that culture first, and the facts say when date readings disagree ([#59](https://github.com/TriasDev/tabular/issues/59)) ([1fa72b3](https://github.com/TriasDev/tabular/commit/1fa72b3e03d46b9c5a448e5a553d839042e6b2f6))
* **analysis:** numbers that read under both separators rank the one the sheet's evidence favours, and the facts say when number readings disagree ([#62](https://github.com/TriasDev/tabular/issues/62)) ([8b14042](https://github.com/TriasDev/tabular/commit/8b140420190bd4461e58aa2915489a8d763e8540))

## [0.4.0](https://github.com/TriasDev/tabular/compare/v0.3.0...v0.4.0) (2026-09-28)


### Features

* count, and on request import, a decimal written with the other separator where it can be read no other way ([42c26ad](https://github.com/TriasDev/tabular/commit/42c26adb28a6e4974e70abad5bc982e36b1342cf))
* say whether a sheet is hidden, on SheetInfo and SheetProfile ([#55](https://github.com/TriasDev/tabular/issues/55)) ([88820a6](https://github.com/TriasDev/tabular/commit/88820a67a26a6c6291ce9e4a46ba112a8782b627))


### Bug Fixes

* **analysis,import:** read a decimal written in exponential notation, in the profile and the import alike ([#52](https://github.com/TriasDev/tabular/issues/52)) ([b174704](https://github.com/TriasDev/tabular/commit/b174704ec909bcf6d38fb105b681cb217ff8b9b5))
* **analysis:** empty cells past a row's last value are padding, not columns ([#53](https://github.com/TriasDev/tabular/issues/53)) ([f9783ba](https://github.com/TriasDev/tabular/commit/f9783ba6c2e8a3149b6c39460b0f145bf1044df5))
* **analysis:** when the distinct budget runs out, settled and rightmost columns give theirs up, so the key keeps an answer ([#54](https://github.com/TriasDev/tabular/issues/54)) ([eea4c64](https://github.com/TriasDev/tabular/commit/eea4c6467053dce00592dfc6bcff3eeb5a1063f8))
* **csv:** a quoted value whose line would overfill the record is not read as a stray quote ([69a7b47](https://github.com/TriasDev/tabular/commit/69a7b4792b9aac2d3deb1511215434cfc3105431))
* **mapping:** a pattern on the linear engine runs without a clock, so a valid value cannot fail on a slow first match ([994cb1f](https://github.com/TriasDev/tabular/commit/994cb1f67ddf7964674cfd7b0fe7faa29274baef))
* **xlsx,ods:** read a line end written as it is the way XML does, as a line feed ([fc203bb](https://github.com/TriasDev/tabular/commit/fc203bba43d4ccc0a70a2bc7808858cc7cbfa31f))
* **xlsx:** a number format counts only inside numFmts, not in a conditional format's dxf ([43cbda2](https://github.com/TriasDev/tabular/commit/43cbda2fb7c78c8bf9d2ac9a98d54ac247a85bae))
* **xlsx:** decode the _xHHHH_ escapes OOXML writers use for characters in strings ([6b1b0b4](https://github.com/TriasDev/tabular/commit/6b1b0b402ee2262957e154ce6f473bcb768fabad))
* **xlsx:** read a boolean cell holding no xsd:boolean as its text, not as false ([58f7eb3](https://github.com/TriasDev/tabular/commit/58f7eb3c164f213c04494985f38a402a43cdc34e))
* **xlsx:** read a boolean written as true or false, as the Open XML SDK writes it ([b13a703](https://github.com/TriasDev/tabular/commit/b13a703cedc26c3655281f4f98ba018f6cc2ff2c))
* **xlsx:** read a value on past text that follows a CDATA section ([9b0d785](https://github.com/TriasDev/tabular/commit/9b0d785bb439af3f77cca0d89d209ed5090aedfb))


### Performance Improvements

* the other-separator check runs only on values that could be numbers, with the separator looked up once ([b9ba1c5](https://github.com/TriasDev/tabular/commit/b9ba1c5c386b49633d4be44abb0c5769d68ea045))

## [0.3.0](https://github.com/TriasDev/tabular/compare/v0.2.0...v0.3.0) (2026-09-27)


### ⚠ BREAKING CHANGES

* TabularExtractor.Start → TabularExtractor.Extract, and ExtractionSession → ExtractionRun; its members are unchanged.
* precheck argument keys renamed — readableCount → judgedCount (PrecheckArguments.ReadableCount → JudgedCount), profileHeaderRow → profileHeaderRowIndex, planHeaderRow → planHeaderRowIndex, boundColumns → boundColumnCount. A schema with a typed field whose Type is not its class's is refused with ArgumentException. The constraints, now classes, no longer have init setters on Length and Value.
* the public API is reshaped once for 1.0. Old → new:
    - `new TabularAnalyzer(options).Analyze(cursor, progress, ct)` → `TabularAnalyzer.Analyze(cursor, options, progress, ct)`
    - `TargetField` → `ImportField` (also the factory: `ImportField.Text(...)` and the others)
    - `TextField` / `IntegerField` / `DecimalField` / `DateField` / `BooleanField` → `TextImportField` / `IntegerImportField` / `DecimalImportField` / `DateImportField` / `BooleanImportField`
    - `TranslatedField` → `TranslatedImportField`; `TargetSchema` → `ImportSchema`
    - `SourceColumnIndex` → `ColumnIndex` and `TargetFieldName` → `FieldName` on `ColumnBinding`, `RowError`, `MappingFault`, `PrecheckFinding`; `TabularStructureException.SourceColumnIndex` → `ColumnIndex`; `ColumnBinding.SourceHeader` → `Header`
    - new `ITabularProblem`, implemented by `RowError`, `MappingFault`, `PrecheckFinding`
    - `PrecheckFinding.Detail` removed → `AffectedRowsBound`, `Examples`, `Arguments` (`PrecheckArguments`, `PrecheckReasons`); `PrecheckFinding.FieldName` / `ColumnIndex` are nullable
    - `foreach (var o in run)` → `run.ReadRows(ct)`; `Rows` → `ReadRows`, `InChunks` → `ReadChunks`, `All` → `ReadAll`; `ImportRun<T>` is not an `IEnumerable`
    - `ExtractionSummary` is an immutable record; `Summary` properties return snapshots
    - fields, `ImportSchema` and constraints are classes (no `with`, reference equality); plans, bindings, options, profiles and results compare by value; `CursorDiagnostics` is a record
    - `ITabularCursor.MoveToSheet(int)` → `MoveToSheet(int, CancellationToken = default)`
* ITabularCursor.MoveToSheet(int) is MoveToSheet(int, CancellationToken = default); a cursor implemented outside the library must add the parameter.
* ImportRun<T> no longer implements IEnumerable; `foreach (var o in run)` is `foreach (var o in run.ReadRows(ct))`. Rows → ReadRows, InChunks → ReadChunks, All → ReadAll. ExtractionSummary is an immutable record: ImportRun<T>.Summary and ExtractionSession.Summary return a snapshot rather than a live object.
* ImportField (and TextImportField and the other typed fields), ImportSchema, FieldConstraint and its nested rules are classes, not records: `with` expressions, value equality and deconstruction on them are gone. CursorDiagnostics is a record. Collections in MappingPlan, ColumnBinding, AnalysisOptions, FileProfile, SheetProfile, ColumnProfile, ColumnFacts, CultureParseCounts, TypeHypothesis, PrecheckFinding, PrecheckResult, ImportPreviewRow and ImportResult compare by value.
* PrecheckFinding.Detail is removed; its data is in AffectedRowsBound, Examples and Arguments. PrecheckFinding.FieldName is string? and ColumnIndex is int?, null where a finding concerns no field or column.
* renamed types — TargetField → ImportField (now also the factory; the static class ImportField is merged into it), TextField → TextImportField, IntegerField → IntegerImportField, DecimalField → DecimalImportField, DateField → DateImportField, BooleanField → BooleanImportField, TranslatedField → TranslatedImportField, TargetSchema → ImportSchema. Renamed members — SourceColumnIndex → ColumnIndex and TargetFieldName → FieldName on ColumnBinding, RowError, MappingFault and PrecheckFinding; TabularStructureException.SourceColumnIndex → ColumnIndex; ColumnBinding.SourceHeader → Header. A plan stored with the old property names must be migrated.
* `new TabularAnalyzer(options).Analyze(cursor, progress, ct)` is now `TabularAnalyzer.Analyze(cursor, options, progress, ct)`; the constructor and the instance overloads are gone.

### Features

* an import run reads once, through ReadRows, ReadChunks or ReadAll ([53a5248](https://github.com/TriasDev/tabular/commit/53a5248988c8bcb8029a200d1fc7c8de017f2d19))
* MoveToSheet takes a cancellation token ([362737a](https://github.com/TriasDev/tabular/commit/362737a01318eded2f99ab160346cd6133dadec6))
* precheck findings carry their data instead of an English sentence ([d45f2ee](https://github.com/TriasDev/tabular/commit/d45f2ee2aa435ea3c0758fed2945cf4ceeb81a3c))
* TabularAnalyzer is static, its options passed to Analyze ([47a84e7](https://github.com/TriasDev/tabular/commit/47a84e7740189ba27307ebb77ad220b429fb26e1))


### Bug Fixes

* findings of the final reviews of the 1.0 API ([b7e5bd3](https://github.com/TriasDev/tabular/commit/b7e5bd33dee740e62994faeba30e74b9f2cf8d24))


### Documentation

* the documentation follows the 1.0 API ([f80cf99](https://github.com/TriasDev/tabular/commit/f80cf99fcee454ebfaa4a01d839d86b603961c2d))


### Code Refactoring

* data compares by value; fields, constraints and schemas are classes ([b947e0b](https://github.com/TriasDev/tabular/commit/b947e0b84dd262b83db8ac482154354ee0550c8f))
* TabularExtractor.Extract returns an ExtractionRun ([4c53e2a](https://github.com/TriasDev/tabular/commit/4c53e2a5bdf0e14c374bf86ff3550a62381777bb))
* unique names for fields and schemas; one shape for a problem ([fec1071](https://github.com/TriasDev/tabular/commit/fec1071db8f081c12029fb97b257ada4b64ab2b0))

## [0.2.0](https://github.com/TriasDev/tabular/compare/v0.1.0...v0.2.0) (2026-09-26)


### Features

* **archive:** open a zip archive by its contents ([49cde9b](https://github.com/TriasDev/tabular/commit/49cde9bcce57d3cd5a05d0b7cbdcfc555ea74b25))
* **archive:** options, the zip format and skipped entries ([a0aea63](https://github.com/TriasDev/tabular/commit/a0aea6314bcaeb716a469fbe7cb44fe63eab9259))
* **archive:** read the csv files in a zip archive as sheets ([59b086c](https://github.com/TriasDev/tabular/commit/59b086c866370bba800d0a38ade9aa9f1b16522b))
* **archive:** read xlsx and ods workbooks inside an archive ([2bcdf09](https://github.com/TriasDev/tabular/commit/2bcdf09e712e35fbb9430ff5afdc29ba0a3bda58))
* **ods:** read OpenDocument spreadsheets ([5f5f927](https://github.com/TriasDev/tabular/commit/5f5f927db11ca3189cdeb9bcf887095d75cb2bc1)), closes [#13](https://github.com/TriasDev/tabular/issues/13)


### Bug Fixes

* **archive:** findings of the final review ([88bcd8f](https://github.com/TriasDev/tabular/commit/88bcd8f0789072b371dfa6243a78b0800963541b))
* **csv:** read quoted notes of any length, catching stray quotes by what they swallow ([b550474](https://github.com/TriasDev/tabular/commit/b550474637e6747c7d347e4b2d0d2a5c7bbd72df))
* **csv:** refuse an XML document instead of reading its markup as lines ([efd98fc](https://github.com/TriasDev/tabular/commit/efd98fca1fcd203579abed47c4f4630a50f287e4))
* drop the raw HTML image from the readme, which nuget.org shows as text ([2ccdbbf](https://github.com/TriasDev/tabular/commit/2ccdbbfac8accbbbc09a384bc295006a861490eb))
* **ods:** findings of two independent reviews ([a9324a9](https://github.com/TriasDev/tabular/commit/a9324a993954d3fbcad376cb03b210290b48b571))
* **xlsx:** read a written-out bare time on the day a serial time reads on ([010aca1](https://github.com/TriasDev/tabular/commit/010aca13ee717376839c82522f231d324e903b2c))


### Performance Improvements

* **analysis:** ask the culture-free shape questions once per value, not once per culture ([eba9e27](https://github.com/TriasDev/tabular/commit/eba9e27007a2c9e80e84ff5e52712cdae69dbbca))
* **analysis:** count cell kinds in an array, not a dictionary ([2b06499](https://github.com/TriasDev/tabular/commit/2b06499749549ddd1c0538f5d1710d27f0a722e2))
* **analysis:** keep distinct-value hashes in a flat open-addressing set ([c8eb8f6](https://github.com/TriasDev/tabular/commit/c8eb8f6fed69dbc92942226623ae302204e47f24))
* **analysis:** read a number once for cultures that write numbers alike ([94fd7a8](https://github.com/TriasDev/tabular/commit/94fd7a82351cc4eca6d81b09d910b665ae2db500))
* **csv:** make a field that is one run of the buffer straight from the buffer ([d526580](https://github.com/TriasDev/tabular/commit/d5265802ed6814252723c924e9498e86f9c2364e))
* **csv:** take runs of ordinary text inside quotes as one span too ([64d527b](https://github.com/TriasDev/tabular/commit/64d527b2d10e0d7ceb88f2f5ef396eea7c817781))
* **csv:** take runs of ordinary text outside quotes as one span, found by a vectorised search ([b1273a5](https://github.com/TriasDev/tabular/commit/b1273a5ed5478fbe6300157c2c2364d5a2372f9a))

## 0.1.0 (2026-09-25)


### Features

* **benchmarks:** a synthetic fixture generator, so the published numbers can be reproduced ([c913bd2](https://github.com/TriasDev/tabular/commit/c913bd2ef70bf70cdf93c1a5d77f900a5f4a2a55))
* let callers add their own value rules ([4c0ec6c](https://github.com/TriasDev/tabular/commit/4c0ec6c2efbb308c334b71b528bcc46a62d0219b))
* public ErrorCodes constants for every code the library reports ([4bd9567](https://github.com/TriasDev/tabular/commit/4bd95670088cb09bb2c2dc7a3a8a1238bcaedf0d))
* report progress while analysing a file ([0428a2a](https://github.com/TriasDev/tabular/commit/0428a2a5bd3bef9ff304b403378e8157d26f2211))


### Bug Fixes

* a successful MoveToSheet clears a fault from the sheet before ([#14](https://github.com/TriasDev/tabular/issues/14)) ([ef3589f](https://github.com/TriasDev/tabular/commit/ef3589f8643e27cac8aec0bd037ea4ec8adf202f))
* a zoned timestamp is read as the clock time it states, on every server ([e8b7883](https://github.com/TriasDev/tabular/commit/e8b788302d0d57c6031582ca977e8c9673981f60))
* an empty shared string no longer hangs the xlsx reader ([52c5fc2](https://github.com/TriasDev/tabular/commit/52c5fc278fac7b91180746375578129ec027200e))
* **csv:** read records joined by a pair of stray quotes as records, and count the repair ([6832d78](https://github.com/TriasDev/tabular/commit/6832d784bf1a12790d6e564c8623f0c9d98502c1))
* files in formats the library does not read are refused by name ([7f5571f](https://github.com/TriasDev/tabular/commit/7f5571fd4883d5d509a6352918132e42a5c7e264))
* find workbook parts through the package relationships ([8eb6890](https://github.com/TriasDev/tabular/commit/8eb689083b0735439a5ee086b662269ed8d2ddda))
* four value errors found against other readers' corpora ([7ed3376](https://github.com/TriasDev/tabular/commit/7ed337603af17455a5bd62464ff398398f950bfc))
* HeaderRowIndex names a spreadsheet row, the numbering every report uses ([4577245](https://github.com/TriasDev/tabular/commit/457724547097c5fc05ddb31c2ff8c8dc344b720b))
* malformed parts and truncated worksheets are refused instead of leaking or shortening ([d19aaf2](https://github.com/TriasDev/tabular/commit/d19aaf28027debaeb191b2ffc8c39b838292f70e))
* options are validated up front, and invariant globalization degrades instead of failing ([d7fff36](https://github.com/TriasDev/tabular/commit/d7fff3643187c4b979f9442edf819fc73c68c4ec))
* read worksheets whose markup has elements with many attributes ([3b3add5](https://github.com/TriasDev/tabular/commit/3b3add5b0b427482c2796d9d9b395045731ee1a2))
* the import applies the profiler's number-grouping rule, and honours AllOrNothing ([406f6f3](https://github.com/TriasDev/tabular/commit/406f6f3acce1fc37fcda4f620958cc1983cfc2be))
* the precheck judges a workbook's own numbers, dates and booleans against the field ([dbf5855](https://github.com/TriasDev/tabular/commit/dbf585507875b832f0068436356283a140c08295))
* workbook row numbers only ever increase ([b18cd0b](https://github.com/TriasDev/tabular/commit/b18cd0bb2181d6cdd063bb668c5ddae6d4ec0432))


### Performance Improvements

* read the shared string table with one chunk buffer, not one per entry ([87104be](https://github.com/TriasDev/tabular/commit/87104be2e47e905f914d573610f21739d2ca7e41))


### Code Refactoring

* one exception hierarchy with a code on every instance ([34ab68f](https://github.com/TriasDev/tabular/commit/34ab68f1c8bb5e1d033ab8367ecf2e6e2e6f1517))
* one namespace for the workflow ([4dbd4a3](https://github.com/TriasDev/tabular/commit/4dbd4a33f3b54e744059b64b1bf25efa5dd632d4))
