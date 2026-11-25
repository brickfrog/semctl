# Inspecting and validating models

## Comparing models (`diff`)

`diff` compares two models object by object, in any mix of formats, and prints:

```
~ measure:Sales/Total
    formatString: "#,0" -> "0.00"
    expression:
      - SUM(Sales[Amount])
      + SUMX(Sales, Sales[Amount])
> measure:Sales/Margin -> measure:Sales/Margin %
+ column:Sales/Discount
- role:Readers
```

How objects are matched:
- Objects are matched by reference.
- Relationships are matched by their endpoints (`Sales[DateKey] -> Date[DateKey]`), because their names are
  GUIDs in Power BI.
- A removed and an added object of the same kind that share a `lineageTag` are reported as a rename, with any
  other property changes listed under it.
- When a table is renamed, its columns, measures, levels and partitions follow it, even without lineage tags.
  So does a relationship into it: same relationship, new endpoints, so it shows as modified.

`--stat` prints one line per changed object, and `--name-only` just their references. `--json` writes the
change set:

```json
{ "command": "diff", "summary": { "created": 0, "deleted": 0, "modified": 1, "moved": 1, "total": 2 },
  "changes": [ { "objectPath": "measure:Sales/Net Revenue", "objectType": "Measure", "changeKind": "moved",
                 "movedFromObjectPath": "measure:Sales/Revenue", "label": "...", "properties": [] },
               { "objectPath": "measure:Sales/Margin", "objectType": "Measure", "changeKind": "modified",
                 "label": "measure:Sales/Margin",
                 "properties": [ { "property": "expression", "before": "...", "after": "..." } ] } ] }
```

`changeKind` is `created`, `deleted`, `modified` or `moved` (renamed). The editing commands use the same shape,
the one the Tabular Editor CLI uses.

What is compared:
- Properties are compared using the same paths as `get -p`. `--ignore` takes path wildcards, e.g.
  `--ignore lineageTag,annotations.PBI_*`.
- A change of compatibility level shows up on `model`.
- The order of child objects is not compared.
- Translations show on the object they translate, as `translations.<culture>.caption`, `.description` and
  `.displayFolder`, not on the culture. So renaming or moving a translated object doesn't show every culture as
  changed. `get -p` and `find` use the same paths.
- A perspective's membership is one `members` property, one reference per line, so it diffs line by line:

  ```
  ~ perspective:Finance
      members:
          table:Sales
        + measure:Sales/Budget
  ```

## Dependencies (`deps`)

`deps` reads every DAX expression in the model:
- measure expressions, plus their format string, detail rows and KPI expressions
- calculated columns and calculated tables
- calculation items and calculation group expressions
- row-level security filters
- user-defined functions

It adds structural links: sort-by columns, hierarchy levels, and relationship columns. How a reference resolves:

| Written as | Resolves to |
|---|---|
| `'Sales'[X]`, `Sales[X]` | a column named X in Sales, otherwise a measure |
| `[X]` | a column in the expression's own table, then a measure in it, then a measure anywhere |
| `Sales`, `'Sales'` | the table (or calendar) |
| `Name(...)` | a user-defined function, if one has that name (built-in functions aren't objects) |

Some names belong to the expression itself and are not model references:
- `VAR` names and function parameters, which shadow tables of the same name
- columns created inside the expression, like `"Local"` in `ADDCOLUMNS(t, "Local", ...)`
- `''[X]` columns of anonymous tables

The argument of `NAMEOF([X])` and `TABLEOF([X])` is always a model reference, even when the expression has a
string `"X"` (as a field parameter's label often is).

Comments and strings are never treated as references. A reference that names nothing in the model is listed as
`? <text>`. Each link shows the property and line it comes from, e.g. `expression:5`. `--depth` follows links
further (0 means all the way), and circular references are marked rather than followed.

### Unused objects (`deps --unused`)

`sm deps <model> --unused` lists the tables, columns, measures, hierarchies and functions that nothing uses.
Reports can use any visible object, and sm can't see reports, so by default only hidden objects are listed:
hidden and unused means dead. An object counts as hidden if it, or its table, is hidden or private.
`--include-visible` checks visible objects too: what nothing *in the model* uses.

```
$ sm deps ./Sales.pbip --unused
measure:Sales/Old Margin
column:Sales/Legacy Key  used only by unused measure:Sales/Old Margin
```

Usage is transitive: something used only by unused objects is unused too, and is listed with them (`used only
by unused ...`), so removing those first frees it. An object is used if any of these use it:
- the DAX of a used object (as `deps` reads it), including a used measure's format string, detail rows and KPI
- a used column's sort-by column, variations (their hierarchy and column) and group-by columns (field parameters)
- a used hierarchy's levels
- a calculation item of a used calculation group
- a relationship, row-level security filter, calendar or aggregation column (`alternateOf`): these always
  count as used
- its own parts: a used column, measure or hierarchy keeps its table, and a used table keeps its key column

Some rules of thumb:
- A table reference, such as `COUNTROWS(Sales)`, uses the table but not its columns.
- Perspectives and translations don't count: they list objects, they don't use them.
- Calculated-table columns are listed with a note, since removing one means changing the table's DAX.
- Calculation items, levels, partitions, roles, relationships, M expressions and data sources are never
  listed. sm doesn't read M, so it can't tell which M expressions are used.
- Power BI's auto date/time template table counts as used.
- When in doubt, an object counts as used. For example, a hidden table that only a relationship touches is
  never listed.

`-k` and `-t` narrow the list, as in `list`. `--json` writes an array of `{ ref, kind, name, table, hidden,
usedBy, note }`. The exit code is 0, unless `--fail-on-unused` is given and something is listed (then 1).

## Validation (`validate`)

`validate` checks for structural problems: things Analysis Services or Power BI would reject, or that can't
work. It doesn't check style. Each issue prints as `severity  ref  property:line  message  [rule]`, followed by a
summary line such as `2 errors, 1 warning`.

| Rule | Severity | Reports |
|---|---|---|
| `unresolved-reference` | error | a DAX reference to a table, `'T'[X]` or `T[X]` that doesn't exist (as `deps` resolves it) |
| | warning | an unqualified `[X]` that matches nothing, since it may be a column of a table expression |
| `circular-reference` | error | measures, calculated columns, calculated tables, calculation items or functions that depend on themselves, through their DAX expressions. Each loop is reported once, e.g. `measure:Sales/A -> measure:Sales/B -> measure:Sales/A` |
| `invalid-token` | error | an unterminated string, quoted name, `[name]`, date literal or `/*` comment, or a character DAX doesn't use, in any DAX property |
| `empty-expression` | warning | a measure, calculated column, calculated table, calculation item or function with no DAX |
| `duplicate-name` | error | a column, measure and hierarchy that share a name in one table, or measures that share a name across tables (ignoring case). TOM itself rejects duplicate tables, and duplicate columns or measures within one table, when it loads a model |
| `relationship-datatype-mismatch` | warning | a relationship between columns of different data types |
| `relationship-self-join` | error | a relationship from a table to itself |
| `invalid-sort-by` | warning | a column sorted by itself, or by a column that has its own sort-by column |
| `tom-validation` | error | TOM's own validation, e.g. a sort-by column, level or relationship that names a column that doesn't exist |
| `unknown-function` | error | a call to a function that is neither a DAX function nor one of the model's user-defined functions (ignoring case; `My.Lib.Twice` is one name), e.g. `NoSuchFn ( 1 )`, or a call left behind by `pkg remove --force`. DAX that doesn't parse is skipped |
| `invalid-culture` | error | a model culture or translation language that isn't a culture name at all (`en US`) |
| | warning | a well-formed name this system's culture data doesn't list (`English`, `xx-YY`): a server may still know it, as the lists differ by OS |
| `calculate-variable` | warning | a variable as the first argument of `CALCULATE` or `CALCULATETABLE` (parentheses around it don't matter): it was evaluated where it was defined, so the filters don't change it. Variables as filter arguments are fine, and a function's parameters aren't variables |
| `ignore-outside-summarizecolumns` | error | `IGNORE` anywhere but directly as an expression argument of `SUMMARIZECOLUMNS` (the argument after a name) |
| `week-without-weekly-calendar` | warning | a week-based time intelligence function (`DATESWTD`, `TOTALWTD`, `STARTOFWEEK`, `ENDOFWEEK`, `PREVIOUSWEEK`, `NEXTWEEK`, `OPENINGBALANCEWEEK`, `CLOSINGBALANCEWEEK`) in a model that has calendars but none that can tell weeks apart, or over a named calendar that can't. A calendar can when it tags Week, or Week of Year and Year, or another of Learn's "Period Uniqueness" sets, as the [local engine](dax-engine.md#calendar-based-time-intelligence) reads them. Models without calendars aren't checked |

The last four follow Tabular Editor 3's semantic analyzer (release notes 3.27.1, 3.18.0, 3.26.2 and 3.24.0).
They read DAX as a syntax tree, so they skip DAX that doesn't parse. Their errors (`invalid-culture` for a name
that isn't shaped like one, `ignore-outside-summarizecolumns`) gate a save and a deploy like any other error;
their warnings never do.

`unknown-function` knows DAX's functions from a list built on 2026-11-15 from Microsoft Learn's DAX function
reference (every function in its table of contents) and dax.guide (which adds visual calculation functions,
ones Power BI's own queries use, and deprecated ones such as `KEYWORDMATCH`); the list and its sources are in
`src/Semctl.Core/DaxFunctions.fs`. A function Microsoft adds after that is reported as unknown, and an edit that
introduces a call to one needs `--force` to save, until the list catches up.

There is no rule for a compatibility level too low for the features a model uses, because TOM already refuses
to load a `.bim` file or TMDL folder that uses an object, property or value its compatibility level doesn't have
(a function below 1702, a calendar below 1701, a Direct Lake partition below 1604): such a model never gets as far
as `validate`. (TE3 3.25.1's check is a different one: a compatibility level the connected server doesn't support.)

Options:
- `--rule` picks rules (repeatable or comma-separated).
- `--min-severity error|warning|info` hides less severe issues.
- `--fail-on error|warning|info` sets the exit code: 1 if any issue is this severe or more. The default is
  `error`. Issues hidden by `--min-severity` still count, so hiding warnings can't turn a failing check green.
- `--no-group` lists issues that share a root cause one by one (see below).

`--json` writes the [findings envelope](ci.md#findings-json-and-ci-annotations), with `"valid": false` when there are
errors. `--ci github|azdo` also writes CI annotations to stderr.

Issues with one root cause are grouped. In practice that means a name many expressions use but that nothing
has, such as a column renamed or removed outside sm: every `unresolved-reference` naming it is one group, and
so is every reference to a missing table or its columns. Names match ignoring case. The first issue stands for
the group, with a count and the missing name, and the summary still counts every issue:

```
error  measure:Sales/A  expression:1  'Old'[X] does not exist: there is no table 'Old'  [unresolved-reference]  +2 more for 'Old'
3 errors (2 grouped under their root cause; --no-group lists them)
```

How grouped issues are reported:
- **JSON** keeps every finding. The group's first finding gets `dependents` (the other findings' `objectPath`s,
  each listed once), and the others get `rootCause` (the first finding's `objectPath`). Findings outside a group
  have neither key. `rootCause` says which finding stands for the group: fixing that one object doesn't fix the
  others, but restoring the missing name fixes them all.
- **CI annotations** are written once per group, with the count in the message.
- **`--no-group`** lists every issue on its own, and the JSON has no grouping keys.

Exit codes don't change. Grouping applies after `--rule` and `--min-severity`.

Other rules don't cascade, so they aren't grouped. Every rule checks an object's own definition: a measure
that uses a broken measure isn't reported, and TOM's validation doesn't read DAX. To see what a broken object affects, use
`sm deps <model> <object> --referenced-by --depth 0`. TOM also doesn't say which name it couldn't find, so its
own errors (e.g. a relationship to a missing column) aren't grouped with the DAX that names that column.

## VertiPaq statistics (`vertipaq`)

`sm vertipaq` shows what VertiPaq Analyzer shows: for each table, column, relationship, user hierarchy and
partition, its rows, distinct values (cardinality) and size in VertiPaq's storage, and the rows that break
referential integrity. The statistics come from one of three places:

```sh
sm vertipaq ./Sales.pbip --data ./fixtures                  # offline: model files + fixture data
sm vertipaq "xmla:powerbi://api.powerbi.com/v1.0/myorg/Finance;Sales"   # a model on a server
sm vertipaq --import stats.vpax                             # a VertiPaq Analyzer file (DAX Studio, Bravo, TE3)

sm vertipaq ./Sales.pbip --data ./fixtures --columns --top 10     # the 10 largest columns
sm vertipaq ./Sales.pbip --data ./fixtures Sales Customer --all   # every view, for two tables
sm vertipaq "xmla:...;Sales" --export stats.vpax                  # write a .vpax for DAX Studio
sm vertipaq --import stats.vpax --export shared.vpax --obfuscate  # obfuscate a file before sharing it
sm vertipaq ./Sales.pbip --import stats.vpax --annotate --save    # stats as annotations, for BPA rules
sm bpa ./Sales.pbip --vpax stats.vpax                             # ... or for one BPA run, in memory
```

**Views.** The default is the table view; `--columns`, `--relationships`, `--partitions` and `--hierarchies`
choose others (several at once is fine), `--all` shows them all. Each view lists the largest objects first
(unknown sizes last) and `--top N` keeps the first N. Table names after the model show only those tables (a
relationship stays if either end is one of them), but sizes, totals and shares are still the whole model's; a name
that isn't a table is an error with suggestions. `te
vertipaq` shows columns by default; sm follows VertiPaq Analyzer, which opens on tables. `te`'s `--detail`,
`--fields` and `--stats` aren't there: `--json` has every field.

**Unknown is never 0.** A statistic sm couldn't read is `?` in text and left out of JSON, and so is every total
that needs it (a table's size, then the model's). stderr says why, once per cause.

### From fixture data (`--data`)

The rows, cardinalities, missing keys and invalid rows are **exact**: counted in the fixture's rows (the same
fixture `query --data` uses, see [Fixture data](fixture-data.md#fixture-data-data)). A fixture with errors is refused (exit 3), as
for `preview`: its invalid values would be counted as BLANK. Data columns are counted in the fixture itself;
calculated columns, calculated tables and calculation groups are computed by the [local
engine](dax-engine.md#the-local-dax-engine-query---data), with VertiPaq Analyzer's own DAX (`DISTINCTCOUNT`, and `COUNTROWS (
ALLNOBLANKROW ( ... ) )` for a field parameter's column). Where the engine refuses, that column's statistics are
unknown, with a warning. Nothing leaves the machine.

- **Cardinality:** distinct values, BLANK counting as one, text compared as the model's collation compares it.
- **Missing keys / invalid rows:** the many side's distinct keys, and its rows, that have no match on the one
  side: the rows DAX puts on the one side's blank row. A BLANK key is a violation unless the one side has a BLANK
  key too. Inactive relationships are checked like active ones.
- **Partitions:** a table has one fixture file, so a table with one partition gives it the table's rows and data;
  with several, the partitions are listed without statistics (a warning names those tables). Segments are
  unknown: how many there are depends on the server.

**Sizes are estimates** (`"estimated": true` in JSON, `(sizes estimated)` in text). The real sizes depend on how
VertiPaq sorts, encodes and compresses each segment, which only a refresh decides, so sm models the
uncompressed layout, assuming hash encoding everywhere:

| Structure | Estimate |
|---|---|
| column data | ceil(rows x b / 8) bytes, b = ceil(log2(cardinality)), at least 1 bit |
| dictionary | 16 bytes per distinct value, plus 2 bytes per character of the distinct text values |
| attribute hierarchy | 8 bytes per distinct value; 0 for a column with `IsAvailableInMDX` false |
| user hierarchy | 8 bytes per distinct combination of each level with the levels above it |
| relationship | ceil(From cardinality x ceil(log2(To cardinality)) / 8) bytes, charged to the From table |

Use them to compare a model's tables and columns with each other (a long text key, a high-cardinality date-time),
not with a server's numbers. The encoding VertiPaq chose (HASH or VALUE) is only known from a server; offline the
column view shows the model's `EncodingHint` when it isn't `Default`.

### From a server

For an `xmla:` model, sm reads the model, then runs the read-only storage DMVs and DAX queries VertiPaq Analyzer
runs (SQLBI's Dax.Model.Extractor), over the same connection, against that database only, and closes it.
(`--annotate` connects twice: once to read the model it edits, once for the statistics.) These are
all the queries it sends (the DAX ones in batches of 50 tables or columns, and 10 relationships):

| Query | For |
|---|---|
| `SELECT DIMENSION_NAME, TABLE_ID, ROWS_COUNT FROM $SYSTEM.DISCOVER_STORAGE_TABLES WHERE RIGHT ( LEFT ( TABLE_ID, 2 ), 1 ) <> '$'` | rows, and the tables' storage ids |
| `SELECT DIMENSION_NAME, COLUMN_ID, ATTRIBUTE_NAME, DICTIONARY_SIZE, COLUMN_ENCODING FROM $SYSTEM.DISCOVER_STORAGE_TABLE_COLUMNS WHERE COLUMN_TYPE = 'BASIC_DATA'` | dictionaries, encodings, column ids |
| `SELECT DIMENSION_NAME, TABLE_ID, ROWS_COUNT - 3 FROM $SYSTEM.DISCOVER_STORAGE_TABLES WHERE LEFT ( TABLE_ID, 2 ) = 'H$'` | cardinality, where DAX doesn't give it |
| `SELECT DIMENSION_NAME, PARTITION_NAME, COLUMN_ID, SEGMENT_NUMBER, RECORDS_COUNT, USED_SIZE FROM $SYSTEM.DISCOVER_STORAGE_TABLE_COLUMN_SEGMENTS WHERE RIGHT ( LEFT ( TABLE_ID, 2 ), 1 ) <> '$'` | column data, by partition and segment |
| `... DISCOVER_STORAGE_TABLE_COLUMN_SEGMENTS WHERE LEFT ( TABLE_ID, 2 ) = 'H$' AND SEGMENT_NUMBER = 0`, and `= 'U$'`, `= 'R$'` | attribute hierarchies, user hierarchies, relationships |
| `SELECT [ID], [Name] FROM $SYSTEM.TMSCHEMA_HIERARCHIES` and `SELECT [ID], [FromTableID], [FromColumnID], [ToTableID], [ToColumnID] FROM $SYSTEM.TMSCHEMA_RELATIONSHIPS` | matching storage ids to names |
| `EVALUATE UNION ( ROW ( "Table", "0000Sales", "Cardinality", COUNTROWS ( 'Sales' ) ), ... )` | exact rows |
| `EVALUATE UNION ( ROW ( "Table", ..., "Column", ..., "Cardinality", DISTINCTCOUNT ( 'Sales'[Key] ) ), ... )` | exact cardinality |
| `CALCULATETABLE ( ROW ( "RelationshipId", 1, "MissingKeys", DISTINCTCOUNT ( 'Sales'[Key] ), "InvalidRows", COUNTROWS ( 'Sales' ) ), ISBLANK ( 'Product'[Key] ), USERELATIONSHIP ( 'Sales'[Key], 'Product'[Key] ) )` | missing keys and invalid rows |

The full texts are in `src/Semctl.Xmla/VertipaqServer.fs`. Tables in DirectQuery, Dual or Direct Lake mode get
no DAX queries (they would query the source, or page columns into memory): their statistics come from the DMVs
only, and a warning names them. A query that fails (a DMV the server doesn't have, a user without the permission
to run it) leaves its statistics unknown, with a warning, and the rest is read: Tabular Editor 3's "fall back to
metadata-only statistics". Nothing else is sent, and nothing is written to the server. **Like the rest of
connected mode, this has been tested against a fake server only.**

### `.vpax` files (`--import`, `--export`, `--obfuscate`)

A `.vpax` is a zip of `DaxModel.json` (the statistics and the model's metadata), `DaxVpaView.json`, `Model.bim`
and `CompatabilityMode.txt` (sic). sm reads and writes them with SQLBI's own library (Dax.Vpax, MIT), the one
VertiPaq Analyzer's tools use, so a file sm writes is in their format. The tests read a file Bravo made, and
open every file sm writes with Dax.ViewModel, the view model DAX Studio's VertiPaq Analyzer pane is built on,
reading every property it shows. **DAX Studio, Bravo and Tabular Editor 3 themselves have not been run on a file
sm wrote.**

- `--import <file>` reads one. It needs no model: `sm vertipaq --import stats.vpax`. With a model first
  (`sm vertipaq ./Sales.pbip --import stats.vpax`), the model is what `--annotate` writes to. A file made
  without reading statistics from data says so, and its missing keys and invalid rows are unknown.
- `--export <file>` writes one, from any source; `--import a.vpax --export b.vpax` copies the file's own
  statistics unchanged. An existing file is replaced only with `--force` (exit 4). The file says which tool made
  it; a file sm made from fixture data is marked as an estimate, and reads back as one. The format has no
  "unknown": sm writes 0 (what VertiPaq Analyzer writes for a statistic it didn't read) and says which ones on
  stderr. Each column gets one segment per partition, holding that partition's whole share of it, so a file
  sm writes shows one segment per partition however many the server had. A column's data with no known
  partition (or in a partition the model doesn't have, which stderr names) goes in the table's first partition.
  A server's `RowNumber-<guid>` columns are included, as VertiPaq Analyzer includes them in table sizes.
- `--obfuscate` (with `--export`) obfuscates the names and expressions with SQLBI's Dax.Vpax.Obfuscator (their
  library for obfuscating VertiPaq Analyzer files), and writes only `DaxModel.json`, as that library does (no
  `Model.bim`, which would carry the real names). SQLBI's tools name such a file `.ovpax`; sm uses the name you
  give. Names that are
  reserved in DAX (a table called `Date` or `Currency`) can't be changed and stay readable; stderr lists them. The
  dictionary that maps the obfuscated names back is written next to the file as `<name>.dict` (replaced only with
  `--force`): it holds the real names, so keep it, and don't send it with the file. `--dictionary <old.dict>`
  extends an earlier dictionary, so the same names obfuscate the same way in a new file.

### Annotations (`--annotate`)

`--annotate` writes the statistics into the model as annotations, through the same dry run / `--save` /
`--save-to` as every [editing command](editing.md#editing-commands), so Best Practice rules that read them work offline. The
names are those of Michael Kovalsky's VertiPaq Analyzer scripts for Tabular Editor, which the rules in
[microsoft/Analysis-Services](https://github.com/microsoft/Analysis-Services/tree/master/BestPracticeRules) read
(`Convert.ToInt64(GetAnnotation("Vertipaq_Cardinality")) > 100000`):

| On | Annotations |
|---|---|
| the model | `Vertipaq_ModelSize`; sm's own `Semctl_Vertipaq_Estimated` = `true` when the sizes are estimates |
| a table | `Vertipaq_RowCount`, `Vertipaq_TableSize`, `Vertipaq_TableSizePctOfModel` |
| a column | `Vertipaq_Cardinality`, `Vertipaq_ColumnSize`, `Vertipaq_DataSize`, `Vertipaq_DictionarySize`, `Vertipaq_ColumnHierarchySize`, `Vertipaq_ColumnSizePctOfTable`, `Vertipaq_ColumnSizePctOfModel` |
| a relationship | `Vertipaq_MaxFromCardinality`, `Vertipaq_MaxToCardinality`, `Vertipaq_RelationshipSize`, `Vertipaq_RIViolationInvalidRows` |
| a user hierarchy | `Vertipaq_UserHierarchySize` |
| a partition | `Vertipaq_RecordCount`, `Vertipaq_SegmentCount`, `Vertipaq_RecordsPerSegment` |

Values are integers, and fractions rounded to 3 decimals (`0.125`), in the invariant culture. Only known values
are written; an annotation of the family whose value is now unknown is removed, so the model never mixes two
runs. Running it again with the same statistics changes nothing. Relationships are matched by their columns, not
their names (a model's files and its server copy name them differently). With table names, only those tables'
objects (and their relationships) are annotated, and the model's own annotations are left alone. `te`'s `--push`
(annotating the model on the server) isn't there: `--save` is refused for a server model, as for every edit, so
`--save-to` files and `sm deploy` them.

`sm bpa <model> --vpax <file>` puts a `.vpax`'s statistics on the model the same way, in memory, for that run
only: the model's files aren't changed. It can't be combined with `--fix`, which would save them.

## Checking columns against their source (`schemacheck`)

`schemacheck` compares each table's data columns with the columns its source returns, as Tabular Editor 2's `-SC`
and the Tabular Editor CLI's `schemacheck` do. It never changes the model unless you add `--update`.

```sh
sm schemacheck ./Sales.pbip --source ./schema.json             # a schema file (offline)
sm schemacheck ./Sales.pbip --source ./fixtures                # a fixture directory (names only, offline)
sm schemacheck ./Sales.pbip Sales Product --source "sql:myserver.database.windows.net;SalesDb"
sm schemacheck ./Sales.pbip --source ./schema.json --update --save   # add new columns, update types
sm set ./Sales.pbip Sales --update-schema --source ./schema.json --save   # the same, for one table
```

| Code | Severity | Reports |
|---|---|---|
| `SCHEMA_SOURCE_COLUMN_ADDED` | warning | a source column no data column maps to |
| `SCHEMA_DATA_TYPE_CHANGED` | warning | a data column whose type isn't what its source column's type maps to |
| `SCHEMA_MISSING_SOURCE_COLUMN` | error | a data column whose source column the source doesn't return |
| `SCHEMA_SOURCE_QUERY_ERROR` | error | the source couldn't describe the table (the server rejected the query, a fixture file can't be read) |
| `SCHEMA_DUPLICATE_SOURCE_COLUMN` | info | a source column name returned more than once (the first is used) |
| `SCHEMA_TABLE_SKIPPED` | info | a table that wasn't checked, and why |

- **Which tables:** the ones you name, or every table with data columns. Calculated tables and calculation groups
  aren't checked (and aren't reported unless you name one). A table name that isn't in the model is an error (exit 3).
- **Matching:** a data column maps to the source column its `sourceColumn` names, ignoring case, with or without
  `[brackets]`. Calculated columns are left out. A data column whose type is unset (`automatic`, `unknown`) isn't
  type-checked.
- **Exit code:** 1 if a finding is at or above `--fail-on` (default `error`), so missing columns and query errors
  fail the check and new or retyped columns don't. `--ci`, `--sarif` and `--json` work as for `validate`.
  `--trx` writes one result per finding (errors `Failed`, warnings `Warning`, skipped tables `NotExecuted`) and a
  passed result for each table that matches; the run is `Failed`, `Warning` or `Completed`.
- **JSON:** the [findings envelope](ci.md#findings-json-and-ci-annotations) with `"command": "schemacheck"` and
  `"source": "schemaCheck"` on each finding, plus `source`, `tablesChecked`, `tablesSkipped`, `tablesWithDrift`
  and `tables: [{table, ref, status (checked|skipped|error), reason?, origin?, typesChecked?, drift?}]`.

Tabular Editor 2's annotations are honoured, with TE2's names. Any value except `0`, `false` or `no` turns one on.

| Annotation | On | Effect |
|---|---|---|
| `TabularEditor_SkipSchemaCheck` (also TE2's old spelling `TabularEditor_SkipSkemaCheck`) | table | not checked (`SCHEMA_TABLE_SKIPPED`) |
| `TabularEditor_IgnoreSourceColumnAdded` | table | no `SCHEMA_SOURCE_COLUMN_ADDED` |
| `TabularEditor_IgnoreDataTypeChange` | table or column | no `SCHEMA_DATA_TYPE_CHANGED` |
| `TabularEditor_IgnoreMissingSourceColumn` | table or column | no `SCHEMA_MISSING_SOURCE_COLUMN` |
| `TabularEditor_SchemaCheckNoWarnings` | model | every warning becomes an error (as in TE2) |

One difference from TE2: with `IgnoreDataTypeChange` on a table, TE2 also reports every column of it as missing
(it only marks a column as matched while it compares types). sm doesn't.

### Sources

**A schema file** (`--source <file>.json`): what each table's source returns, by model table name (ignoring case).
Comments and trailing commas are allowed.

```json
{ "tables": {
    "Sales":   [ { "name": "OrderDate", "type": "dateTime" }, { "name": "Amount", "type": "decimal", "sourceType": "decimal(18,2)" } ],
    "Product": [ "ProductKey", "Name" ] } }
```

A column is its source name, or an object with `name`, an optional `type` (`string`, `int64`, `double`,
`decimal`, `dateTime`, `boolean`, `binary`: the model's data types) and an optional `sourceType` (shown in
messages). A column without `type` isn't type-checked; a table none of whose columns has one counts as names only.
A table the file doesn't list is skipped.

**A fixture directory** (`--source <dir>`, the [`sm data`](fixture-data.md#fixture-data-data) layout): each table's file names its
columns, a CSV by its header and a JSON file by its rows' properties. Fixture files name columns by their *model*
names, so columns are matched by name rather than by `sourceColumn`, and they carry no types, so only names are
compared (the summary says so). Calculated columns in a file are ignored. A table with no file is skipped.

**A SQL endpoint** (`--source "sql:<server>;<database>"`): SQL Server, Azure SQL, or a Fabric warehouse / lakehouse
SQL endpoint. sm asks the server to describe each table's query with `sp_describe_first_result_set`: nothing is
executed and no rows are read. (Tabular Editor 2 runs the query with `CommandBehavior.SchemaOnly` instead.) It reads a
table's first partition, as TE2 does, and only these, exactly:

- a **query partition** on a SQL Server provider data source (`SQLNCLI*`, `MSOLEDBSQL*`, `SQLOLEDB`, or SqlClient):
  its query as it is;
- an **M partition** that is exactly `Sql.Database(server, database)` and one navigation step
  `Source{[Schema = "dbo", Item = "Sales"]}[Data]` (described as `SELECT * FROM [dbo].[Sales]`), or exactly
  `Sql.Database(server, database, [Query = "..."])`. The server and database may be text or parameters (named
  expressions that are text, with or without `meta [...]`).

Anything else (another step such as `Table.SelectColumns`, other options, other connectors, Direct Lake) is
`SCHEMA_TABLE_SKIPPED`: its columns can't be known without running Power Query, and sm doesn't guess. So is a
table whose partition names a different database from the one in `--source` (a different server is fine: point
`--source` at a dev copy). A query the server rejects is `SCHEMA_SOURCE_QUERY_ERROR`, with the server's message.

Data types: for a **query partition**, SQL types map as TE2 maps them (`int`, `bigint`, `smallint`, `tinyint` to
`int64`; `float`, `real` to `double`; `decimal`, `numeric`, `money`, `smallmoney` to `decimal`; the date and time
types to `dateTime`; `bit` to `boolean`; character types to `string`; `binary`, `varbinary` to `binary`), without
TE2's guesses for other names. For an **M partition**, sm found no Microsoft documentation of Power Query's mapping, so only
`int`/`bigint` (`int64`), `float`/`real` (`double`), `bit`, character types, and `date`/`datetime`/`datetime2`/
`smalldatetime` are compared. Other types (`decimal`, `numeric`, `money`, `smallint`, `tinyint`, `time`,
`datetimeoffset`, `uniqueidentifier`, ...) are not compared, and `--update` won't add a column of such a type.

Signing in, from environment variables only (never the command line):

| To sign in with | Set |
|---|---|
| an Entra access token for `https://database.windows.net/` | `SM_SQL_TOKEN`, or `SM_SQL_TOKEN_FILE=<path>` (`-` for standard input) |
| a service principal | the same variables as [connected mode](xmla.md#signing-in): `SM_XMLA_CLIENT_ID`, `SM_XMLA_TENANT_ID`, `SM_XMLA_CLIENT_SECRET` (or `_FILE`), else `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_CLIENT_SECRET` |
| a SQL login | `SM_SQL_USER`, and `SM_SQL_PASSWORD` or `SM_SQL_PASSWORD_FILE` |
| the current Windows / Kerberos account | nothing |

`SM_SQL_AUTH` picks one: `auto` (the default), `token`, `spn`, `sql` or `integrated`. With `auto`, a Microsoft
cloud host (`*.database.windows.net` and the national clouds' equivalents, `*.sql.azuresynapse.net`,
`*.datawarehouse.fabric.microsoft.com`, `*.database.fabric.microsoft.com`) uses the token, then the service
principal, then a SQL login; any other server uses a SQL login if one is set, else integrated security. **A token or
service principal is never sent to a server that isn't one of those hosts.** Nothing interactive is ever started.

The connection is always encrypted (`Encrypt=Mandatory`) and the server's certificate must be trusted. For a local
test server with a self-signed certificate, `SM_SQL_TRUST_SERVER_CERTIFICATE=1` accepts it (still encrypted); it
is off unless you set it. sm uses SqlClient's managed network layer on every OS (it ships without SqlClient's
native Windows library). On Windows, Microsoft documents that layer as meant for testing and debugging, and it
doesn't do non-domain Windows authentication. A server that can't be reached, or refuses the sign-in, fails the whole command (exit 5).

**What is sent, and to whom:** only to the server in `--source`, and only once a table needs describing (a model
with no SQL-readable table connects to nothing): the sign-in, then for each table its query, as the text of a call to
`sp_describe_first_result_set`. With a service principal, MSAL also asks Microsoft's sign-in service
(`login.microsoftonline.com`, or the national cloud's) for a token, sending the client ID and secret. Nothing else:
no model metadata, no data, no telemetry. A schema file or fixture directory opens only local files.

### Updating the model (`--update`, `set --update-schema`)

`--update` turns the check into an edit, with the [editing commands'](editing.md#editing-commands) conventions: a dry run that
shows the changes unless `--save` or `--save-to`, `--stat`, `--name-only`, `--force`, and `--json` for the change
set. The findings go to stderr.

- A **new source column** becomes a data column named and sourced as the source names it, with its mapped type
  (summarized by sum if numeric; in a table with a lineage tag, also a lineage tag and `SummarizationSetBy`,
  as `sm add` makes a column). A column with no exactly mapped type, or
  whose name the table already uses, isn't added; a warning says why.
- A **changed type** is changed on the column itself; everything else about it (name, format string, description,
  folders, relationships, translations) stays.
- A **removed source column** is only reported, unless `--drop-removed-columns`. Then it is removed as `sm remove`
  would; a column other DAX still uses, or a PBIR report next to the model uses, is kept, with a warning naming
  what uses it.
- Whatever an annotation makes the check ignore is left alone; skipped tables and query errors change nothing.

`--update` doesn't take `--fail-on`, `--ci`, `--trx` or `--sarif`. `sm set <model> <table> --update-schema --source
<src>` does the same for one table (or for every table, with `model` as the object), with `set`'s switches.
