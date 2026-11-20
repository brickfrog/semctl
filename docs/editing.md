# Editing models

## Editing commands

The editing commands (`rename`, `set`, `add`, `remove`, `move`, `script apply`, `translations import`,
`perspective add|remove`, `lint --fix`, `bpa rules add|remove|ignore|unignore` and `format` on a model) follow one
set of conventions, the same as the Tabular Editor CLI's.

**Saving:**
- **A dry run by default.** The command shows what would change and writes nothing; stderr says
  `Dry run - nothing saved. Add --save to persist.`
- **`--save`** writes the model back in place: its own format, location and line endings, with restricted
  information kept.
- **`--save-to <path>`** writes elsewhere, like `convert` (add `--force` to overwrite a non-empty destination).

**Output:**
- The change as a line diff, like `diff`.
- `--stat` prints one line per object.
- `--name-only` prints just the changed references.
- Notes and warnings go to stderr, so stdout is only the change.
- `--json` writes the change set (see `diff`) plus `dryRun`, `introducedErrors` (findings), `warnings`, `notes`,
  `saved` (the format and path written; left out on a dry run) and `files` (report files `rename` and `move` change, see
  [Reports](#reports-pbir)).

**Safety:** a change that introduces new errors is shown, but `--save` refuses it (exit 1) unless `--force`.
The checks are the `validate` rules an edit can break: unresolved and circular references, duplicate names,
invalid tokens, unknown functions, TOM's validation, and the errors of the syntax-tree rules (a malformed culture
name, `IGNORE` outside `SUMMARIZECOLUMNS`). Errors the model already had don't count, even on an object the edit
renamed. `tess interactive` and `tess mcp` gate their saves on the same checks.

## Renaming (`rename`)

`tess rename <model> <object> <new name>` renames an object and rewrites every DAX reference to it. Only the
reference tokens change; every other character of every expression stays as it was. It follows the
[editing conventions](#editing-commands): a dry run unless `--save` or `--save-to`.

What is rewritten, in all the DAX `deps` reads, resolved the same way (`VAR`s and parameters shadow tables):

| Renaming | Rewrites |
|---|---|
| a measure | `[Old]`, `'T'[Old]`, `T[Old]` that resolve to it, as `[New]` (`]]` escaped) |
| a column | qualified and unqualified references that resolve to it |
| a table (also calculated and calculation-group tables) | the table in `Old`, `'Old'`, `Old[X]`, `'Old'[X]` |
| a calendar | `Old`, `'Old'` where they resolve to it |
| a function | calls, `Old(...)` |

The arguments of `NAMEOF` and `TABLEOF` are references like any other, so field parameters
(`{ ("Revenue", NAMEOF('Sales'[Revenue]), 0) }`) follow a rename. Their labels are text and stay as they are; a
warning names a field parameter whose label still shows the old name.

Renaming a table or column also updates the `sourceColumn` of calculated-table columns that map to it, such as
`Sales[Amount]` in a table defined as `FILTER(Sales, ...)`: the table's DAX now returns `Sales[Net]`, so its column
must map to that. The column keeps its own name. A `sourceColumn` like `[Value1]` names a column the table's own DAX
makes, and stays.

A measure qualified with a table other than its own (`'Date'[Revenue]` for a measure in `Sales`) isn't rewritten:
tess, like Tabular Editor 2, resolves a qualified name only in the table it names, so that reference doesn't resolve
to the measure (`validate` reports it). A warning names each one.

Unquoted table references stay unquoted only if the new name is a plain identifier (letters, digits, `_`), isn't a
DAX keyword, and isn't already used as a name in that expression; otherwise they're written `'New'` (`''`
escaped). Function names must be identifiers (dots allowed).

Hierarchies, levels, partitions, calculation items, roles, perspectives, cultures, relationships and data sources
are renamed only: DAX can't refer to them by name (reports can name hierarchies and levels; see
[Reports](#reports-pbir)). Sort-by columns, hierarchy levels, relationships, perspectives
and translations point at objects, not names, so they follow any rename. Power BI's `PBI_QueryOrder` model
annotation, which lists queries by name, is kept in step.

Not rewritten:
- comments and string literals, even ones that look like references (`// [Old]`, `"Old"`)
- M: named expressions (Power Query parameters and queries) are renamed, with a warning listing the M that
  mentions the old name; the same warning appears for tables and data sources whose name appears in M
- reports, except the field references of PBIR reports in the same PBIP project (see [Reports](#reports-pbir))

`rename` refuses:
- empty names, names with leading or trailing spaces, and names with line breaks or other control characters
- names already taken, case-insensitively: by a column, measure or hierarchy in the same table; by any measure (for
  a measure); by any table or calendar (for a table); by any function (for a function); by a sibling, otherwise
- the model object
- a new name that would change what another reference means. For example, a column named like a measure in another
  table would capture that table's unqualified `[Name]` references. tess checks this by applying the rename to a
  copy and comparing every reference. References that named nothing and now resolve are reported as warnings.
  Because `deps` doesn't know DAX keywords, this also refuses renaming a table to a word an expression already
  uses bare, such as `NOT`, `IN` or `RETURN`.

The same name is a no-op. A case-only rename (`Sales` to `SALES`) is allowed and rewrites references to match.
In a TMDL or PBIP folder, the object's file (`tables/SALES.tmdl`, `roles/...`) is renamed too, also on
case-insensitive file systems (Windows, macOS). Saves only rewrite `.tmdl` files whose content changed.

### Reports (PBIR)

When the model is part of a PBIP project, `rename` and `move` also update the report: every `X.Report` folder
next to the `X.SemanticModel` folder whose `definition.pbir` points at it (`byPath`). In the PBIR format
(`X.Report/definition/**/*.json`), visuals, pages, bookmarks and filters name model fields structurally, and
those names are rewritten in place, so every other byte of each file stays as it was:

| Renaming or moving | Rewrites |
|---|---|
| a table | `"Entity"` of `SourceRef`s and of `From` sources |
| a column | `"Property"` of `Column` (and date-variation) references to it |
| a measure | `"Property"` of `Measure` references to it |
| a hierarchy, a level | `"Hierarchy"` of `Hierarchy` references, `"Level"` of `HierarchyLevel` references |
| a measure, moved | the `"Entity"` of its own `Measure` references |

References resolve as Power BI resolves them: a field's table is its `SourceRef` `Entity`, or the source a
`Source` alias names in the enclosing `From`. Names match ignoring case. The edits show in the dry run, as a line
diff per file (`~ report Sales.Report/definition/pages/.../visual.json  (2 references)`), in `--stat` and
`--name-only`, and in the `--json` `files` array (`path`, `display`, `references`, `written`, and the changed
`lines` with `line`, `before` and `after`). They are written with `--save`, after the model and only if it saved,
and never with `--save-to`, since the report still points at the original model (a warning says so).

Not rewritten:
- `queryRef`, `nativeQueryRef` and formatting selectors (`"metadata"`). These are a visual's own names for its
  fields: Power BI itself keeps a `queryRef` after a field is renamed, and the format describes the native
  reference name as the name native expressions (such as visual calculations) use. Visuals keep working with them
  as they are.
- free text, such as titles, and literal values (filters and slicer selections store data values). This includes
  a field parameter's saved selection, stored as the text `NAMEOF` returned (`'Sales'[Total]`): after a rename,
  that selection no longer matches and must be made again
- report-level measures (`reportExtensions.json`, and fields with `"Schema": "extension"`): a warning lists the
  file when it mentions the old name
- a moved measure named in a filter through a `From` alias, which the filter's other fields share: a warning
  lists the files
- PBIR-Legacy reports (`X.Report/report.json`), which Power BI doesn't support editing outside Power BI Desktop:
  a warning lists the file when it mentions the old name. Save the report in PBIR format to have tess update it.
- reports in other folders, and reports connected to a published model (`byConnection`)

## Setting properties (`set`)

`tess set <model> <object> -p <path>=<value> ...` sets properties on one object. It follows the
[editing conventions](#editing-commands): a dry run unless `--save` or `--save-to`, the change shown as a diff,
and new errors refused unless `--force`. `-p` is repeatable, and all assignments apply as one change.

```sh
tess set ./Sales.pbip "[Revenue]" -p formatString='#,0.00' -p displayFolder=KPIs --save
tess set ./Sales.pbip "Date[Month]" -p sortByColumn=MonthNo -p summarizeBy=none --save
tess set ./Sales.pbip "[Revenue]" -p formatStringDefinition.expression='"#,0." & REPT("0", 2)'
tess set ./Sales.pbip "[Revenue]" -p kpi.targetExpression='[Budget]' -p kpi.statusGraphic='Traffic Light'
tess set ./Sales.pbip Sales -p annotations.Owner=finance -p isHidden=true
tess set ./Sales.pbip "[Revenue]" -p expression=- < revenue.dax      # the value from stdin
tess set ./Sales.pbip "[Revenue]" --unset description --unset annotations.Owner
```

**Paths** are the ones `get -p` prints: TOM's JSON property names, such as `formatString`, `isHidden`,
`displayFolder`, `expression`, `dataCategory`, `summarizeBy` and `sortByColumn`. They match case-insensitively.
- **Nested paths** reach into sub-objects: `formatStringDefinition.expression`, `detailRowsDefinition.expression`,
  `kpi.targetExpression`, `calculationGroup.precedence`, `source.expression` (a partition's M or DAX).
  A missing sub-object is created, except a calculation group: that would make the table a calculation group.
  Unsetting a sub-object's last property, or the sub-object itself (`--unset kpi`), removes it.
- **Named items:** `annotations.<name>` and `extendedProperties.<name>`. Everything after the first dot is the
  name, so it may contain dots. Setting one adds or updates it; an existing one matches case-insensitively. A new
  extended property is a string one. An existing JSON one stays JSON and takes the whole value as JSON text, even
  though `get -p` flattens it into `extendedProperties.<name>.value...`.

`tess get <model> <object> --properties` lists every path `set` accepts on that object.

**Values** are read according to the property's type:

| Type | Accepted |
|---|---|
| string, expression | the text as-is (quote it for the shell) |
| bool | `true` or `false`, any case |
| int, number | `42`, `-1`, `0.25` (invariant culture) |
| enum | a member's TOM name, any case: `summarizeBy=none`, `dataType=int64`, `crossFilteringBehavior=bothDirections`. Members the model's compatibility level lacks are refused |
| reference | an object, in any form `get` accepts |
| `-` | read the value from stdin, taken literally (minus one trailing line break). Only one `-p` can use stdin |
| `null` | clear a string or reference, reset anything else to TOM's default (`isAvailableInMdx` goes back to `true`, `summarizeBy` to `default`), remove a sub-object, annotation or extended property. `--unset <path>` does the same. To store the text `null`, pass it on stdin |

Reference properties:

| Object | Property | Refers to |
|---|---|---|
| column | `sortByColumn` | a column of the same table: a bare name (`MonthNo`) is looked up there first |
| level | `column` | a column of the hierarchy's table |
| relationship | `fromColumn`, `toColumn` | a column; a bare name is looked up in that end's current table first |
| model | `defaultMeasure` | a measure |
| partition (legacy query source) | `source.dataSource` | a data source |
| named expression | `parameterValuesColumn`, `expressionSource` | a column; a named expression |
| column | `alternateOf.baseColumn`, `alternateOf.baseTable` | a column; a table (aggregations) |

A reference to an object of the wrong kind is refused ("that is measure:Sales/Total, not a column").

`set` refuses, with exit code 2 unless noted:
- unknown properties (`unknown-property`), suggesting close matches: `did you mean 'formatString'?`
- `name`: use [`rename`](#renaming-rename), which also fixes DAX references (`read-only-property`)
- properties that hold child objects, like `columns` or `measures`: point `set` at the child itself
  (`read-only-property`)
- read-only properties (`read-only-property`):
  - what TOM computes, like a column's `type`
  - properties the model's compatibility level or mode doesn't support (the reason names the level needed)
  - a partition's `source` itself (its properties can be set)
  - passwords, which TOM's JSON masks so a change couldn't be shown
  - a Power Query data source's `credential`, `connectionDetails` and `options`
  - query-group references
- values that don't fit the type, the same path twice, a reference to the wrong kind or to a column of another
  table where the same table is required, or a value TOM rejects (`invalid-property-value`)
- a reference to an object that doesn't exist or is ambiguous (exit code 3, `object-not-found` /
  `ambiguous-object`)

Nothing is written unless every assignment is valid. A value that is valid but breaks the model, such as DAX that
refers to a missing table, is shown along with the errors it introduces, and `--save` refuses it unless `--force`.

A relationship whose `fromColumn` or `toColumn` changes is shown as the same relationship, modified, under its new
endpoints.


## Adding objects (`add`)

`tess add <model> <ref> [options]` creates one object. The [reference](model-files.md#object-references) says what it is,
where it goes and its name. `add` follows the [editing conventions](#editing-commands), so it is a dry run unless
you pass `--save` or `--save-to`. A save is refused if the new object introduces errors, such as a measure whose
DAX names a missing table, unless you pass `--force`.

```sh
tess add ./Sales.pbip measure:Sales/Margin -e "[Revenue] - [Cost]" --format-string "#,0" --save
tess add ./Sales.pbip column:Sales/Profit -e "Sales[Amount] - Sales[Cost]"           # calculated column
tess add ./Sales.pbip column:Sales/Region --data-type string --source-column region   # data column
tess add ./Sales.pbip table:Budget -m "let Source = ... in Source" --mode import
tess add ./Sales.pbip table:Numbers -e "GENERATESERIES(1, 10)"                        # calculated table
tess add ./Sales.pbip "table:Time Intelligence" --calculation-group
tess add ./Sales.pbip "calcitem:Time Intelligence/YTD" -e "CALCULATE(SELECTEDMEASURE(), DATESYTD('Date'[Date]))"
tess add ./Sales.pbip hierarchy:Date/Calendar --levels Year,Quarter,Month
tess add ./Sales.pbip level:Date/Calendar/Day --column Date
tess add ./Sales.pbip relationship --from "'Sales'[DateKey]" --to "'Date'[DateKey]"
tess add ./Sales.pbip role:Denmark --filter "'Customer'=Customer[Country] = \"DK\""
tess add ./Sales.pbip perspective:Finance --include Sales --include "'Date'[Year]"
tess add ./Sales.pbip culture:da-DK
tess add ./Sales.pbip expression:Server -m '"localhost" meta [IsParameterQuery=true, Type="Text", IsParameterQueryRequired=true]'
tess add ./Sales.pbip function:My.Double -e "(x: INT64) => x * 2"
tess add ./Sales.pbip "partition:Sales/Sales 2024" -m "let Source = ... in Source"
cat margin.dax | tess add ./Sales.pbip measure:Sales/Margin -e - --save            # DAX from stdin
tess add ./Sales.pbip calendar:Date/Gregorian --category Year=Year --category "Month=Year Month,Month Name" \
    --category Date=Date --time-related Weekday                                        # calendar (CL 1701+)
```

| Kind | Needs | Optional |
|---|---|---|
| `measure:T/N` | `-e <DAX>` | `--format-string`, `--display-folder`, `--description`, `--hidden` |
| `column:T/N` | `-e <DAX>` (calculated), or `--data-type <type>` (data column) | `--source-column` (data; default: the column's name), `--format-string`, `--display-folder`, `--description`, `--hidden` |
| `table:N` | one of `-e <DAX>` (calculated), `-m <M>` (Power Query) or `--calculation-group` | `--mode` (M), `--precedence` and `--column` (calculation group), `--description`, `--hidden` |
| `hierarchy:T/N` | `--levels <col,col,...>`, top level first | `--display-folder`, `--description`, `--hidden` |
| `level:T/H/N` | | `--column` (default: the column named like the level), `--ordinal`, `--description` |
| `calcitem:T/N` | `-e <DAX>` | `--format-string-expression <DAX>`, `--ordinal`, `--description` |
| `relationship[:N]` | `--from <column> --to <column>` | `--cardinality`, `--cross-filter`, `--inactive` |
| `role:N` | | `--permission`, `--filter 'Table'=<DAX>` (repeatable), `--description` |
| `perspective:N` | | `--include <object>` (repeatable), `--description` |
| `culture:N` | | |
| `expression:N` | `-m <M>` | `--description` |
| `function:N` | `-e <DAX>` | `--description`, `--hidden` |
| `partition:T/N` | `-m <M>` | `--mode`, `--description` |
| `calendar:T/N` | `--category <category>=<column>[,<associated>...]` (repeatable) | `--time-related <column>` (repeatable or comma-separated), `--description` |

An option a kind doesn't take, such as `--levels` on a measure, is a usage error. `-e -` or `-m -` reads the
expression from stdin, without its trailing line break. Only one option per command can read stdin.

Values:
- `--data-type`: `string`, `int64`, `double`, `decimal`, `datetime`, `boolean` or `binary`.
- `--mode`: `import`, `directquery` or `dual`.
- `--cardinality`: `many-to-one`, `one-to-one` or `many-to-many`. `--from` is the many side.
- `--cross-filter`: `single` or `both`.
- `--permission`: `read`, `readrefresh`, `refresh`, `administrator` or `none`.

Wherever `add` takes an object (`--from`, `--to`, `--include`), any form `get` accepts works. `--include` takes
tables, columns, measures and hierarchies, one per option. `--levels` splits on commas, so for a column whose name
contains one, add that level with `add level:T/H/N --column <name>`. DAX that starts with `-` or `--` works as a separate argument
(`-e "-- comment ..."`). The `--expression=<value>` form isn't supported.

What new objects get (Power BI Desktop's defaults, where the spaceparts-analytics sample shows them):
- **Lineage tags:** a fresh `lineageTag` (a lowercase GUID) on every table, column, measure, hierarchy, level,
  named expression and function.
- **Data columns:** `summarizeBy: sum` for `int64`, `double` and `decimal`, otherwise `none`, plus the annotation
  `SummarizationSetBy = Automatic`. The source column defaults to the column's name.
- **Tables:** an M or calculated table gets one partition named after it, with `mode: import` unless you pass
  `--mode`. An M table starts with no columns: add data columns, or let Power BI read them from the query on refresh.
  A calculated table's columns are inferred by the engine.
- **Calculation groups:** tess's own default, the shape Tabular Editor uses: a `Name` column (rename it with
  `--column`) sorted by a hidden `Ordinal` column, and a `calculationGroup` partition with no explicit mode. The precedence is one more than the highest in the model, unless you pass
  `--precedence`. A new group has no calculation items. If the model doesn't have `discourageImplicitMeasures`
  set, `add` sets it, since calculation groups require it, and the change shows on `model`.
- **Levels and calculation items** are added last, unless you pass `--ordinal` (0 is first). The items after the
  new one move down a place.
- **Relationships:**
  - The name is a new GUID, as in Power BI. Write `relationship:<name>` to choose one, or just `relationship` for the
    GUID.
  - The defaults are many-to-one, a single cross-filter direction, and active. One-to-one relationships default to
    `both`, as in Desktop.
  - If an active relationship already joins the same two tables, the new one is made inactive, as Desktop does,
    and `add` warns. Use it with `USERELATIONSHIP`. Other ambiguous paths, through third tables, aren't detected.
  - Columns of different data types are added, with a warning.
- **Roles** have `read` permission unless you pass `--permission`.
- **Partitions** take the mode of the table's existing partitions.
- **Power BI's `PBI_QueryOrder`** model annotation, if present, lists a new M table or named expression at the end.

Names are checked as [`rename`](#renaming-rename) checks them: no empty names, no leading or trailing spaces, no
control characters. Function names must be identifiers. Taken names are refused, case-insensitively: by a column,
measure or hierarchy in the same table, by any measure (for a measure), by any table or calendar (for a table), by
any function (for a function), or by a sibling otherwise. A table and a named expression can't share a name,
because both are Power Query queries. Cultures must be specific culture names, such as `da-DK`, and are written in
their canonical case. The table, hierarchy or column an object goes into must exist. Names match case-insensitively,
and the new reference uses the real name, so `measure:sales/X` adds `measure:Sales/X`. If nothing matches, `add`
suggests close names.

`add` refuses:
- columns in calculated tables, because their columns come from the DAX, and in calculation groups, whose columns
  are fixed
- a second partition for a calculated table or a calculation group, or an M partition in a table whose partitions
  have another source type
- calculation items outside calculation groups, and a level on a column that's already in the hierarchy
- relationships from a table to itself, or over columns another relationship already joins (in either direction)
- calculation groups below compatibility level 1470, calculation item ordinals below 1500, and functions below 1702
- data sources, which `add` doesn't create

**Calendars** (TE3's calendar editor; the metadata [calendar-based time
intelligence](dax-engine.md#calendar-based-time-intelligence) reads) need compatibility level 1701 or higher, as Microsoft Learn
says of TOM's `Calendar`. Each `--category` names a category, its primary column and any associated columns:
"Each category has a primary column and can have zero or more associated columns" (Learn). The categories are
Learn's: `Year`, `Quarter`, `QuarterOfYear`, `Month`, `MonthOfYear`, `MonthOfQuarter`, `Week`, `WeekOfYear`,
`WeekOfQuarter`, `WeekOfMonth`, `Date`, `DayOfYear`, `DayOfQuarter`, `DayOfMonth`, `DayOfWeek` (case, spaces and
hyphens don't matter: `"Month of Year"`). TOM's semester units are refused: no page describes them. `add` refuses
what the engine would: a category given twice, a column in two categories, a column whose category differs from
the one another calendar of the table gives it ("Columns must have a consistent category across calendars"), and a
column that is both in a category and time-related, in this calendar or another. A column's sort-by column counts
as an associated column of the column's category ("The columns specified in the sort-by-column property are
automatically considered as associated columns", SQLBI; Tabular Editor 3's calendar editor does the same), or as
time-related with it, so it can't go anywhere else. A calendar is named like a table:
no table or other calendar may have its name. A calendar without a `Date` category is added with a warning, as the
local engine refuses time intelligence over one. Calendars on calculation groups are refused.

**`--if-not-exists`:** if an object with that reference already exists, in any case, `add` does nothing, notes
it, and exits 0. For a relationship, an existing relationship over the same columns counts too. A different kind
of object holding the name is still a collision.

Not supported: column data types for calculated columns (inferred), KPIs, detail rows, translations (use
[`translations import`](#translations-translations)), role members,
object-level security, and the Power Query parameter metadata of named expressions (write the `meta [...]` record
in the M yourself).

## Removing objects (`remove`)

`tess remove <model> <object>... [--cascade] [--if-exists]` removes one or more objects, as one change, with
everything inside them: a table's columns, measures, hierarchies and partitions, a hierarchy's levels. It follows
the [editing conventions](#editing-commands): a dry run unless `--save` or `--save-to`.

**Dependents first.** If DAX outside what's being removed still refers to it (as `deps --referenced-by` shows), the
removal is refused (exit 3) and the references are listed:

```
tess: Can't remove measure:Sales/Revenue: other objects refer to it:
  measure:Sales/Margin %  expression:5  [Revenue]  -> measure:Sales/Revenue
```

- References from inside the removed set don't count, so a table can go together with the measures that use it.
  Neither does a role's row filter on a removed table: the filter goes with the table.
- **`--cascade`** also removes the dependents, and theirs, as long as they're objects whose definition is their DAX:
  measures, calculated columns, calculated tables, calculation items and functions. A role or a table-level
  expression (detail rows, calculation group defaults) that refers to it is never removed for that; it still blocks.
- **`--force`** removes it anyway and leaves those references broken. Each is reported as a warning, and the
  qualified ones (`'T'[X]`) also as `introducedErrors`. An unqualified `[X]` that names nothing is only a `validate`
  warning, since it may be a column of a table expression.

**Structural fallout.** TOM itself doesn't clean up when an object goes; links to it would dangle and the model
couldn't be saved. tess handles each case, and says what it did on stderr (`notes` in JSON):

| When this goes | What happens |
|---|---|
| a column | relationships that use it are removed (any DAX that relies on a relationship, like `USERELATIONSHIP`, names the column and so blocks); hierarchy levels on it are removed, the other levels renumbered (so they show as modified) and a hierarchy with no levels left is removed; sort-by columns pointing at it are cleared; so are `alternateOf`, group-by columns, calendar associations and a parameter's values column |
| a table | the same for each of its columns; its relationships, perspective entry and roles' permissions on it (row filter and object-level security) go; it's taken out of Power BI's `PBI_QueryOrder` annotation |
| a relationship or hierarchy | variations that use it are removed |
| anything | its perspective memberships, object-level security entries and translations go |

Relationships are removed rather than refused because one can't exist without its columns, and DAX that depends on a
particular relationship refers to its columns, so it is caught by the dependents check.

`remove` refuses (exit 3):
- the model itself
- a table's last partition (remove the table instead)
- a column of a calculated table (it comes from the table's expression) or of a calculation group
- a data source or shared expression a partition still uses
- anything that would still leave TOM with a link to a removed object (a safety net: tess checks TOM's own
  validation after the removal)

**`--if-exists`** skips objects that don't exist, with a note, and exits 0 (so a script can run the same removal
twice). An ambiguous object is still an error.

**Reports.** In a PBIP project, removing a table, column, measure, hierarchy or level that a PBIR report next to the
model uses is refused (exit 3, error code `used-by-report`), and the visuals are listed, one line per report file:

```
tess: Can't remove column:Date/Year: a report uses it:
  level:Date/Calendar/Year <- Sales.Report: page 'Overview', visual 3f2a9c (tableEx)  (1 reference)
Change those visuals first, or use --force to remove it anyway and leave them broken.
```

A use is found exactly as [`rename`](#renaming-rename) finds the references it rewrites (the report's semantic
query expressions, reports whose `definition.pbir` points at this model), and is checked for everything the
removal takes, after `--cascade` and the structural fallout (a removed column takes its hierarchy levels, and a
visual that shows a level counts). Removing a table is checked as the table, so a visual on any of its fields
counts. Report-level measures (`reportExtensions.json`) and a PBIR-Legacy `report.json` can't be read structurally:
there, a file that merely mentions the name counts as a use. JSON lists them in the error's `usages`. `--force`
removes it anyway; `--save-to` (a copy, which the reports don't point at) isn't checked. `bpa --fix` (a `Delete()`
fix) and `tess mcp` (`apply_changes` remove) check the same way.

Not updated: M (Power Query) that mentions a removed table, expression or data source by name (listed as a warning),
the linguistic schema, and the report visuals that `--force` leaves broken.

## Moving measures (`move`)

`tess move <model> <measure> <table> [--display-folder <folder>]` moves a measure to another table. It keeps its name,
lineage tag and every other property, and it follows the [editing conventions](#editing-commands). The change set
shows it as `moved` (`measure:Sales/Revenue -> measure:Measures/Revenue`).

Measure names are unique across the model, so unqualified `[Revenue]` references stay as they are. References that
qualify it with its old table, `'Sales'[Revenue]` and `Sales[Revenue]`, are rewritten to the new table token by
token, quoted as `rename` quotes table names, with every other character untouched. Comments and strings are never
rewritten. As with `rename`, tess applies the move to a copy and refuses (exit 3) if any reference would then mean
something else. The usual case is the measure's own unqualified column reference, such as `SUMX(Sales, [Amount])`,
since `[Amount]` resolves against the measure's own table. Qualify it first (`Sales[Amount]`).

The measure's translations follow it. So do its perspective memberships: if the new table isn't in a perspective the
measure is in, the table is added to it with just that measure, so the measure stays visible (noted on stderr).
`--display-folder` sets the folder in the same change.

`move` refuses (exit 3) a column (its data comes from its table's partitions), a calculation item, a table that
doesn't exist, a calculation group table, and a target table with a column or hierarchy of the same name. Moving to
the table it's already in changes nothing. In a PBIP project, the report's references to the measure follow it (see
[Reports](#reports-pbir)).

## Translations (`translations`)

`tess translations export` writes a file for translators: one row for each object, culture and translatable
property, with the untranslated text next to the translation. `tess translations import` applies the
filled-in file.

```sh
tess translations export ./Sales.pbip                          # CSV on stdout, every culture
tess translations export ./Sales.pbip --culture da-DK -o da-DK.csv
tess translations export ./Sales.pbip --only-translated --json # just what is translated, as JSON
tess translations import ./Sales.pbip da-DK.csv                # preview the changes
tess translations import ./Sales.pbip da-DK.csv --save
tess translations import ./Sales.pbip fr-FR.csv --create-cultures --save
tess translations cultures ./Sales.pbip                        # how much of each culture is translated
```

The CSV file has a header row and RFC 4180 quoting: fields with commas, quotes or line breaks, or with spaces
at either end, are quoted, with quotes doubled. It is UTF-8 with LF line endings. `--bom` adds a byte-order
mark, which Excel needs to read it as UTF-8.

```
object,culture,property,source,translation
measure:Sales/Total,da-DK,Caption,Total,I alt
measure:Sales/Total,da-DK,Description,"Net sales, after returns",
table:Sales,da-DK,Caption,Sales,Salg
```

- `object` is a [reference](model-files.md#object-references). On import, any form `get` accepts works.
- `property` is `Caption`, `Description` or `DisplayFolder`.
- `source` is the untranslated value: the object's name, description or display folder.
- `translation` is empty where there isn't one.

`--format json` (or an `-o` file ending in `.json`, or `--json`) writes the same rows as a JSON array of
`{object, culture, property, source, translation}` objects.

**What's exported:** rows are grouped by culture, in the model's culture order, with objects in model order, so
re-exported files diff cleanly. Captions are always listed. Descriptions and display folders are listed when the
object has one, or a translation of it exists, since there is otherwise nothing to translate.
`--only-translated` lists only rows that have a translation. Objects and properties are the ones TOM keeps
translations for:

| Object | Properties |
|---|---|
| model, table, level, perspective, named expression, function, calendar | Caption, Description |
| column, measure, hierarchy | Caption, Description, DisplayFolder |
| role | Description |

Calculation items are left out: TOM writes their translations to TMDL but can't read them back.

**Import** follows the [editing conventions](#editing-commands): a dry run unless you pass `--save` or
`--save-to`. The change shows on each translated object, e.g. `translations.da-DK.caption: (none) -> "I alt"`.
- Each row sets its translation. An empty translation removes it.
- Rows the file doesn't have are left alone, so a file with only some rows or cultures is fine. `--culture` imports
  only those cultures' rows.
- The format comes from `--format`, else the file's extension, else its content (`[` means JSON). `-` reads
  stdin. A byte-order mark is skipped. Header columns can be in any order and any case; `source` is optional, and
  extra columns are ignored.
- Cultures match case-insensitively. A culture the model doesn't have is refused unless you pass
  `--create-cultures`, which adds it (it must be a specific culture name such as `fr-FR`).
- If a row's `source` no longer matches the model, for example because a description changed after the export,
  the row is imported with a warning that lists the lines.

Import refuses the whole file (exit 3), naming the file and line, for:
- an object that doesn't exist, or is ambiguous
- a property the object can't have translated, such as a table's `DisplayFolder`. TOM accepts it, but drops it
  on save.
- an unknown property or culture
- two rows that give the same property different translations
- a malformed file: a missing header column, a row with too many or too few fields, or an unclosed quote

The line is where the row starts, counting line breaks inside quoted fields. In JSON files it's the item number.
`export` writes a file the translator fills in: exporting, then importing that file unchanged, changes nothing.

`get -p translations.da-DK.caption` shows one translation, and `find` searches translations. `set` doesn't
write them and points at `translations import`. `tess translations cultures` prints each culture's translated
and total rows per property; `--json` writes `[{ref, name, translated, total, properties: {caption: {translated,
total}, ...}}]`.

## Perspectives (`perspective`)

`tess perspective add|remove <model> <perspective> <object>...` changes which tables, columns, measures and
hierarchies a perspective shows. It follows the [editing conventions](#editing-commands). `tess perspective list`
shows membership.

```sh
tess perspective add    ./Sales.pbip Finance Sales "'Date'[Year]" "[Budget]" --save
tess perspective remove ./Sales.pbip Finance "Sales[Internal Key]" --save
tess perspective list   ./Sales.pbip                # each perspective, with member counts
tess perspective list   ./Sales.pbip Finance        # its members, one reference per line (--json too)
```

The perspective is a name, matched case-insensitively, or a `perspective:<name>` reference. The objects can be in
any form `get` accepts. In TOM a perspective lists its tables, and each table lists the members it shows, so:
- **Adding a table** adds it with all its columns, measures and hierarchies, like `add perspective:X --include`.
  Adding a table that's partly in adds the rest.
- **Adding a column, measure or hierarchy** adds its table too, with just that member.
- **Removing a table** removes it with all its members.
- **Removing a table's last member** removes the table too, since a table with no members shows nothing, and says so
  on stderr.
- What's already in, or not in, the perspective is noted on stderr. It isn't an error.

`perspective` refuses (exit 3) a perspective that doesn't exist (create it with `tess add <model> perspective:<name>`),
and levels, calculation items and other objects perspectives don't hold. The change shows as the perspective's
`members`, line by line. `list --json` writes `[{ref, name, tables, columns, measures, hierarchies}]`, or
`{perspective, members: [{ref, kind}]}` for one perspective.

## Interactive sessions (`interactive`)

`tess interactive <model>` (or `tess shell <model>`) opens a shell over one model, held in memory. Every tess
command runs inside it without the model argument, which the session puts back where the command takes it (after
its subcommand words: `bpa rules list`, `translations cultures`):

```
$ tess interactive ./Sales.pbip
tess Sales> get [Total] -p expression
SUM(Sales[Amount])
tess Sales> rename [Total] Revenue
tess Sales*> set [Revenue] -p formatString=#,0.00
tess Sales*> query --data ./fixture "EVALUATE ROW(""r"", [Revenue])"
tess Sales*> status
tess Sales*> save
```

**Edits stage.** An edit of the session's model (`set`, `rename`, `add`, `remove`, `move`, `script apply`,
`format`, `lint --fix`, `bpa --fix`, `translations import`, `perspective add|remove`, `bpa rules ...`) changes the
model in memory and writes nothing; the prompt shows `*` while edits are staged. Every later command sees them:
`get`, `validate`, `deps`, `query --data` and `test run --data` evaluate the staged model, `convert <path>` and
`--save-to <path>` write it elsewhere, `deploy` deploys it. Report files that a staged `rename` or `move` rewrites
are staged too, so later renames and the [removal check](#removing-objects-remove) see them. Per command,
`--dry-run` (or `--revert`) shows the edit without staging it, and `--save` stages it and saves the session.

| Session command | What it does |
|---|---|
| `status` (`pwd`) | the model, the staged changes (one line per object), staged report files, new errors (`--json`) |
| `diff` | the staged changes against the model as loaded or saved (`--stat`, `--name-only`, `--json`); `diff <other>` compares the staged model with another |
| `save [--force]` | write every staged edit back in place, then the staged report files; refused while the staged edits introduced errors (listed), unless `--force` |
| `revert` | discard every staged edit |
| `undo` | take back the last staged edit (one command's); `save` and `revert` clear the undo stack |
| `history`, `clear` (`cls`), `help` (`?`) | |
| `exit` (`quit`, `q`) `[--force]` | leave; with staged edits, asks first (no by default); `--force` discards them |

The model staged in memory is the one `tess mcp` serves: one implementation of staged edits, the save gate and
staged report files (`src/Tessera.Cli/Staging.fs`).

**Lines.** Words are split as `te interactive` splits them: `"..."` and `'...'` group words (the quotes are dropped,
a doubled quote is a literal one: `"EVALUATE ROW(""x"", 1)"`), and `[...]` stays whole with its brackets, so
`[Total Sales]` and `'Internet Sales'[Amount]` reach the command as one argument (a `'...'` right before `[` keeps
its quotes: it is a DAX table name). An unterminated group is an error. Backslashes are literal.

**At a terminal** the line is edited in place (Left/Right, Home/End or Ctrl+A/Ctrl+E, Backspace/Delete, Ctrl+U,
Ctrl+K, Up/Down through the history; a line longer than the terminal scrolls sideways); Ctrl+C abandons the line; Ctrl+D on an empty line ends the session (Ctrl+Z
then Enter on Windows). The history is kept in memory; `--history <file>` keeps it in a file too (read at the start,
appended to). There is no tab completion inside the session. A running command can't be interrupted: Ctrl+C is
ignored until it finishes. `--no-banner` drops the welcome lines.

**Piped input** runs line by line (also `--batch`): blank lines and `#` comments are skipped, `--echo` writes each
command (after the prompt) before its output, and the first command that fails ends the run with its exit code
(`diff` finding differences is not a failure). `--no-batch` runs every line and exits 0. Staged edits are never
dropped silently: input that ends (or `exit`s) with unsaved edits fails with exit 1 (`unsaved-changes`) and lists
them; end the script with `save`, or `exit --force` to discard them.

```sh
printf 'rename [Total] Revenue\nvalidate --fail-on error\nsave\n' | tess interactive ./Sales.pbip --echo
```

`--json` (on `tess interactive`) adds `--json` to every command, and the session's own commands and errors answer
in JSON too (the unsaved-edits failure is `{"error": {"code": "unsaved-changes", ..., "changes": [...]}}`).

Refused inside a session: `interactive`/`shell` and `mcp` (they would take over the input), `--watch`, and `-` as
a value (read from stdin: the session reads its commands from stdin; `-p expression=-` counts too). A server model
(`xmla:...`) can be explored and edited, but `save` is refused: `convert` or `--save-to` writes the staged model as
files, and `tess deploy` deploys them. Without a model, the session starts on the active connection when it is a
local model; a server is contacted only when named (`tess interactive active:`).
