# The local DAX engine (`query --data`)

`tess query` on model files evaluates DAX on your machine, over [fixture data](fixture-data.md#fixture-data-data): no server, no
refresh, no network.

```sh
tess query ./Sales.pbip --data ./fixtures "EVALUATE SUMMARIZECOLUMNS ( 'Date'[Year], \"Sales\", [Total] )"
tess query ./Sales.pbip --data ./fixtures -f totals.dax --json      # or - for standard input; --csv; --max-rows
tess query ./Sales.pbip --coverage                                  # what the engine can evaluate of this model
tess query ./Sales.pbip --data ./fixtures --now 2024-03-05T14:30:00 "EVALUATE ROW ( \"d\", TODAY () )"   # a fixed clock
```

The options, the output and the JSON are those of [`query` on a server](xmla.md#query): the same `QueryResult`, with
`"source"` the model's path. The fixture is checked first (as `tess data check` does) and a fixture with errors is
refused (exit 3, `fixture-has-errors`).

**What it is.** An independent reimplementation of DAX's semantics, written against the public documentation:
Microsoft Learn's DAX reference, [dax.guide](https://dax.guide) and SQLBI's articles, cited in the code next to each
rule. **It has not been checked against a real Analysis Services engine yet.** Its tests are hand-worked expected
values with their sources, identities DAX guarantees, and a check of filter propagation against its definition on
random models; a conformance corpus against published results is the next step.

**The one rule: never a wrong answer.** A function, an argument form, a data-type combination or a model feature the
engine doesn't implement exactly is an error, `not-supported` (exit 3), naming the thing and where it is:

```
tess: Not supported by the local DAX engine: the function PATHITEM (measure 'Sales'[Label] 2:5)
```

That includes the places where the sources don't settle what the real engine does. A subset that is right is the
point; so expect refusals, and read them as "not yet" rather than as a problem with your DAX. `IFERROR` never hides a
refusal. The other errors are `dax-syntax` (with line and column), `dax-invalid` (an unknown name, the wrong
arguments, a circular dependency) and `dax-error` (an error DAX itself raises: `ERROR ( )`, a failed conversion).

## What it supports

- **Values:** whole, decimal and fixed decimal numbers, text, true/false, dates and BLANK, with DAX's rules for
  each operator (`BLANK + 5` is 5, `BLANK * 5` is BLANK, `BLANK = 0` but not `BLANK == 0`, `5 / 0` is infinity, text
  compares without case). `&&`, `||`, `IF`, `SWITCH` and `COALESCE` evaluate only what they need; `IF.EAGER`
  evaluates both branches. Whole-number `+`, `-` and `*` wrap around past 64 bits as the engine's do (SQLBI,
  "Rounding errors with different data types"); `ISERROR` and `IFERROR` treat infinity and NaN as errors; a BLANK
  written as a branch of an `IF` whose other branch is TRUE/FALSE is FALSE.
- **Tables as values, with data lineage:** a column of a table expression remembers the model column it came from,
  which is what makes `VALUES ( T[c] )` usable as a filter, what `TREATAS` assigns, and what an expression loses.
  Result columns are named as the engine names them (`Sales[Amount]`, `[Total]`, `[Value]`) and typed statically
  where DAX types them so (a column that is BLANK in every row still has its type; an `IF` with a whole and a decimal
  branch is a decimal; one with a text and a number is a variant).
- **Evaluation contexts:** the filter context; nested row contexts with `EARLIER`; context transition in
  `CALCULATE` and in every measure reference, covering the expanded table and keeping duplicate rows together;
  variables evaluated where they are defined, on first use, once.
- **Relationships:** many-to-one, single column. Filters travel from the one side; both ways where the model says
  so or under `CROSSFILTER`; inactive relationships under `USERELATIONSHIP`; expanded tables (a table used as a
  filter filters the tables it relates to, and `ALL ( Sales )` removes filters from them); the blank row for keys
  with no match (`VALUES` and `ALL` have it, `DISTINCT`, `ALLNOBLANKROW` and a plain table reference don't), on an
  inactive relationship too ("Inactive relationships are also expanded": Learn), though filters and `RELATED` go
  through the active one.
- **`CALCULATE` / `CALCULATETABLE`:** the documented order (filter arguments in the original context, context
  transition, modifiers, then the filters); True/False filters over one or several columns of a table; table
  filters; `KEEPFILTERS`; `ALL`, `ALLEXCEPT`, `ALLNOBLANKROW`, `REMOVEFILTERS`, `ALLCROSSFILTERED`,
  `USERELATIONSHIP`, `CROSSFILTER` (NONE, ONEWAY, BOTH); arbitrarily shaped filters, which lose their pairing when
  one of their columns is replaced.
- **Queries:** `DEFINE MEASURE` (overriding a model measure for the query's own code only), `DEFINE VAR`,
  `DEFINE FUNCTION`, `DEFINE COLUMN` (a calculated column on a model table, for the query) and `DEFINE TABLE` (a
  calculated table, for the query), several `EVALUATE` statements, `ORDER BY` by result columns or expressions,
  `START AT` (the rows before the values, in the order of the keys and their directions, are skipped).
- **User-defined functions,** the model's and the query's, with `VAL` parameters (evaluated once, in the caller's
  context, and converted to the parameter's type) and `EXPR` parameters (evaluated where the body uses them).
- **Calculated columns and calculated tables,** computed in dependency order when the model is loaded. A
  calculated table may read the tables related to it, as a date table does
  (`CALENDAR ( MIN ( Sales[Date] ), MAX ( Sales[Date] ) )`). A circular
  dependency is an error, including the classic one: two calculated columns that each use `CALCULATE` in a table
  with no key. The key is what the engine knows to be unique: a column on the one side of a relationship (either
  side of a one-to-one), not one whose values happen to be unique ("The presence of a relationship forces the DAX
  engine to verify that ProductKey is a column with unique values": SQLBI, "Understanding circular
  dependencies"). With a key, a context transition depends on it alone, so several such columns are fine.
  `ALLEXCEPT`, `ALL` or `REMOVEFILTERS` in the same `CALCULATE` take the transition down to the columns they keep,
  key or not ("The technique using ALLEXCEPT relies on the semantics of the DAX language. As such, it works in any
  scenario": SQLBI), so `CALCULATE ( ..., ALLEXCEPT ( T, T[Key] ) )` in two columns of a table with no relationship
  is no cycle. Without a key, a cycle that another narrowing (a table filter argument, a modifier in a measure)
  could take away, or that a column marked as the key or `USERELATIONSHIP` (which may turn the optimization off)
  might change, is refused rather than called circular. A calculated column or table that uses something unsupported doesn't stop the rest: only the queries
  that read it fail, with its error.
- **Relationships:** many-to-one either way round, single or both directions, active or not; **one-to-one**
  (filters both ways, and each table's expanded table holds the other: "you can consider the two expanded tables as
  being the same", SQLBI, so `RELATED` works from either side and `ALL ( either )` removes the other's filters; the
  table without a match gets the blank row); **many-to-many** cardinality, which is a limited relationship: no
  table expansion and no blank row, a filter crosses it as the values of the column, in its direction or both, and
  nothing is filtered when nothing filters the other side, so a total includes the rows that match nothing.
  `RELATED` across it is an error.
- **Functions** (anything else is `not-supported`):

| Family | Functions |
|---|---|
| Aggregation | `AVERAGE` `AVERAGEA` `AVERAGEX` `CONCATENATEX` `COUNT` `COUNTA` `COUNTAX` `COUNTBLANK` `COUNTROWS` `COUNTX` `DISTINCTCOUNT` `DISTINCTCOUNTNOBLANK` `MAX` `MAXA` `MAXX` `MIN` `MINA` `MINX` `PRODUCTX` `RANKX` `SUM` `SUMX` |
| Date and time | `CALENDAR` `CALENDARAUTO` `DATE` `DATEDIFF` `DATEVALUE` `DAY` `EDATE` `EOMONTH` `HOUR` `MINUTE` `MONTH` `NETWORKDAYS` `NOW` `QUARTER` `SECOND` `TIME` `TIMEVALUE` `TODAY` `UTCNOW` `UTCTODAY` `WEEKDAY` `WEEKNUM` `YEAR` `YEARFRAC` |
| Filter | `ALL` `ALLCROSSFILTERED` `ALLEXCEPT` (also as a table) `ALLNOBLANKROW` `CALCULATE` `CALCULATETABLE` `CROSSFILTER` `DISTINCT` `EARLIER` `EARLIEST` `FILTER` `HASONEFILTER` `HASONEVALUE` `ISCROSSFILTERED` `ISFILTERED` `KEEPFILTERS` `REMOVEFILTERS` `SELECTEDVALUE` `TREATAS` `USERELATIONSHIP` `VALUES` |
| Information | `CONTAINS` `CONTAINSROW` `CUSTOMDATA` `ISBLANK` `ISEMPTY` `ISERROR` `ISEVEN` `ISINSCOPE` `ISLOGICAL` `ISNONTEXT` `ISNUMBER` `ISODD` `ISTEXT` `NAMEOF` (not calendars) `USERCULTURE` `USERNAME` `USEROBJECTID` `USERPRINCIPALNAME` |
| Logical | `AND` `BLANK` `COALESCE` `ERROR` `FALSE` `IF` `IF.EAGER` `IFERROR` `NOT` `OR` `SWITCH` `TRUE` |
| Math | `ABS` `ACOS` `ACOSH` `ACOT` `ACOTH` `ASIN` `ASINH` `ATAN` `ATANH` `CEILING` `CONVERT` `COS` `COSH` `COT` `COTH` `CURRENCY` `DEGREES` `DIVIDE` `EVEN` `EXP` `FACT` `FLOOR` `GCD` `INT` `ISO.CEILING` `LCM` `LN` `LOG` `LOG10` `MOD` `MROUND` `ODD` `PI` `POWER` `QUOTIENT` `RADIANS` `ROUND` `ROUNDDOWN` `ROUNDUP` `SIGN` `SIN` `SINH` `SQRT` `SQRTPI` `TAN` `TANH` `TRUNC` |
| Relationship | `LOOKUPVALUE` `RELATED` `RELATEDTABLE` |
| Statistical | `GEOMEAN` `GEOMEANX` `MEDIAN` `MEDIANX` `PERCENTILE.EXC` `PERCENTILE.INC` `PERCENTILEX.EXC` `PERCENTILEX.INC` `RANK.EQ` `STDEV.P` `STDEV.S` `STDEVX.P` `STDEVX.S` `VAR.P` `VAR.S` `VARX.P` `VARX.S` |
| Table manipulation | `ADDCOLUMNS` `CROSSJOIN` `CURRENTGROUP` `DATATABLE` `EXCEPT` `GENERATE` `GENERATEALL` `GROUPBY` `IGNORE` `INTERSECT` `NATURALINNERJOIN` `NATURALLEFTOUTERJOIN` `NONVISUAL` `ROLLUPADDISSUBTOTAL` `ROLLUPGROUP` `ROW` `SELECTCOLUMNS` `SUMMARIZE` `SUMMARIZECOLUMNS` `TOPN` `UNION` |
| Calculation groups | `ISSELECTEDMEASURE` `SELECTEDMEASURE` `SELECTEDMEASUREFORMATSTRING` `SELECTEDMEASURENAME` |
| Text | `COMBINEVALUES` `CONCATENATE` `CONTAINSSTRING` `CONTAINSSTRINGEXACT` `EXACT` `FIND` `FIXED` `FORMAT` `LEFT` `LEN` `LOWER` `MID` `REPLACE` `REPT` `RIGHT` `SEARCH` `SUBSTITUTE` `TOCSV` `TOJSON` `TRIM` `UNICHAR` `UNICODE` `UPPER` `VALUE` |
| Time intelligence | `CLOSINGBALANCEMONTH` `CLOSINGBALANCEQUARTER` `CLOSINGBALANCEWEEK` `CLOSINGBALANCEYEAR` `DATEADD` `DATESBETWEEN` `DATESINPERIOD` `DATESMTD` `DATESQTD` `DATESWTD` `DATESYTD` `ENDOFMONTH` `ENDOFQUARTER` `ENDOFWEEK` `ENDOFYEAR` `FIRSTDATE` `FIRSTNONBLANK` `FIRSTNONBLANKVALUE` `LASTDATE` `LASTNONBLANK` `LASTNONBLANKVALUE` `NEXTDAY` `NEXTMONTH` `NEXTQUARTER` `NEXTWEEK` `NEXTYEAR` `OPENINGBALANCEMONTH` `OPENINGBALANCEQUARTER` `OPENINGBALANCEWEEK` `OPENINGBALANCEYEAR` `PARALLELPERIOD` `PREVIOUSDAY` `PREVIOUSMONTH` `PREVIOUSQUARTER` `PREVIOUSWEEK` `PREVIOUSYEAR` `SAMEPERIODLASTYEAR` `STARTOFMONTH` `STARTOFQUARTER` `STARTOFWEEK` `STARTOFYEAR` `TOTALMTD` `TOTALQTD` `TOTALWTD` `TOTALYTD` |

  `SUMMARIZE` takes a table, group-by columns (its own, or of tables it has a many-to-one relationship to) and
  name/expression pairs; not `ROLLUP`. `SUMMARIZECOLUMNS` takes group-by columns, filter tables (with
  `KEEPFILTERS`) and name/expression pairs, anywhere outside a row context: in a query, in `CALCULATETABLE`, in a
  measure. Existing combinations for columns of one table; group-by tables that a filter table spans together are
  grouped by the combinations it has, the others cross joined; the blank row is a group; rows whose expressions are
  all BLANK (except `IGNORE`d ones) are dropped. `ROLLUPADDISSUBTOTAL` (with `ROLLUPGROUP` and its flag columns) adds
  the subtotal rows, and `ISINSCOPE` tells them apart. `NONVISUAL ( filter )` on group-by columns of the same call:
  it picks the groups, and `ALLSELECTED` in a row doesn't restore it ("NONVISUAL marks a SUMMARIZECOLUMNS filter so
  that it does not affect measures using ALLSELECTED": dax.guide). Not `ROLLUP`, `ROLLUPISSUBTOTAL`, nor a rollup
  with a group-level filter; nor `NONVISUAL` on other columns (whether it then changes the measures' values isn't
  documented), with `ROLLUPADDISSUBTOTAL` or `KEEPFILTERS`, or on a column the outer context already filters. `NATURALINNERJOIN` and `NATURALLEFTOUTERJOIN` join tables with no model columns
  (`DATATABLE`, table constructors, computed columns) on their columns of the same name. `FIRSTNONBLANK` and its
  relatives go through the column's values in ascending order.

## Calculation groups

A calculation group is a table whose rows are its items (its `Name` column holds their names, its `Ordinal`
column their ordinals), so it is filtered, grouped by and iterated like any table. A measure reference evaluated
where a group's filter leaves exactly one item visible becomes that item's expression, with `SELECTEDMEASURE ()`
the measure (after the reference's context transition); nothing else is rewritten, so `CALCULATE ( SUM ( ... ),
<item> )` is unchanged and every measure reference inside a `CALCULATE` gets the item (SQLBI, "Understanding the
application of calculation items"). The rules, each cited in `Engine/Functions/CalculationGroups.fs`:

- **Selection.** A group that isn't filtered applies nothing, even with one item. Several items, or none (a filter
  on a name no item has), leave the measure unchanged; under `selectionExpressionBehavior` Visual several items give
  BLANK (Learn, "Calculation groups").
- **Precedence.** The highest group is applied first; the others are applied where its `SELECTEDMEASURE ()` and its
  other measure references are evaluated, in the filter context there: `( ( 10 ) + 2 ) * 2 = 24`.
- **Once.** An item in force (the group's filters are the ones it was applied under) is not applied again, also not
  to the measures the measure refers to, while an item selected later, in a measure or in another item, is: SQLBI's
  `Just2Times10` gives 25.
- **Sideways recursion.** A `CALCULATE` in an item that selects another item of its group applies that one (Learn's
  `YOY%`, `PY YTD`).
- **The functions** `SELECTEDMEASURE`, `SELECTEDMEASURENAME`, `ISSELECTEDMEASURE` and `SELECTEDMEASUREFORMATSTRING`
  (the measure's format string: see [format strings](#format-strings)) work in an item's expression. A measure's
  result type is its values'.

Refused, as the sources leave them open: an item selected again by a filter while it is being applied (itself, a
cycle, `KEEPFILTERS` over its own filter: "Other types of recursion aren't supported"); `multipleOrEmptySelectionExpression`
and `noSelectionExpression`; several or no items under `selectionExpressionBehavior` Automatic above compatibility level
1702 (Learn says Automatic becomes Visual "above a future compatibility level") or no items under Visual; two groups
with the same precedence; a group with an Ordinal column and items left at ordinal -1; `ROLLUPADDISSUBTOTAL` over a group's column (its subtotals depend on the selection
behaviour); an item applied in a calculated column or table; these functions outside an item's expression or in a
function it calls; what [format strings](#format-strings) refuses. `--coverage` lists calculation items like
measures (what their expressions use, not their format string expressions).

## Format strings

A measure's format string is its `formatString`, or its dynamic format string (`formatStringDefinition`, a DAX
expression), and a calculation item's format string expression replaces it while the item is applied. The local
engine evaluates them where the measure is evaluated; `tess query --formatted` shows a result's measure columns with
them, and these functions see them:

```sh
tess query ./Sales.pbip --data ./fixtures --formatted "EVALUATE SUMMARIZECOLUMNS ( 'Date'[Year], \"Sales\", [Sales Amount] )"
```

- **Dynamic format strings** are evaluated as the internal measure Power BI makes of them, `Table[_<measure>
  FormatString]`, which a query can name too (with the table: SQLBI, "Introducing dynamic format strings for DAX
  measures"): in the cell's filter context, after the context transition, with `SELECTEDMEASURE ()` the measure. It
  may name its own measure (Learn). `SELECTEDMEASURENAME` and `ISSELECTEDMEASURE` work there ("The same DAX patterns
  that work with calculation groups also work in dynamic format strings for measures": Learn). A measure with a
  static format string has no internal measure, and a `DEFINE MEASURE` has no format string at all.
- **Calculation items.** An item with a format string expression gives its result, evaluated where the item is
  applied; an item without one leaves the measure's static one ("all the calculation items except YOY% should use the
  format of the current measure in context": Learn). `SELECTEDMEASUREFORMATSTRING ()` in an item is the measure's format
  string, a dynamic one evaluated there.
- **`--formatted`** gives format strings to the columns of a top-level `SUMMARIZECOLUMNS`, `ADDCOLUMNS` or `ROW`
  whose expression is a measure reference (`[Sales]`, `'Sales'[Sales]`, `IGNORE ( [Sales] )`), as Power BI asks
  for them: one hidden column per measure, `IGNORE`d, so `SUMMARIZECOLUMNS` keeps the same rows. Each value is
  formatted as `FORMAT ( <value>, <format string> )` is, with everything [FORMAT](#text-functions-and-format)
  refuses refused here too (other cultures among it); BLANK is the empty text. The rows and values are the same as
  without the flag. Other columns, and the columns of other table expressions (`TOPN ( ... )`, a model table, a
  variable: `DEFINE VAR v = SUMMARIZECOLUMNS ( ... ) EVALUATE v`), are not told: they get no format string, whatever
  the measure's. A format string and the formatted value come together: a value FORMAT can't format refuses the
  query, also where its format string is known. Text and CSV output show the formatted values; JSON adds them (see
  [`query`](xmla.md#query)). An error in a row the result doesn't keep (one `SUMMARIZECOLUMNS` drops, one `START AT`
  skips) doesn't count.
- **Local only.** A server's DAX query result carries no format strings, so `--formatted` is refused with
  `xmla:` models.

Refused, as the sources leave them open or disagree: two calculation items with format string expressions that could
both be applied to one measure (Learn: "The highest precedence calculation group dynamic format string is the only
one applied"; SQLBI: `SELECTEDMEASUREFORMATSTRING` in the lower one sees "the one already modified by another
calculation item"), and an item without one whose expression may select one of a lower group; a measure's dynamic
format string (other than a text constant) under an item without a format string expression (Learn: it is "a lower
precedence to any calculation group", and where that is evaluated inside the item is not said); a measure reference,
`SELECTEDMEASURE` or a user-defined function in a format string evaluated under a calculation item (whether the item
applies to them there); the internal `_<measure> FormatString` measure in a model with calculation groups; a format
string expression that returns BLANK, an empty text or something that is not text; `SELECTEDMEASUREFORMATSTRING` for
a measure with no format string, and in a measure's own dynamic format string; the format string of a cell made
BLANK by several items selected under `selectionExpressionBehavior` Visual; under `--formatted`, a query that
redefines `_<measure> FormatString`.

## Dates, times and the clock

A date is a number of days since December 30, 1899, with the time of day as its fraction, on the real calendar
on both sides of 1900: DAX is not Excel, and February 1900 has 28 days. `DATE`, `TIME`, `DATEVALUE`, `TIMEVALUE`,
`EOMONTH`, `EDATE`, `TODAY`, `NOW`, `UTCTODAY`, `UTCNOW` and the `[Date]` column of `CALENDAR` and `CALENDARAUTO`
are dates (`dateTime` in results); `YEAR`, `QUARTER`, `MONTH`, `DAY`, `HOUR`, `MINUTE`, `SECOND`, `WEEKDAY`,
`WEEKNUM`, `DATEDIFF` and `NETWORKDAYS` are whole numbers; `YEARFRAC` is a decimal number.

- **The clock.** `TODAY`, `NOW`, `UTCTODAY` and `UTCNOW` return one instant, read once when the model is loaded:
  the calculated tables and columns and the query all see the same one. By default it is the machine's clock
  (local time for `TODAY` and `NOW`, UTC for the other two). `--now <ISO 8601>` sets it: `--now 2024-03-05`,
  `--now 2024-03-05T14:30:00`. The date and time as written is what `NOW` returns. With a UTC offset
  (`2024-03-05T14:30:00+02:00`, `...Z`) the UTC functions return that instant in UTC; without one they return the
  same as `NOW`. The machine's time zone is never used with `--now`, so a result is the same everywhere. In code:
  `Engine.prepareWith { Engine.Options.defaults with Clock = Some clock } db store`.
- **`DATE`** follows dax.guide's rules, each of which it shows a result for: a year from 0 to 49 has 2000 added and
  from 50 to 99 has 1900; months above 12 and at or below 0 move the year (`DATE ( 2008, 0, 2 )` is December 2,
  2007); days past the month's end and negative days move the month; day 0 and a year above 9999 are errors;
  decimals round; BLANK is 0. **`TIME`** wraps at 24 hours and takes 0 to 32767 in each argument.
- **`WEEKDAY`** takes return types 1, 2, 3 and 11 to 17; **`WEEKNUM`** 1, 2, 11 to 17 (the week with January 1 is
  week 1) and 21 (ISO 8601). **`DATEDIFF`** counts boundaries crossed, not whole intervals, with weeks from Sunday,
  and is negative when the first date is the later one. **`EOMONTH`** rounds its months and **`EDATE`** truncates
  them, as their pages say. **`NETWORKDAYS`** takes the weekend numbers 1 to 7 and 11 to 17 and a table of
  holidays. **`CALENDARAUTO`** scans every date column that is not a calculated column or in a calculated table,
  and takes the month a fiscal year ends in.
- **Text as a date.** The engine reads text by the model's culture and then tries other formats, most of which is
  written down nowhere. The local engine reads ISO 8601 text under any culture (`"2024-01-31"`,
  `"2024-01-31T13:45:00"`, `"13:45"`), and under an `en-US` model also the forms the documentation shows results
  for: `"10/15/2020"` and `"10-15-2020"` (day first when month first is no date: `"28/02/2020"`),
  `"March 3, 2008 3:00 PM"`, `"Feb 14, 2010"`. Any other text is refused.
- **Refused, because the sources leave it open or disagree:** `YEAR`, `MONTH`, `DAY`, `HOUR`, `MINUTE`, `SECOND`,
  `WEEKDAY`, `WEEKNUM`, `EOMONTH`, `EDATE` and `YEARFRAC` of BLANK (only `QUARTER`, `DATEDIFF` and `NETWORKDAYS`
  say what BLANK gives); the calendar parts of a plain number below 61 (Microsoft Learn dates such a number as
  Excel does, a day later than the zero date gives); `DATE` with a negative year; `HOUR`, `MINUTE` or `SECOND`
  where a fraction of a second would change the answer if it were rounded; `EOMONTH` and `EDATE` of a date with a
  time of day; `EDATE` before March 1900; `YEARFRAC` on the actual/actual basis, with a start after the end, or
  on a 30/360 basis with a date on the 31st or the last day of February; `DATEDIFF` where a fraction of a second
  would change the answer if it were rounded; `NETWORKDAYS` with a start after the end, a time of day, or
  dates as text; return types and weekend numbers the pages don't list.

## Text functions and FORMAT

`LEN`, `LEFT`, `RIGHT`, `MID`, `UPPER`, `LOWER`, `TRIM`, `REPT`, `SUBSTITUTE`, `REPLACE`, `SEARCH`, `FIND`, `EXACT`,
`CONTAINSSTRING`, `CONTAINSSTRINGEXACT`, `COMBINEVALUES`, `UNICHAR`, `UNICODE`, `CONCATENATE`, `FIXED` and `FORMAT`
are implemented for **en-US**. The model's culture is read from the model; `FORMAT`'s locale argument overrides it.

- **Culture.** Any culture other than en-US is refused, for `FORMAT` (model culture or locale argument), `FIXED`,
  `UPPER`/`LOWER`, and for a number with decimals turned into text by `&` or a text function (the decimal separator
  is the culture's). A model that names no culture is refused too: a server would use its own language. Nothing is
  ever formatted by .NET, so the machine's culture changes nothing.
- **`FORMAT` numbers.** `General Number`, `Currency`, `Fixed`, `Standard`, `Percent`, `Scientific`, `Yes/No`,
  `True/False`, `On/Off`, and custom formats: `0` `#` `.` `,` (grouping, and scaling by 1000 right before the decimal
  point), `%`, `E+`/`E-`/`e+`/`e-`, quoted and `\`-escaped literals, one to three `;` sections. A custom format is
  literal text, digit placeholders, literal text: placeholders in two places (`0-0`) are refused.
- **`FORMAT` dates.** `General Date`, `Long Date`, `Short Date`, `Long Time`, `Medium Time`, `Short Time`, and custom
  formats: `d dd ddd dddd dddddd w ww m mm mmm mmmm q y yy yyyy h hh n nn s ss`, `AM/PM am/pm A/P a/p AMPM`, `/`, `:`,
  literals. `m` right after `h` (with or without `:`) is minutes.
- **Upper-case date letters.** A run of `D M Y H N S W Q` in upper case is read as the lower-case letters
  (`YYYY`, `MMM DD`, `DDDD`, `HH:NN:SS`). The pages write the characters in lower case and show only `\QQ yyyy`
  (DAX Guide) and `MMMM` (Power BI's own auto date/time table, whose Month column is shown as January). The
  characters are Visual Basic's (OLE Automation), which Microsoft says were not case-sensitive before VB 2005. The
  2010 DAX pages describe .NET's case-sensitive characters instead; where they give an upper-case letter another
  meaning it is refused: `M`/`MM` right after the hour (`hh:MM`: month there, minutes in Visual Basic), `H` with
  AM/PM (always 24-hour there), `Y` alone (the year there, the day of the year in Visual Basic) and the formats `D`,
  `M` and `Y` on their own, and a run in mixed case (`Mmm`: the month then minutes there). Mixed-case AM/PM
  (`Am/Pm`) is refused too.
- **BLANK.** `FORMAT ( BLANK (), ... )` is the empty text. A BLANK argument read as text is the empty text
  (`LEN ( BLANK () )` is 0), but a function that returns text refuses a BLANK text: no source says whether the real
  engine returns BLANK or the empty text.
- **Refused because the sources disagree** (Microsoft Learn against DAX Guide): `REPT` zero times and with a
  fraction; the formats `c`, `ddddd`, `ttttt` and `Medium Date`; `Percent` of a number that needs a thousand
  separator; TRUE or FALSE turned into text.
- **Refused because no source says:** a position or a count that is negative, fractional or past the end of the text
  (`MID ( "abcde", 4, 10 )`; `LEFT` and `RIGHT` past the end are documented and work); text with surrogate pairs
  where a position counts (it depends on the model's `UnicodeCharacterBehavior`); letters outside ASCII in `UPPER`,
  `LOWER` and as the text `SEARCH`/`CONTAINSSTRING` look for; kana, half- and full-width characters and combining
  marks in `SEARCH`; `SEARCH` in a case-sensitive model; a float whose decimal readings differ at the digits a format
  shows (`FORMAT ( 2.675, "0.00" )`: the float is just below 2.675); a negative number exactly halfway, one that
  rounds to zero, a negative `Currency` or `FIXED`, and a negative number through a one-section format that starts
  with literal text (`"$0.00"`); a number with a date format, a date with a number format, text as the value;
  `Short Time` before 10:00; `General Date` of a date with no time or of a time alone; `ww` of a Sunday; a time
  with a fraction of a second; dates before March 1, 1900; `TOCSV` and `TOJSON`.
- A measure's format string (`FormatString`, or a format string expression) is **not** applied to the values of query
  results: the real engine doesn't apply it either. `--formatted` shows them formatted, locally (see
  [format strings](#format-strings)).

## Time intelligence

The classic time intelligence functions, over a date column:

| | Functions |
|---|---|
| First and last | `FIRSTDATE` `LASTDATE` `STARTOFMONTH` `STARTOFQUARTER` `STARTOFYEAR` `ENDOFMONTH` `ENDOFQUARTER` `ENDOFYEAR` |
| To date | `DATESMTD` `DATESQTD` `DATESYTD` `TOTALMTD` `TOTALQTD` `TOTALYTD` |
| Shifting | `DATEADD` `SAMEPERIODLASTYEAR` `PARALLELPERIOD` `PREVIOUSDAY` `PREVIOUSMONTH` `PREVIOUSQUARTER` `PREVIOUSYEAR` `NEXTDAY` `NEXTMONTH` `NEXTQUARTER` `NEXTYEAR` |
| Ranges | `DATESBETWEEN` `DATESINPERIOD` |
| Balances | `OPENINGBALANCEMONTH` `OPENINGBALANCEQUARTER` `OPENINGBALANCEYEAR` `CLOSINGBALANCEMONTH` `CLOSINGBALANCEQUARTER` `CLOSINGBALANCEYEAR` |

Each is implemented to the definition DAX Guide, SQLBI or Microsoft Learn publish for it (the header of
`Engine/Functions/TimeIntelligence.fs` lists them):

- **The dates argument** is a date column, which reads as `CALCULATETABLE ( DISTINCT ( column ) )` (so in a row
  context the row becomes the filter), or a table expression of one column that has the lineage of a date column
  (`VALUES ( 'Date'[Date] )`, a variable, the result of another of these functions).
- **The result** has the lineage of that column and only dates the column has, so it filters the column as a
  `CALCULATE` argument. `FIRSTDATE` and `LASTDATE` are also values.
- **`DATEADD`** by `MONTH`, `QUARTER` or `YEAR` works month by month: a month whose every date (of those the column
  has) is selected becomes the whole other month; otherwise each day moves, to the last day of a shorter month, and
  a selection with the last two days of its month runs to the end of the other month. `PARALLELPERIOD` returns
  whole months, quarters or years.
- **Year end dates** (`DATESYTD`, `TOTALYTD`, `STARTOFYEAR`, `ENDOFYEAR`, `PREVIOUSYEAR`, `NEXTYEAR`, the year
  balances) are a text literal: `"6/30"`, `"06-30"`, `"2000-06-30"`.
- **The date-table rule:** a filter argument of `CALCULATE` on the date column of a date table also removes the
  filters on every other column of that table, as if `ALL ( 'Date' )` were written next to it. A date table is one
  marked as a date table (data category `Time`, with the date column as its key), or one whose date column is the
  one side of an active relationship from another date column. A filter on any other column removes nothing else.

Refused, because the sources don't settle them (each error says which):

- over a date column, the `WEEK` interval, `DATEADD`'s Extension and Truncation and `DATESINPERIOD`'s EndBehavior
  (calendar-based arguments; the week functions over a date column are an error, as the sources say);
- a True/False expression as the dates argument; a table of dates that are not the values of a model column; a date
  column with a time of day;
- a year end date that reads differently by culture (`"6/7"`, the day first as in `"30/6"`, a month name) or is in
  February;
- `FIRSTDATE` / `LASTDATE` as a table, and `DATESMTD` / `QTD` / `YTD`, when no date is selected; `DATESMTD` /
  `QTD` / `YTD` (and `TOTALMTD` / `QTD` / `YTD`) when other filters hide the first date of the period, where SQLBI's
  and DAX Guide's definitions give different dates;
- `DATEADD` and `SAMEPERIODLASTYEAR` of a selection of dates with gaps on a complete date table: Microsoft Learn
  says it is an error, SQLBI moves each month on its own;
- `DATEADD` of the last day of a month, selected without the day before it, to a longer month; `PARALLELPERIOD` of
  a selection that skips a whole month, quarter or year; `DATESINPERIOD` from a day the other month doesn't have
  (unless it is a month end), or from a BLANK start;
- the date-table rule where it is not documented and would change the result: `KEEPFILTERS` on a date filter, a
  filter that pairs the date column with other columns, a key column of a table that is not marked, the key of a
  relationship that is not active, and a context transition that filters the date column while other filters on
  the date table hide that date (a measure iterated over `ALL ( 'Date'[Date] )` under a filter on the year) or
  while a filter the rule would remove also restricts another table (a snowflaked table, a filter pairing a Date
  column with another table's, `SUMMARIZE`'s own extension columns). Iterating `VALUES ( 'Date'[Date] )` or
  `ADDCOLUMNS ( SUMMARIZE ( … ) )` by date under a filter on the year is fine, as is `SUMMARIZECOLUMNS` by date
  with the year as its filter argument.

On a date column that is not unique or has gaps (a fact table's), `DATEADD` and `SAMEPERIODLASTYEAR` of a selection
with gaps are an error, with the message the Power BI community quotes from the engine: "Function 'DATEADD' expects a contiguous selection when the date column is
not unique, has gaps or it contains time portion."

## Calendar-based time intelligence

A model's calendars (TOM `Calendar` objects: a table's columns tagged as Year, Quarter, Month, Week, Date and the
partial categories such as Month of Year, plus time-related columns) can be the dates argument, by name:
`TOTALYTD ( [Sales], 'Fiscal' )`. Sources: Microsoft Learn's "Implement time-based calculations in Power BI"
(calendar-based time intelligence), the functions' pages on Learn and dax.guide, and SQLBI's "Introducing
calendar-based time intelligence in DAX".

| | Over a calendar |
|---|---|
| To date | `DATESWTD` `DATESMTD` `DATESQTD` `DATESYTD` `TOTALWTD` `TOTALMTD` `TOTALQTD` `TOTALYTD` |
| First and last | `STARTOFWEEK` … `STARTOFYEAR`, `ENDOFWEEK` … `ENDOFYEAR` |
| Neighbours | `PREVIOUSDAY` `NEXTDAY`, `PREVIOUSWEEK` … `PREVIOUSYEAR`, `NEXTWEEK` … `NEXTYEAR` |
| Balances | `OPENINGBALANCEWEEK` … `YEAR`, `CLOSINGBALANCEWEEK` … `YEAR` |
| Ranges | `DATESBETWEEN` |

- **The rows** are the calendar's table's, in the order of the column tagged as Date; a period (week, month,
  quarter, year) is the rows that share the values of the categories that identify it (Week, or Week of Year and
  Year, and so on: Learn's "Period Uniqueness" sets). The dates need not be complete: `PREVIOUSDAY` of a Monday in
  a table without weekends is the Friday.
- **The result** is a table of the primary column of each category and the time-related columns. With a calendar,
  `DATES*TD` end on the last visible date when the Date column is filtered, at the end of the last visible period of
  a filtered category otherwise, and at the end of the period when only untagged columns are filtered (dax.guide).
- **Context clearing.** A `CALCULATE` (or `CALCULATETABLE`) filter argument on a tagged column clears, first, the
  filters on the columns of the categories Learn's lattice relates to it, in every calendar of the table: a filter
  on the year clears the month, the week and the date, not the month of the year. This holds for any filter
  argument, not only the time intelligence functions ("regardless if the context is set using time intelligence
  functions or otherwise"). Untagged columns of the table keep their filters, and the date-table rule doesn't apply
  to a calendar's result. Every other way a filter is set (a context transition, `KEEPFILTERS`, `SUMMARIZECOLUMNS`'
  groups and filter arguments, `FIRSTNONBLANK`'s own filter) clears nothing, and is refused where clearing would
  change the rows: grouping by the date under a filter on its week is fine, by the year under a filter on the month
  is not.
- **Errors**, as the sources state them: a week function over a date column or a calendar without weeks; a calendar
  without the categories a function needs; a year end date with a calendar; a filter on a column another calendar
  tags as Week when this calendar has no Week.

Refused, because the sources don't settle them: `DATEADD`, `SAMEPERIODLASTYEAR`, `PARALLELPERIOD`, `DATESINPERIOD`,
`FIRSTDATE` and `LASTDATE` over a calendar; a calendar without the Date category, or with semesters; tagged columns
with BLANKs, periods whose dates are not next to one another or don't sort in date order; a calendar of a date
table's date column alone; the cases above where clearing would change the rows, and a calendar's result over
another calendar's time-related columns; `DATES*TD` with no visible date, with a day category other than Date, a
time-related column or a column only another calendar tags filtered, with filtered periods that don't nest, or
with both the date and another calendar's week filtered over a calendar without weeks; filters on other tables
that cross-filter the calendar's table.

## Numbers, conversion and the identity functions

`RAND` and `RANDBETWEEN` are known names but always refused: a random answer can't be checked against a server's.

The trigonometric and hyperbolic functions (`SIN` … `ACOTH`, `DEGREES`, `RADIANS`) return a decimal number, in
radians. `ACOT` is between 0 and π (dax.guide's example table). The documented errors are errors: `COT` and `COTH`
of 0 or BLANK ("A BLANK argument is treated as 0": dax.guide), `COT` of 2^27 or more, `ATANH` of -1, 1 or beyond.
BLANK is refused elsewhere, as is an argument outside what a page says it "must" be where it doesn't say what
happens (`ASIN ( 2 )`, `ACOSH ( 0.5 )`, `ACOTH ( 1 )`), and a result too large for a float.

Arithmetic follows the published descriptions of the engine (SQLBI's "Rounding errors with different data types in
DAX" and "Understanding numeric data type conversions", Microsoft Learn's data types tables); where they say nothing
the engine refuses rather than pick:

- whole numbers and fixed decimal numbers wrap around on `+`, `-` and whole × whole, as SQLBI shows
  (`5000000000000000000 + 5000000000000000000` is `-8446744073709551616`); a result that lands on the two values
  that mean BLANK (2^63 - 1 and -2^63) is refused, and so is `SUM`/`SUMX` past the 64-bit range;
- a whole or fixed decimal number compared with a decimal number is compared as decimal numbers (floating point), as
  the comparison table says: `CURRENCY ( 0.1 ) = 0.1` is TRUE, and so is `9007199254740993 = 9007199254740992.0`;
- a number times a date follows the multiplication table: a whole number times a date is a whole number, a fixed
  decimal or decimal number times a date a decimal number;
- fixed decimal results round half away from zero to four places; a value whose decimal reading and binary value
  disagree about the rounding (`ROUND ( 1.005, 2 )`, `CURRENCY ( 2.00005 )`) is refused, since which one the engine
  rounds isn't documented;
- `DIVIDE` returns what `/` returns, a fixed decimal number for a fixed decimal numerator;
- text read as a number takes only what doesn't depend on the culture: `"1,234"`, `"$5"` or `" 12 "` is refused,
  `"abc"` is an error; `CONVERT` and `VALUE` read text as a date as the date functions do (see "Text as a date").

`USERNAME`, `USERPRINCIPALNAME`, `USEROBJECTID`, `USERCULTURE` and `CUSTOMDATA` come from the connection on a server;
the local engine has none, so it refuses them unless the caller says who is asking:

```fsharp
Engine.withIdentity : Identity -> Engine.Prepared -> Engine.Prepared
// { Identity.none with UserName = Some "CONTOSO\\ann"; UserCulture = Some "en-US" }
```

A field left `None` stays refused (`CUSTOMDATA` is then BLANK, as for a connection without it). The original
`Prepared` is unchanged; calculated columns and tables never see the identity. On the command line:

```sh
tess query model.bim --data fixture "EVALUATE { USERNAME () }" --user-name 'CONTOSO\ann'
```

with `--user-name`, `--user-principal-name`, `--user-object-id`, `--user-culture` and `--custom-data`. On a server
model, `--user-name` / `--user-principal-name` and `--custom-data` become the connection's `EffectiveUserName=` and
`CustomData=` (see [roles](#roles-row-level-security)); the other two have no connection property and are refused there.

## Roles (row-level security)

`--role <name>` (repeatable) runs the query as a member of the model's roles: each role's row filters hide the rows
they don't keep, and what that leaves is all the query sees.

```sh
tess query model.bim --data fixture --role Europe "EVALUATE { [Sales Amount] }"
tess query model.bim --data fixture --role ByEmail --user-principal-name ann@contoso.com "EVALUATE Customer"
tess query 'xmla:<server>;<database>' --role Europe --role Asia --custom-data 7 "EVALUATE { [Sales Amount] }"
```

On a server the roles become the connection's `Roles=` and the identity options `EffectiveUserName=` and
`CustomData=` ("Connection string properties (Analysis Services)" on Learn; `EffectiveUserName` needs administrator
rights on the server). Nothing else changes: the connection is the one the address already asked for. A role name
with a comma, or an address that already sets one of those properties, is a usage error. Learn's note for Power BI:
"If specifying roles when connecting to a Power BI Premium workspace, use workspace roles from the Power BI security
model."

Over fixture data, the engine follows what Microsoft Learn ("Roles in Analysis Services tabular models",
"Row-level security (RLS) with Power BI", "RLS guidance") and SQLBI ("Computing accurate percentages with row-level
security", "Implement non-visual totals with Power BI security roles", "DAX limitations with inactive relationships
and RLS") describe:

- **A filter is evaluated on every row of its table**, in a row context, over the data as the model was processed
  (no role applied), with the identity options for `USERNAME`, `USERPRINCIPALNAME`, `CUSTOMDATA` and the others. It
  must be `TRUE` or `FALSE`.
- **It reaches other tables over active relationships only**, from the one side to the many side, and back where
  the relationship's security filtering behavior is BothDirections ("Apply security filter in both directions"),
  whatever its cross-filter direction. A role's filters on several tables intersect.
- **Several roles add up**: a row is visible if one of the roles lets it through. A role with the Administrator
  permission sees everything; one with None or Refresh adds nothing (if no role given can read, the query fails:
  `role-cannot-read`). An unknown role is `role-not-found`.
- **The hidden rows are gone**, not filtered: `ALL`, `REMOVEFILTERS` and `ALLSELECTED` can't bring them back, a
  percentage over `ALL ( Customer )` is of what the user may see, and `ISFILTERED` / `ISCROSSFILTERED` don't see the
  role (SQLBI: "Any customer from outside Europe disappears as if they did not exist").
- **Calculated columns and tables keep the values they were computed with**, at processing time, without the roles.

Refused, because the sources don't settle them: object-level security (a table or column a role hides); a filter
that reads its own table other than as the row being filtered (`MAX ( Customer[Key] )`, `PATHCONTAINS` over a
`LOOKUPVALUE` of the same table), reads another table the roles' filters reach, or refers to a measure or a
user-defined function; a role's filter that reaches a one-to-one or many-to-many relationship, or two tables along
more than one path; rows of the many side with no match on a filtered one side, and a role that would add or take
away a table's blank row (it is decided when the model is processed); two roles whose filters reach a table through
different tables (the union of each role's rows, or each table's filters combined first: the sources can be read
both ways); `USERELATIONSHIP` and `CROSSFILTER` anywhere a query under a filtering role may reach ("The
UseRelationship() and CrossFilter() functions may not be used when querying 'Sales' because it is constrained by
row-level security", the error SQLBI quotes); `DEFINE COLUMN` / `DEFINE TABLE` under a role. A role's members list
is not used: `--role` says which roles the query runs as.

In code:

```fsharp
Engine.asRoles : string list -> Engine.Prepared -> Result<Engine.Prepared, TessError>
// Engine.prepare db store |> Result.map (Engine.withIdentity id) |> Result.bind (Engine.asRoles [ "Europe" ])
Engine.roles : Engine.Prepared -> string list
```

`asRoles` builds a copy of the prepared model without the hidden rows (`p` is unchanged). Give the identity first:
the filters are evaluated with it, and a query refuses if `withIdentity` changes it afterwards.

## ALLSELECTED

`ALLSELECTED ()`, `ALLSELECTED ( table )` and `ALLSELECTED ( column, ... )`, as a filter argument of `CALCULATE` /
`CALCULATETABLE` and (with an argument) as a table. They follow two mechanisms the sources describe:

- **Shadow filter contexts** (SQLBI, "The definitive guide to ALLSELECTED"). Every iterator leaves one, holding the
  table it iterates. For each column, ALLSELECTED restores the last one that filters it. As a modifier with a
  table or no argument, the explicit filters on the other columns stay. As a table, a column gives its values in
  that shadow, or every value when no shadow filters it. A cross-filter doesn't count.
- **SUMMARIZECOLUMNS rows** (SQLBI, "ALLSELECTED best practices"). With no iteration since the row, ALLSELECTED
  removes the filter of the group-by columns it names. They get back the filters SUMMARIZECOLUMNS was evaluated
  under: its filter arguments and the caller's. These are a visual's totals. Inside an iteration in the row, the
  shadow filter contexts apply.

`SUMMARIZE` over a table with no columns but its group-by columns (Microsoft Learn's visual-totals example) counts
as an iteration over the groups.

It refuses what the sources leave open:
- a column form on a column that has a filter of its own but no shadow (shown only for a cross-filtered column);
- the shadow of an iteration over a table restoring a column of a table it expands to (`SUMX ( Sales, ... )` with
  `ALLSELECTED ( Product[Color] )` or `ALLSELECTED ()`);
- a group-by column of SUMMARIZECOLUMNS inside an iteration that doesn't iterate it;
- a SUMMARIZECOLUMNS row whose filters a CALCULATE changed first;
- SUMMARIZECOLUMNS inside an iteration or inside another's row;
- `KEEPFILTERS ( ALLSELECTED ( ... ) )`, or more than one ALLSELECTED, or another removing or relationship
  modifier next to it in one `CALCULATE`;
- several columns as a table unless one shadow holds them all;
- `ISFILTERED`, `ISCROSSFILTERED` and `HASONEFILTER` of a filter restored from a shadow;
- row contexts that aren't an iterator's: a calculated column, `ORDER BY`, a True/False filter argument, a wider
  `SUMMARIZE`, `GROUPBY`, a function called from a row context.

A `NONVISUAL` filter argument of `SUMMARIZECOLUMNS` is not restored (see `SUMMARIZECOLUMNS` above for the shapes
implemented).


## Window functions

`INDEX`, `OFFSET`, `WINDOW`, `RANK` and `ROWNUMBER`, with `ORDERBY` (ASC/DESC, `BLANKS DEFAULT`/`FIRST`/`LAST`),
the blanks argument (`DEFAULT`, `KEEP`, `FIRST`, `LAST`), `PARTITIONBY` and `MATCHBY`. The header of
`Engine/Functions/Window.fs` has the sources and the rules.

- **The current row** is found by apply semantics (SQLBI): each model column of the relation takes its value from
  the row context, or, when no row context has it, its values in the filter context (all combinations, no
  auto-exist); every matching row of the relation is a current row, and the results are put together without
  repeats (SQLBI's DISTINCT; Learn says only "a union"). `RANK` with more than one current row (a total) is BLANK;
  `ROWNUMBER` then is refused. In a calculated column of the relation's table, the columns not computed yet are
  left out when a computed unique column identifies the rows.
- **Ties.** Rows that tie on every `ORDERBY` key are fine for `RANK`. For the others the engine adds columns in a way
  no source documents, so a result that depends on how ties are broken is refused; a relation whose rows nothing
  can tell apart is the documented error ("…'s Relation parameter may have duplicate rows, which isn't allowed"),
  and so is a model table whose order ties and that has no unique column at all. A model table counts as
  identified by a column the model declares its key or unique (TOM `IsKey`/`IsUnique`); one whose values just
  happen to be unique is refused when the order ties.
- An omitted `ORDERBY` orders by the relation's other columns; since the order they are added in is not
  guaranteed, every order is tried and the call is refused if they disagree.

Refused: an omitted relation (it is `ALLSELECTED` of the `ORDERBY` and `PARTITIONBY` columns), `reset` and axes
(visual calculations), a column two nested row contexts both have (Learn: an error; SQLBI: the innermost),
`RELATED` and related tables' columns in `PARTITIONBY`/`MATCHBY`, an `ORDERBY` column that is not the relation's,
BLANK next to zero or negatives and mixed types in a key (as everywhere), a BLANK or fractional delta or bound, a
model table with no declared key or unique column whose order happens not to tie, and calls where the sources' readings of
which columns identify the current row (Learn, DAX Guide, SQLBI) give different results (`RANK` ordered by
expressions only, say).

## What it refuses

- **Not implemented yet:** visual calculations,
  query parameters, detail rows, KPIs, natural joins of tables with model columns (joined over their expanded
  tables). Without `--role` a query runs as an administrator's would; object-level security is refused (see
  [roles](#roles-row-level-security)).
- **Model shapes:** an inactive one-to-one relationship, relationships on several columns, DirectQuery and
  Dual tables: every table connected to one is closed to queries, the rest of the model works. A calculation group
  is closed when a query names it. Active relationships that give two filter paths between two tables. A column
  with Group By Columns (a field parameter's display column): a query that groups by it without them fails on a
  server, so a query that reads it is refused (the table itself is computed, and its other columns work), except
  `SUMMARIZECOLUMNS` grouping by it together with all of its Group By Columns, as Power BI queries a field
  parameter ("The columns specified in Group By Columns must be present in the grouping conditions of any query
  that includes the target column": SQLBI, "Understanding Group By Columns in Power BI"). Under such a group,
  measures see the group's row; reading the column there (`SELECTEDVALUE`, `VALUES`), filtering it alone, a context
  transition over the result, and `SUMMARIZE`, `ALL` and `TREATAS` over it are still refused.
- **One-to-one relationships, by their data:** keys missing on both sides (both tables would need a blank row),
  more than one unmatched row on the side with the extra keys, and a blank row either table has from another
  relationship. The declared direction doesn't matter (a one-to-one relationship filters both ways and its two
  expanded tables are the same), except for a calculated key column or keys of different types, whose declared
  `to` table may not have keys the `from` table lacks. `CROSSFILTER` `ONEWAY` on one.
- **Many-to-many relationships:** a filter that keeps every row of the filtering table (whether it still hides the
  rows that match nothing isn't documented), a BLANK on one side meeting a BLANK on the other, columns of different
  types, `CROSSFILTER` `ONEWAY` ("unclear which side is the lookup side": Learn), and `ALLCROSSFILTERED` in a model
  with one.
- **Where the sources don't say what the engine does,** for instance:
  - a one-side key that repeats or is BLANK;
  - keys with no match at the start of a chain of relationships (whether the blank row exists);
  - a filter that crosses a both-directions relationship while keeping every row of the many side;
  - `TOPN` without an order-by on more rows than asked for, and `CONCATENATEX` without one over different texts
    (the engine defines no order), and sorting where BLANK meets zero or negative values, or values of different
    types (also in `START AT`);
  - whole-number arithmetic that reaches -2^63 or 2^63-1 (the documented range stops short of them, and the engine
    uses one of them for BLANK); a total of an aggregation that overflows;
  - `BLANK () && BLANK ()`, `BLANK () || BLANK ()` and `NOT BLANK ()` (the sources disagree);
  - text turned into a number when it has spaces around it, a currency symbol, thousands separators or `%`;
  - a variable whose value is an error when the result may not need it (the engine may or may not evaluate it);
  - `MAXX` / `MINX` with their variant argument TRUE, and over numbers mixed with dates or TRUE/FALSE;
  - a date or TRUE/FALSE turned into text by `&` (the format depends on the model's culture), text read as a date
    in arithmetic, a date on the left of `*`, a whole number times a date with a time of day;
  - `DISTINCTCOUNT` of a column while its table's blank row is visible (whether that BLANK is counted: write
    `COUNTROWS ( DISTINCT ( ... ) )` or `COUNTROWS ( VALUES ( ... ) )` to say which);
  - a context transition, or a column read, inside a user-defined function that was called from a row context
    (the documentation says what the function's parameters see of the caller's row context, not its body);
  - a name that is both a column of the row context and a measure (except in `ORDER BY`, where it is the result's
    column);
  - a table built on a model table with columns added, used as a filter or iterated with a measure, when that table
    relates to others (whether those are still filtered).

Without `ORDER BY`, rows come in the order the engine produced them. As on a server, that order is not defined:
compare such results as sets.

## `--coverage`

`tess query <model> --coverage` reads the model only (no data) and lists every measure, calculated column,
calculated table, calculation item and role (its row filters, and what applying it refuses) that uses something the
engine doesn't support, with what; then how many of each kind it can
evaluate, and the unsupported things ranked by how many objects use them. It follows references: a measure that
calls an unsupported measure or function is unsupported, and so is everything that depends, over relationships, on a
calculated table or column that can't be computed (a date table that can't be computed closes most of a model).
`--json` writes `{"command": "query coverage", "source", "summary": {measures, calculatedColumns,
calculatedTables, calculationItems, roles: {total, supported}}, "objects": [{ref, kind, supported, unsupported: [...]}], "unsupported":
[{reason, objects}]}`.

Coverage runs each function's static check of its argument forms, the same one the evaluator runs before a call. A
refusal that depends on where a function is used (`KEEPFILTERS` on an iterator's table), on the evaluation context (a
function called from a row context) or on the data (a key that repeats, a blank row that can't be decided, values
of mixed types) only shows when evaluating: coverage is a lower bound on what is refused, not a guarantee.

## For developers

The engine is `src/Tessera.Core/Engine/`:

```fsharp
Engine.prepare        : Database -> DataStore -> Result<Engine.Prepared, TessError>   // bind, compute calculated columns/tables
Engine.evaluateQuery  : Engine.Prepared -> string -> Result<QueryResult list, TessError>
Engine.evaluateScalar : Engine.Prepared -> string -> Result<QueryValue, TessError>    // no filter context, no row context
Engine.coverage       : Database -> (ObjectRef * string list) list
Engine.failures       : Engine.Prepared -> (ObjectRef * TessError) list               // what prepare couldn't compute
Engine.functions      : (string * string) list                                        // name, family
```

A `Prepared` is immutable and can be shared between threads. Every DAX function is an entry in a registry, in a
file per family under `Engine/Functions/`; a function receives its arguments unevaluated together with the
evaluation context, so iterators and `CALCULATE` are ordinary entries. Adding a family is a new file, a `<Compile>`
line and a line in `Registry.fs`; the header of `Engine/Types.fs` documents the API with examples. Each entry also
carries a static check of its argument forms, which is what `coverage` runs.

Performance, on a 100,000-row fact table with three dimensions (see `EnginePerfTests`, on a laptop): `SUMX` over
the fact about 0.15 s, `CALCULATE` with two filters under 0.01 s, `SUMMARIZECOLUMNS` by a dimension column with a
measure 0.1 to 0.2 s (by two dimensions with a filter 0.35 s), a measure per row of the fact (100,000 context
transitions) 0.35 s.

## Caches and the benchmark

An iterator's context transitions build a new filter context per row, so the engine shares work between filter
contexts with the same filters (`Engine/Caching.fs`): the rows a table shows (per model and relationship set-up,
keyed on the filters that can reach the table), and, for one query, the value of a measure reference and the filter
a True/False filter argument stands for. Every key holds everything the result is computed from, compared by content;
a measure that reads anything else of the context (ALLSELECTED, ISINSCOPE, ISFILTERED, ISCROSSFILTERED,
HASONEFILTER, through the measures and functions it names) is never shared, and nothing is shared while a
calculated column or table is being computed or when the model has calculation groups. The rules this sets for
function authors are in the header of `Engine/Types.fs`.

`bench/` is a benchmark on the spaceparts sample model (every measure at the total and by each dimension, the
Station matrix of `[CF - Bar Chart X Min]` / `Max`, coverage), not part of the tests or CI; it fingerprints every
answer so two runs can be compared. On 200 rows the Station matrix (200 stations, each a MINX over all of them under
ALLSELECTED) went from more than 30 minutes to about 2 s. `bench/RESULTS.md` has the numbers at 200, 2,000 and 20,000
rows. `--check` runs it in the incremental check mode below.

## Incremental filter contexts

A filter context made from another (a context transition, CALCULATE's filters) works out a table's visible rows
from the rows under the filters it kept, which every row of an iteration shares, intersected with what its added
filters allow: for a transition on a dimension row, the fact rows of that one key instead of a scan of the fact
table. That is exact where a table's rows are intersections and preimages of its filters only; across a relationship
that filters both ways, a limited or a one-to-one relationship, while anything is being computed, or where a column
read could raise, the engine computes the rows in full as before. The argument, and what it asks of an engine
author, are in "Incremental filter contexts" in the header of `Engine/Types.fs`. A check mode recomputes every
incremental result in full and fails on any difference: `TESSERA_CHECK_INCREMENTAL=1` for the test suite,
`--check` for the benchmark, `(Engine.incrementalStats prepared).Check` in code. What remains slow is the cost of
a context transition itself: an iterator over 2,000 dimension rows with a measure per row is 2,000 CALCULATEs
however few fact rows each one reads (see `bench/RESULTS.md`).
