# Fixture data and preview

## Previewing a table (`preview`)

`sm preview <model> <table>` shows the first rows of a table, like Tabular Editor 3's table preview:

```
sm preview ./Sales.pbip Customer --data ./fixture                 # offline: the first 10 rows
sm preview ./Sales.pbip Customer --data ./fixture -n 3 -c Name,Country --csv
sm preview "xmla:powerbi://api.powerbi.com/v1.0/myorg/Sales;Sales" Customer --json
```

- Model files need `--data <fixture>`: the local engine evaluates the table (`EVALUATE 'Customer'`) and the first rows
  in fixture order are shown. Nothing leaves the machine.
- A server model (`xmla:`, as for [`query`](xmla.md#query)) runs `EVALUATE TOPN ( N, 'Customer' )`, so the server does
  only that much work (it asks for N + 1 rows, to know whether there are more). TOPN without an order "does not
  guarantee any sort order" (dax.guide), so these are the rows the server returns first.
- `--rows N` / `-n` (default 10), `--columns a,b` / `-c` (repeatable; in that order; names match ignoring case),
  `--csv`. `--json` writes `{"command": "preview", "source", "table", "results", "durationMs"}`, with `results` in
  the shape of [`query`](xmla.md#query)'s; `truncated` is true when the table has more rows than shown.
- An unknown table or column is exit 3 with close names, as everywhere. On a server, the table is the server's to
  find, and its error is exit 5.

## Fixture data (`data`)

A fixture is a directory of data files, one per table, that the local DAX engine evaluates a model against: no
server, no refresh. `sm data` creates one, checks it against the model, and summarizes it. `sm query <model>
--data <dir>` runs queries over it: see [the local DAX engine](dax-engine.md#the-local-dax-engine-query---data). `sm test`
runs a model's tests over it: see [testing a model](model-tests.md#testing-a-model-test).

**Fixture data never leaves your machine.** sm reads the files from the directory and does nothing else with
them: no upload, no telemetry, no network call of any kind.

```sh
sm data init  ./Sales.pbip ./fixtures                      # a header-only CSV per table, to fill in
sm data init  ./Sales.pbip ./fixtures --rows 1000 --seed 7 # ... or 1000 rows of synthetic data per table
sm data check ./Sales.pbip ./fixtures                      # files, columns, types, keys, relationships
sm data info  ./Sales.pbip ./fixtures --json               # tables, row counts, distinct counts per column
sm data embed ./Sales.pbip ./fixtures ./with-data.bim      # the model with the fixture's rows inside it
```

### The format

- **One file per table:** `<Table>.csv` or `<Table>.json`, directly in the directory. Other files and
  subdirectories are ignored. File names match table names ignoring case.
- **Which tables:** every table except calculated tables and calculation groups, which the engine computes. A
  table with no file is empty; `check` lists those in one warning.
- **Which columns:** the table's data columns, by their model names (not the source column names), in any order,
  matched ignoring case. Calculated columns are computed by the engine: if a file has one it is ignored, with a
  warning. A data column the file leaves out is BLANK in every row, with a warning.

Table names that can't be file names are escaped with `%XX` (the UTF-8 bytes in hex), so the same directory works
on Windows, macOS and Linux:

| In the table name | In the file name |
|---|---|
| `%` | `%25` |
| `< > : " / \ \| ? *` and control characters | `%3C` `%3E` `%3A` `%22` `%2F` `%5C` `%7C` `%3F` `%2A`, `%09`... |
| a trailing `.` or space | `%2E`, `%20` |
| a Windows device name (`CON`, `PRN`, `AUX`, `NUL`, `COM1`.., `LPT1`..), alone or before a dot | its first letter escaped: `%43ON` |

Everything else is kept as it is, so `Invoiced Sales` is `Invoiced Sales.csv` and `Ventes/Région` is
`Ventes%2FRégion.csv`. `data init` writes the right names. Reading accepts any `%XX`.

**CSV** is RFC 4180: comma-separated, UTF-8 (a byte-order mark is fine), LF or CRLF line ends, the first line
naming the columns.

- A field with a comma, a quote or a line break goes in double quotes; a quote inside quotes is written twice.
- **An empty field is BLANK.** In a text column, `""` (quoted) is the empty string, which DAX tells apart from
  BLANK with `==` and `ISBLANK`. In other columns `""` is BLANK too.
- Nothing is trimmed, except what the engine itself drops: trailing spaces of text.
- A row with more or fewer fields than the header is an error. Empty lines are skipped (in a one-column file an
  empty line is a BLANK row).
- Values are written in the invariant culture, whatever your locale:

| Column data type | Written as | Examples |
|---|---|---|
| Whole number (`int64`) | digits, optional sign | `42`, `-7` |
| Decimal number (`double`) | decimal point, optional exponent; no `NaN` or `Infinity` | `1.5`, `-2.5e-3` |
| Fixed decimal number (`decimal`) | decimal point, at most 4 decimals | `499.95`, `0.0001` |
| True/false (`boolean`) | `true` / `false` (any case), or `1` / `0` | `true` |
| Date/time (`dateTime`) | ISO 8601: a date, a date and time (`T` or a space), or a time | `2024-01-31`, `2024-01-31T13:05:00`, `13:05` |
| Text (`string`) | as it is | `Bike`, `"Lock, heavy"` |

A date/time may end in `Z`, which is dropped: values are never shifted between time zones. Other UTC offsets are
refused. A time alone is that time on 1899-12-30, which is how the engine stores a time.

**JSON** is an array of row objects, for when you want the types explicit:

```json
[
  { "ProductKey": 1, "Name": "Bike", "Price": 499.95, "Active": true, "Launched": "2020-03-01" },
  { "ProductKey": 2, "Name": null, "Price": "49.0000" }
]
```

`null` and a left-out property are BLANK. A JSON value must be of the column's kind: a number for the number
types, `true`/`false` for True/false, a string for text and dates. A JSON string is also accepted for any type and
is read exactly as the same text in a CSV field (so `"9007199254740993"` keeps an integer that a JSON number might
not, and `""` is BLANK except in a text column). Comments and trailing commas are allowed.

As in the engine, text is case-insensitive: values that differ only in case are one value, shown in the casing
that comes first in the file, and `"A"` matches `"a"` across a relationship. A model whose `collation` is
case-sensitive or binary (`..._CS_...`, `..._BIN2`) is loaded case-sensitively. Accents always count.

### `data init`

`sm data init <model> <dir>` writes `<Table>.csv` for every table that takes data: the header row only, or with
`--rows N`, N rows of synthetic data. The files are UTF-8 without a byte-order mark, with LF line ends.

The synthetic data is made to be queryable, not realistic:
- A key, a column marked unique, and the one side of a relationship get a different value in every row.
- The many side of a relationship gets values of its one side, so no row is left without a match.
- Other columns get small numbers, a few repeating texts (`Colour 3`), and dates from 2020 to 2024. Nothing is
  BLANK.
- A table gets fewer rows where it must: a unique True/false column allows two.

The rows depend only on the model, `--rows` and `--seed` (default 1): the same command writes the same bytes on
any machine. `data init` won't replace a table's file, of either format, without `--force` (exit 4); other files
in the directory are left alone. Where a relationship ends at a calculated table (a `CALENDAR()` date table, say), the
key on the fixture's side is generated on its own, and may not match what the engine computes.

### `data check`

`check` reads the fixture and reports findings as `severity  ref  file:line  message  [rule]`, with a summary
line. `--min-severity`, `--fail-on` (default `error`), `--rule` and `--ci` work as in
[`validate`](model-analysis.md#validation-validate): exit 1 if any issue is at or above `--fail-on`, 3 if the directory or model
can't be read.

| Rule | Severity | Reports |
|---|---|---|
| `unknown-table` | error | a `.csv` or `.json` file whose name is no table of the model |
| `duplicate-file` | error | more than one file for a table (`Sales.csv` and `Sales.json`); neither is loaded |
| `calculated-table` | warning | a file for a calculated table or calculation group: ignored |
| `missing-table` | warning | the tables with no file, in one finding |
| `malformed-file` | error | a file that isn't UTF-8, has no header, or stops being CSV or JSON; with the line |
| `malformed-row` | error | a row with the wrong number of fields, or a JSON row that isn't an object |
| `unknown-column` | error | a column the table doesn't have |
| `duplicate-column` | error | a column twice in a header or a JSON row |
| `ignored-column` | warning | a calculated column in a file |
| `missing-column` | warning | a data column the file doesn't have |
| `invalid-value` | error | a value that isn't of its column's data type, with file, line and column |
| `duplicate-key` | error | a value that repeats on the one side of a relationship |
| `blank-key` | error | a BLANK on the one side of a relationship, or in the table's key column |
| `unmatched-key` | warning | rows on the many side with no match on the one side |
| `not-unique` | error | a value that repeats in a column marked as key or unique |
| `unchecked-relationship` | info | a relationship that ends at a calculated table or column, so can't be checked |

`unmatched-key` is a warning because DAX has an answer for it: those rows belong to the blank row of the one-side
table, and the engine will put them there. An invalid value is reported once, as `invalid-value`, and not again
as a BLANK or unmatched key. After 10 invalid values in one column of one file, the rest are counted in a single
finding.

`--json` writes the [findings envelope](ci.md#findings-json-and-ci-annotations) with `"command": "data check"` and
`"source": "data"`. Findings also carry `file` (full path), `line` and `columnName` where they apply, and the
envelope adds `"valid"` (no errors), `"directory"` and `"tables": [{table, file, rows}]`.

### `data embed`

```sh
sm data embed ./Sales.pbip ./fixtures ./Sales-with-data.bim
```

`embed` writes a copy of the model that carries the fixture inside it: every loaded table becomes a calculated
table whose expression is a `DATATABLE` of the fixture's rows. The copy needs no data source, so it can be
deployed to any workspace (`sm deploy`, then refresh it there) and answers queries the way the model does
locally against the fixture. That is how the [conformance corpus](development.md#the-dax-conformance-corpus) is run on a real
server.

- **Kept:** every column with its name, type and properties, so relationships, sort-by columns, hierarchies,
  measures and calculated columns still resolve. Calculated tables and calculation groups are untouched.
- **Removed:** partitions, refresh policies, data sources and shared (M) expressions: what only a refresh needs.
- **Values:** numbers in plain digits, text quoted, `TRUE`/`FALSE`, `BLANK ()`, dates as `dt"2024-01-31T13:05:00"`.
  An empty table keeps its columns and types. Where a constant can't be exactly the value, stderr says so (and
  `--json` lists it under `warnings`): a date/time loses a fraction of a second, a fixed decimal with more than
  15 digits may be rounded by the server, and a table with no data columns gets a hidden column `_`.

The output format follows the destination as for `convert` (`--format bim|tmdl|pbip`), and like `convert` it
refuses a destination that isn't empty without `--force` (exit 4). A fixture with errors is refused (exit 3):
its invalid values would be deployed as BLANK. `--json` writes `{"command": "data embed", "directory", "output":
{format, path, files}, "tables": [{table, rows, columns}], "warnings": [...]}`.

### `data info`

`info` lists each table with its file and row count, and each column with its data type, the number of distinct
values, and the number of BLANK rows. BLANK counts as one of the distinct values. `--json` writes `{"command":
"data info", "directory", "tables": [{table, ref, file, rows, columns: [{name, ref, dataType, distinct, blanks}]}],
"computed": [...], "issues": {errors, warnings}}`. `info` exits 0 even when the fixture has errors (it says so on
stderr); use `check` to see them.
