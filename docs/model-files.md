# Model files and object references

## Accepted model paths

You can point any command at any of these:

- a `.bim` file
- a TMDL folder, or any `.tmdl` file inside one
- a Tabular Editor "save to folder" folder (holding `database.json`), or its `database.json`
- a `X.SemanticModel` folder, or its `definition.pbism`
- a `.pbip` file, followed through `X.Report/definition.pbir` to its local model
- a `X.Report` folder
- a project folder containing exactly one of the above

## Output formats (`convert`)

Unless you pass `--format`, the format is inferred from the destination:

| Destination | Format |
|---|---|
| `*.bim` | `bim` |
| `*.SemanticModel` | `pbip`: TMDL in `definition/`, plus `definition.pbism` and `.platform` |
| `.../database.json`, or a folder that holds one | `database.json`: Tabular Editor's folder format |
| an existing folder with `definition.pbism` | `pbip` |
| anything else | `tmdl` folder |

`--serialization` is another name for `--format` (as in TE's CLI). Its values are a fixed list:
`bim`, `tmdl`, `pbip` and `database.json`.

tess tells a `.bim` file from other files by its extension, so `--format bim` adds `.bim` to a destination that
doesn't end in it (`--format bim ./out` writes `./out.bim`; the output line and `--json` give the real path), and
refuses a destination that is a folder (exit 2). The folder formats write wherever they're told. `init` and
`data embed` do the same.

The defaults match Power BI Desktop's output:

- tabs for indentation
- UTF-8 without BOM
- expressions written as-is
- restricted information (e.g. passwords) stripped

Line endings default to `--eol auto`: tess matches the files already at the destination, so overwrites
don't rewrite every line in git. With nothing there, it matches the source, and falls back to LF.
Use `--eol lf|crlf`, `--indent-spaces N` or `--include-sensitive` to change these. `convert` won't write over a
non-empty destination without `--force`. With `--force`, stale `.tmdl` files are removed and unrelated files are
left alone. Re-saving a PBIP folder keeps its `.platform` `logicalId` and its `definition.pbism` settings.

### Writing PBIP

A PBIP save merges into what's there and never clobbers it:

- `definition/` gets the model's TMDL. Stale `.tmdl` files go, and files whose bytes haven't changed aren't
  rewritten.
- An existing `.platform` is kept as it is, including a Desktop-authored `displayName` and `description`.
  An existing `definition.pbism` is kept too. The one exception: a pbism version older than 4.0 is raised to
  4.2, because Power BI can't open TMDL under an older one. Other files (`diagramLayout.json`, `.pbi/`) are
  never touched.
- A missing `.platform` or `definition.pbism` is created. A new `.platform` has `displayName`, a new
  `logicalId` and, when you convert from another PBIP, that item's `description`.
- A `model.bim` next to the new `definition/` is removed, because Power BI refuses a folder that has both.
- If the destination is a project folder (it holds a `.pbip` file or `X.SemanticModel` folders), the model
  goes into its one `X.SemanticModel`. If there is none, a new folder is named after the item: the source
  PBIP's `displayName`, else the database's or model's name. That new folder isn't the destination itself,
  so no `--force` is needed. Any other destination is written as given. The `displayName` is the folder's
  name without `.SemanticModel`.

### Deterministic output

`convert` writes the same bytes on Linux, macOS and Windows for every format:
- Line endings follow `--eol` and never the OS.
- Collections keep the model's order, and file listings are sorted ordinally.
- Every file name is portable:
  - characters Windows refuses become `%xx` (a fixed set, not the running OS's);
  - Windows device names (`CON`, `nul.txt`) get a `_` prefix;
  - folder names don't end in a dot or a space;
  - names that would collide on a case- or normalization-insensitive file system get `~2`, `~3`, ...
    The first keeps its name.

For TMDL, tess keeps TOM's own file names and only renames the files that wouldn't be portable. TMDL readers
take object names from file contents, never from file names. Golden tests in `tests/Tessera.Tests/golden`
pin the output byte for byte. A new `.platform`'s `logicalId` is random, as it has to be.

## Tabular Editor folders (`database.json`)

This is Tabular Editor's "save to folder" format, which TE2, TE3 and pbi-tools use. It is TOM's JSON split
into a file per object:

```
database.json                        the database, with what isn't split out
dataSources/  expressions/  functions/  relationships/  perspectives/  cultures/  roles/   <name>.json
tables/<table>/<table>.json
tables/<table>/{columns,measures,hierarchies,partitions,calculationItems,annotations}/<name>.json
```

```sh
tess convert ./model.bim ./model-folder --serialization database.json
tess convert ./model-folder ./Sales.SemanticModel        # ... and on to PBIP
tess set ./model-folder "[Revenue]" -p formatString='#,0' --save   # saves back as database.json
```

The model's `TabularEditor_SerializeOptions` annotation sets the layout, as it does for TE, so a folder
keeps its layout whoever saves it:
- `Levels` decides which collections get their own files. That includes TE3's per-file UDFs (`Functions`)
  and calculation items (`Tables/Calculation Items`); the old spelling `Tables/CalculationItems` is read
  too. A level tess doesn't know leaves its objects inline.
- `PrefixFilenames` adds `000 ` prefixes to keep the model's order.
- `LocalTranslations`, `LocalPerspectives` and `LocalRelationships` store translations, perspective
  membership and relationships as TE's annotations on the objects themselves.
- `IgnoreInferredObjects`, `IgnoreInferredProperties`, `IgnoreTimestamps`, `SplitMultilineStrings`,
  `IgnoreLineageTags`, `IgnorePrivacySettings` and `SortArrays` behave as in TE.

A model without the annotation gets TE's default levels (every collection in its own file), and the
annotation is written into `database.json`, as TE does. Reading resolves TE's local annotations back into
translations, perspectives and relationships, then removes them, as TE does. A folder TE2 wrote with all
three `Local*` options loads and saves back byte for byte.

Where tess departs from TE2:
- **File names** are portable on every OS (see above). TE2 escapes only what the running OS refuses.
- **Reading order**: names with a number prefix (`000 `, `1000 `) are sorted by that number, the rest
  ordinally. TE2 uses the current culture's order (so `1000 x` comes before `101 x`) and, for table
  folders, the file system's. Without `PrefixFilenames`, split collections come back in file-name order;
  this format stores no order. The model is otherwise the same.
- **Relationship names** are kept under `LocalRelationships`. TE2 drops them all, so Power BI Desktop's
  GUID names became "Relationship 1", "Relationship 2", ... Names of that generated form are still dropped.
- **Linguistic metadata** survives `LocalTranslations`: a culture that has any keeps its file, without the
  translations. TE2 drops such cultures.
- **Refusals**: tess refuses to save, with an error, rather than lose data:
  - `IgnoreIncrementalRefreshPartitions` when the model has incremental refresh partitions (TE2 deletes them);
  - `LocalPerspectives` when a perspective has extended properties, or annotations on its members, which
    TE's annotation can't hold;
  - `LocalTranslations` with translations of objects TE can't annotate;
  - an annotation that isn't valid JSON.
- **Ignored keys**: `IncludeSensitive` (`--include-sensitive` decides), `AlsoSaveAsBim` (a TE UI option)
  and `TmdlOptions` (TMDL only).

A save into an existing folder removes stale `.json` files only under the format's own folders, matched
by exact case, plus folders that end up empty. `README.md` and anything else of yours stays. Unchanged
files aren't rewritten.

## New models (`init`)

`init` writes an empty model. It picks the format from the path the way `convert` does, and writes the same
support files. The model is named after the folder or file, without `.SemanticModel` or `.bim`.

```sh
tess init ./Sales.SemanticModel                                 # PBIP: definition/ + definition.pbism + .platform
tess init ./finance.bim --name Finance --culture da-DK
tess init ./model --compatibility-level 1601 --force            # TMDL folder; replace what's there
```

The model gets the properties Power BI Desktop puts on a new model:
- `culture` and `sourceQueryCulture` (`--culture`, default `en-US`)
- `defaultPowerBIDataSourceVersion: powerBI_V3`
- `dataAccessOptions` with `legacyRedirects` and `returnErrorValuesAsNull`

It also sets `discourageImplicitMeasures`, because models built in code should use explicit measures, and
calculation groups require it anyway. The database is left unnamed, as Desktop leaves it. Desktop's UI-state
annotations, such as `PBI_QueryOrder`, are not written.

The default compatibility level is 1702:
- current Power BI tooling writes it (the spaceparts-analytics sample uses it)
- it is the first level with DAX user-defined functions
- Power BI Desktop raises a model's level when a feature needs a higher one, but never lowers it

Pass `--compatibility-level` for older engines. The minimum is 1520, the lowest level that supports
`sourceQueryCulture`. `init` won't write over a non-empty destination without `--force` (exit 4).

Re-running `init` is safe: if the destination already holds the model `init` would create (the same name,
compatibility level, culture and properties, nothing else in it, and the format it would write), it writes nothing
and exits 0, saying so. With `--json`: `{"created": false, "reason", "model", "output": {"format", "path"}}`. Any
other model there is still refused.

To open the model in Power BI Desktop, you also need a `.pbip` file and a `X.Report` folder that points at it.

## Object references

Every object has a stable address of the form `kind:segment/segment`. Within a name, `/` and `\` are escaped
with `\`.

```
model
table:Sales
column:Sales/Amount            measure:Sales/Total          hierarchy:Date/Calendar
level:Date/Calendar/Year       partition:Sales/Sales        calcitem:Time Intelligence/YTD
calendar:Date/Fiscal           relationship:<name>          role:Readers
perspective:Sales View         culture:da-DK                expression:ServerName
function:DaxLib.Format         datasource:SQL
```

## Pointing at objects

Commands that take an object accept any of these forms:

| Form | Example | Resolves to |
|---|---|---|
| reference | `measure:Sales/Total` | exactly that object |
| DAX, qualified | `'Sales'[Total]`, `Sales[Total]` | the column, measure or hierarchy with that name in that table |
| DAX, unqualified | `[Total]` | the measure with that name; failing that, a uniquely named column or hierarchy |
| bare name | `Sales`, `Readers` | the table with that name; failing that, any uniquely named object |

Names match exact-case first, then case-insensitively, since Tabular treats names case-insensitively. Inside
DAX quoting, `''` escapes a quote and `]]` escapes a bracket. If more than one object matches, tess lists the
candidates.

## Property paths

`get -p` takes paths into an object's TOM JSON:
- nested properties are joined with dots, e.g. `source.expression`
- annotations and other named items use their name, e.g. `annotations.PBI_FormatHint`
- other arrays use an index, e.g. `[0]`

A path that names a group, such as `-p source`, prints everything under it. Restricted information like
passwords is never printed.

An object's own sub-objects are part of it: `kpi.targetExpression`, `formatStringDefinition.expression`,
`detailRowsDefinition.expression`, `calculationGroup.precedence`, `alternateOf.summarization` and
`extendedProperties.<name>` are properties of the measure, table or column, in `get -p`, `find` and `diff` alike.
Child objects that have their own reference (columns, measures, calculation items, ...) and a role's members are
not.

`get --properties` lists the properties [`set`](editing.md#setting-properties-set) can write on an object: the path, its
type, its current value, and the allowed values of enums. `--all` adds read-only properties, with the reason.

```
$ tess get ./Sales.pbip "Sales[Amount]" --properties
alternateOf                     object
alternateOf.baseColumn          reference                -> column
annotations.<name>              string
annotations.SummarizationSetBy  string     Automatic
dataType                        enum       decimal       string|int64|double|dateTime|decimal|boolean|binary|unknown|variant
isHidden                        bool       false
sortByColumn                    reference                -> column
summarizeBy                     enum       sum           default|none|sum|min|max|count|average|distinctCount
...
```

`--json` writes an array with one entry per property, sorted by path:

```json
[ { "property": "summarizeBy", "type": "enum", "writable": true, "value": "sum",
    "allowed": [ "default", "none", "sum", "min", "max", "count", "average", "distinctCount" ] },
  { "property": "sortByColumn", "type": "reference", "writable": true, "value": null, "refersTo": "column" },
  { "property": "name", "type": "string", "writable": false, "value": "Amount",
    "reason": "use `tess rename`, which also updates the DAX that refers to it" } ]
```

Each entry has:
- `type`: `string`, `expression` (DAX, M or a query), `bool`, `int`, `number`, `enum`, `reference` or
  `object` (a sub-object).
- `value`: a JSON string, boolean or number, a reference such as `"column:Date/MonthNo"`, `{}` for a sub-object
  that exists, or `null` when unset.
- optional keys: `allowed` (enums only), `refersTo` (references only: an object kind) and `reason` (read-only
  properties only).

`annotations.<name>` and `extendedProperties.<name>` stand for new items of any name. Existing ones are listed
by name. Text output shows the first line of long values; `--json` has them in full.

Both `get --properties` and `set` read one property catalog (`Tessera.PropertyCatalog`), built once per TOM type
by reflection and cached: each path's type, whether `set` writes it at the model's compatibility level and mode,
and the enum members that level allows. An object's list is that table plus its current values (and the
annotations it has). A value outside an enum's members is refused with the members and the closest one:
`expected one of: default, none, sum, ... (did you mean 'sum'?)`.

### Filtering objects (`get --where`)

`tess get <model> <container> --where <path>=<pattern>` lists the objects inside a container whose property
matches a wildcard: `*` is any run of characters (line breaks included), `?` one character, and the rest is
literal. Matching is case-insensitive and covers the whole value. `<path>!=<pattern>` keeps the objects that
don't match. `--where` is repeatable, and every condition must hold.

```sh
tess get ./Sales.pbip model --where objectKind=measure --where 'expression=*CALCULATE*'
tess get ./Sales.pbip Sales --where isHidden=true
tess get ./Sales.pbip model --where objectKind=measure --where description= -p expression   # undocumented measures
tess get ./Sales.pbip model --where annotations.Owner!=finance --where objectKind=table
```

- **Container:** the objects whose TOM parent chain reaches it. For the model that is every object; for a table
  its columns, measures, hierarchies, levels, partitions, calculation items and calendars; for a hierarchy its
  levels. The container itself is not listed.
- **Paths** are the ones `get --properties` and `get -p` use, matched case-insensitively. `objectKind` is the object's
  kind (`measure`, `column`, ...).
- **Values** are read as `get --properties` shows them, so TOM's defaults have values: `isHidden=false` finds the
  visible objects even though TOM's JSON leaves `isHidden` out. Values that are not in the catalog, such as
  translations, come from the paths `get -p` prints. A property that holds nothing (an unset description, an
  annotation the object doesn't have) reads as empty, so `description=` finds objects without one.
- **Objects without the property** (a relationship has no `isHidden`) match neither `=` nor `!=`.
- **Output** is the same as `list`'s, text or `--json`. With `-p <path>`, tess prints those properties of each
  match instead (`ref  path: value`, the first line of long values; `--json`: `[{ref, kind, properties}]`).
- Finding nothing is not an error: no output, exit code 0. A path that no object inside the container has is
  `unknown-property` (exit code 2) with close matches. `--where` can't be combined with `--properties` or
  `--children`.

`find --in` takes path wildcards. A wildcard matches if it matches the whole path or just the last segment, so
`--in expression` covers both `expression` and `source.expression`. `find` searches every property except
lineage tags, and prints `ref  property[:line]  text`, one hit per matching line.
