# Changelog

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
