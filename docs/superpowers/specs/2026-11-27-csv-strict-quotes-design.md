# Csv: reading quotes strictly (#198) — design

## Why

The csv reader repairs malformed quoting, and one of its repairs splits valid csv: a quoted field that
spans lines and holds a record's worth of delimiters (tables of five columns or more), or more than
`MaxQuotedFieldLines` line breaks, is replayed as records. An Excel export of a cell with a line break
and a few commas is exactly that shape. A caller that reads small, user-authored files and turns each
row into work would rather refuse a file than misread a row, and today its only signal is
`Diagnostics.IsClean`, which also trips on the valid export.

## Goals

- An opt-in mode that reads quotes as RFC 4180 does and refuses, rather than repairs, quoting that
  could be misread.
- In that mode, whitespace around a quoted field is insignificant: `1, "Payroll", "monthly, HR"` reads
  as `1 | Payroll | monthly, HR`.

## Non-goals

- The default mode does not change, in behaviour or in speed.
- The writer (`write.ambiguous-line-breaks`) stays as it is; a strict writer is a possible follow-up.
- No new diagnostics counter.

## Approaches

- **A.** Only the issue's three switches: no delimiter-count replay, no line-bound replay, an
  unterminated quote at the end refused. `"C" Road` would still be repaired silently.
- **B (chosen).** A, plus refusing text after a closing quote (`"C" Road`; `135"th` past a line
  ending). A quote inside an unquoted field (`21"st`) stays content: lossless, and what Excel does.
- **C.** Full RFC, refusing `21"st` too — refuses ordinary files with inch marks.

## Design

`CsvCursorOptions.QuoteMode`, of a new public enum `CsvQuoteMode { Repair = 0, Strict = 1 }`;
`Repair` is the default and today's behaviour. An undefined value is refused by `Checked()`.

In `Strict`:

- A quote opens a quoted field at the start of a field, or after nothing but whitespace (space, and
  tab where tab is not the delimiter); that whitespace is dropped.
- Inside quotes, `""` is a quote. A quote followed by optional whitespace and then a delimiter, a line
  ending or the end of the input closes the field; the whitespace is dropped. Anything else after a
  closing quote is refused.
- A quoted field may span any number of lines and hold any number of delimiters: no replay at all.
  `MaxFieldChars` still bounds it. `MaxQuotedFieldLines` does not apply.
- The end of the input inside a quoted field is refused.
- The raw text kept for a replay is not kept, so a quoted field is held once, not twice.
- `Diagnostics` stays clean: nothing is repaired.

Refusals are `TabularFormatException` with `format.corrupt`, the message naming the row (the record
number the cursor would have given). The cursor is faulted afterwards, as for any failed read. The
same options reach csv files inside archives and gzip, so they read the same way.

Dropping the whitespace changes no value: `RawCell.FromText` trims text anyway.

## Left to the builder

Performance: the default path gets a branch on a local per quote and per quoted run at most;
measured against `main` before merging.
