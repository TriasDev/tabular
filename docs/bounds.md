# Bounds

| | Default | Why |
|---|---|---|
| Archive entries | 16,384 | The directory is read before anything else, and each entry costs a sniff |
| Archive expansion | 8 GB | Every entry's declared size together; an archive may carry a zipped ods at that format's own budget |
| Workbook inside an archive | 256 MB | Held in memory while its sheets are read, one at a time. A server may raise it to any size, past 2 GB too |
| Package expansion | 2 GB (ods: 8 GB) | A zip's ratio is unbounded by design; a 50 MB upload could otherwise become fifty gigabytes. OpenDocument writes about four times the bytes for the same cells, so its budget is four times larger |
| Package parts | 16,384 | Every entry's metadata is materialised to find parts by name, before any budget can be consulted |
| Worksheets | 4,096 | One descriptor per sheet, held for the cursor's life and walked by anything that analyses the file |
| Workbook relationships | 8,192 | A map built before anything reads from it; a workbook declares about one per sheet |
| Shared string entries | 1,048,576 | The sheet's own row count — more distinct strings than a column can hold cells |
| Shared string characters | 64 M | What a table costs is its characters, not its entries — a million entries of two thousand each is 3.9 GB |
| Cell formats | 100,000 | Read in the constructor, before a caller has anything to cancel with; the format itself stops at 65,490 |
| Cell value length | 16 M chars | A value is assembled from as many small runs as a file cares to write, none of them large |
| Package metadata string | 2,048 chars | A sheet name, a relationship target, a format code — each had a ceiling on how many, none on how long |
| Columns per row | 16,384 | The workbook format's own width. A csv has none, and a file of nothing but delimiters is the cheapest attack there is |
| Cells repeats hand out (ods) | 100,000,000 | The row and column ceilings bound a sheet's shape, not the work: one cell repeated across every column of a row repeated a million times is under a kilobyte and seventeen billion cells. The copies count, across the file |
| Rows holding a value (ods) | 1,048,576 | OpenDocument repeats a row or cell with one attribute. An empty repeat only moves the position along; one that holds a value is expanded, so it is bounded here and by the column ceiling — twenty characters of markup could otherwise ask for a billion cells |
| Field length (csv) | 16 M chars | Held twice while a quoted field is open, once as the value and once as the text kept for a replay |
| Quoted field length | 100 lines | Beyond that an opening quote was never syntax. In a table of five columns or more a stray quote is caught sooner, by swallowing a record's worth of delimiters |
| Distinct tracking | 2,000,000 values | Exact counting costs memory in proportion; the budget is per file, not per column |
| Retained distinct values | 1,000 per column | Enough to judge a column of codes against a reference set; a column with more is not one |
| Error rows | 1,000 | A wrong mapping fails every row, and the thousand-and-first error says nothing the first did not |
| Rows per sheet | 2,147,483,647 (csv), 1,048,576 (xlsx) | Row numbers and counts are `int`: the workbook format stops at a million rows, and a csv that long is some 100 GB |

Getting these right took three attempts, and the pattern of the mistakes is worth more than the
numbers. The package budget counts bytes a part expands to, which is not what those bytes become in
the heap — so a shared string table needed a ceiling of its own, and the first such ceiling counted
entries, which a million two-thousand-character entries satisfy at a cost of 3.9 GB. Then a review
found three more collections growing from the file with no ceiling at all, one of them beside a list
that had just been given one. Every ceiling here now guards a quantity that was enumerated rather than
noticed — but the package budget above them still counts the wire, which is why each of the others
exists.

Distinct counting stores 64-bit hashes rather than strings. By the birthday bound the chance of an
accidental collision is about one in 37 million over a million values, and about one in 9 million
over the two million the default budget tracks — it rises with the count, not falls. Small enough to
call the count exact; not zero, and a collision undercounts.
