# Best Practice Analyzer (`bpa`)

`bpa` checks a model against Best Practice Analyzer rules in Tabular Editor's JSON format. Existing TE2/TE3
rule files run unchanged as long as they only use the members listed below (see Compatibility):

```
tess bpa ./Sales.pbip                              # built-in rules, plus rules stored in the model
tess bpa ./Sales.pbip -r BPARules.json -r team.json --no-default-rules
tess bpa ./Sales.pbip --category DAX,Performance --min-severity 2
tess bpa ./Sales.pbip --list-rules                 # what would run, and whether each rule is supported
tess bpa ./Sales.pbip --path 'measure:Sales/*'     # only findings on matching objects
tess bpa ./Sales.pbip --json                       # the findings envelope, plus ignored findings and rule errors
tess bpa ./Sales.pbip --trx bpa.trx                # VSTest results, one test per rule (Azure DevOps etc.)
tess bpa ./Sales.pbip --fix --save                 # apply the rules' fix expressions
```

Text output is one finding per line, most severe first, then a summary:

```
error    relationship:Sales-Product  TESS_RELATIONSHIP_DATA_TYPES  Relate columns of the same data type
warning  measure:Sales/Margin %      TESS_DIVIDE  Use DIVIDE when the divisor can be zero
info     measure:Sales/Total         TESS_MEASURE_DESCRIPTION  Describe visible measures
3 findings (1 error, 1 warning, 1 info); 25 rules checked
```

Severity 1 is info, 2 a warning and 3 (or more) an error. `bpa` exits 1 when a finding is at least as severe
as `--fail-on` (`error`, `warning` or `info`; default `error`). `--min-severity` and `--category`
(case-insensitive, repeatable) choose which rules run. Rules that can't run are listed on stderr (in JSON, under
`ruleErrors`). They don't change the exit code. `--json` writes the
[findings envelope](ci.md#findings-json-and-ci-annotations) with these extra keys:
- `model`, `rulesEvaluated`, `violations`
- `ignored`: findings suppressed by annotations
- `ruleErrors`: `{ruleId, ruleName, stage, error}`
- `skippedRules`, `warnings`
- `rules`: counts

`--ci github|azdo` writes CI annotations to stderr. In JSON, `warnings` lists rule files the model refers to that
couldn't be read, `notes` things deliberately not done (URLs not fetched), and `fetched` the URLs requested.

`--path <pattern>` (repeatable or comma-separated) reports only findings on objects whose reference matches: a glob
over the whole reference (`measure:Sales/*`, `*:Sales/*` for everything in a table), or else a prefix (`column:`,
`measure:Sales/`). Case-insensitive. Every rule still runs; the exit code, the summary, `--trx` and `--ci` only
count the matching findings.

## Which rules run

Rules come from five layers. They merge by rule ID (case-insensitive): every rule from every layer runs, and where
two layers define the same ID, the higher layer's definition wins. Highest first:

1. **`--rules` files**, in order (a later file wins).
2. **`TESS_BPA_RULES`**: a list of rule files separated like `PATH` (`:` on Linux and macOS, `;` on Windows),
   relative to the current directory; a later file wins.
3. **Config files**, each a TE rule file:
   - the project's `.tessera/bpa.json`, the nearest one found walking up from the model's folder to the root of
     the git repository it's in (the first folder holding `.git`). Outside a repository, only the model's own
     folders count: the `.bim` file's folder or the TMDL folder, and for PBIP the `X.SemanticModel` folder and the
     project folder holding it.
   - the user's `bpa.json`: `$XDG_CONFIG_HOME/tessera/bpa.json`, or `~/.config/tessera/bpa.json`, on Linux and
     macOS; `%APPDATA%\tessera\bpa.json` on Windows. The project's file wins over the user's.
4. **The model's own rules:** rules stored in its `BestPracticeAnalyzer` annotation, over the files listed in its
   `BestPracticeAnalyzer_ExternalRuleFiles` annotation (a JSON array of paths, relative to the `.bim` file's
   folder or the TMDL folder). Among those files the first listed wins, as in TE.
5. **tessera's built-in rules.**

A file you name (layers 1 to 3) must exist and be valid, or `bpa` stops (exit 3). A file the model lists that is
missing or invalid is a warning, so a model can always be analyzed.

Switches: `--no-builtin` (or `--no-default-rules`) drops layer 5, `--no-model-rules` layer 4 (like TE2's `-AX`),
and `--no-config` layers 2 and 3. `--builtin-only` runs only layer 5 and can't be combined with `--rules` or
`--no-builtin`. `tess bpa rules list` shows which layer each rule in effect comes from.

How this differs from TE: TE2 merges the same kinds of sources by ID, but gives the model's rules the last word
and rules passed on its command line the least. tess turns that around, so whoever runs the analysis (a CI job, a
team's config) decides what a rule means, not the model being checked. To keep a model's own version of a rule,
give it its own ID.

## External rule URLs

tess makes no network requests unless asked to. A `BestPracticeAnalyzer_ExternalRuleFiles` entry that is a URL is
skipped with a note naming it, unless you pass `--allow-external-rules`. With it:
- only `https://` URLs are fetched, and not ones with a user name or password in them;
- the request is a plain GET with no cookies, credentials or anything about the model;
- it times out after 15 seconds, and a file over 4 MB is refused; redirects must stay on `https://`;
- each URL requested is named on stderr (also with `--json`), and a failed fetch is a warning.

Fetched rules belong to the model's layer, in the position the URL has in the list.

## Rule format

A rule file is a JSON array of rules:

```json
[
  {
    "ID": "MY_RULE",
    "Name": "Hide foreign keys",
    "Category": "Model layout",
    "Description": "Shown in the docs and tooltips.",
    "Severity": 2,
    "Scope": "DataColumn, CalculatedColumn",
    "Expression": "IsVisible and UsedInRelationships.Any(FromColumn == outerIt)",
    "CompatibilityLevel": 1200
  }
]
```

Only `ID`, `Scope` and `Expression` are required. As in TE, property names are case-insensitive, and comments,
trailing commas and extra fields such as `Source` are accepted. `Name` defaults to the ID, and `Severity`
defaults to 1. `LearnMoreUrl` is read. `FixExpression` is applied by [`--fix`](#fixing-findings---fix). A rule whose
`CompatibilityLevel` is above the model's is skipped.

`Scope` is a comma-separated list. It can also be TE's flags number.

| Scope | Objects |
|---|---|
| `Model` | the model |
| `Table` | tables that are neither calculated tables nor calculation groups |
| `CalculatedTable`, `CalculationGroup` | calculated tables; calculation group tables |
| `DataColumn`, `CalculatedColumn`, `CalculatedTableColumn` | columns by kind (`Column` means all three) |
| `Measure`, `KPI` | measures; measures' KPIs |
| `Hierarchy`, `Level`, `Partition`, `Calendar` | per table |
| `Relationship` | single-column relationships |
| `Perspective`, `Culture`, `NamedExpression`, `UserDefinedFunction` (or `Function`) | model-level objects |
| `ModelRole`, `TablePermission`, `ModelRoleMember` | roles, their row filters, their members |
| `ProviderDataSource` (or `DataSource`), `StructuredDataSource` | legacy and Power Query data sources |
| `Variation`, `CalculationItem` | column variations; calculation items |

Plural forms and spaces are accepted (`Calculated Columns`, `Hierarchies`). A rule naming a scope tess doesn't
know is reported as unsupported.

## Expressions

`Expression` is a Dynamic LINQ predicate, evaluated once for each object in scope. tess evaluates it with
[System.Linq.Dynamic.Core](https://github.com/zzzprojects/System.Linq.Dynamic.Core) (Apache-2.0). The objects
it runs on mirror Tabular Editor's TOMWrapper, so rules use the same names. Examples:

- every object: `Name`, `Description`, `ObjectType`, `ObjectTypeName`, `Model`, `HasAnnotation(name)`,
  `GetAnnotation(name)`, `GetAnnotations()`, `HasExtendedProperty(name)`, `GetExtendedProperty(name)`,
  `TranslatedNames[culture]`, `TranslatedDescriptions[culture]`, `DependsOn`, `ReferencedBy`, `Tokenize()`
- tables, columns, measures, hierarchies: `IsHidden`, `IsVisible` (a column in a hidden table isn't
  visible), `DisplayFolder`, `TranslatedDisplayFolders`, `InPerspective[perspective]`, `DaxObjectName`,
  `DaxObjectFullName`
- columns: `Table`, `DataType`, `FormatString`, `SummarizeBy`, `SortByColumn`, `IsKey`, `IsAvailableInMDX`,
  `DataCategory`, `SourceColumn`, `Expression`, `Type`, `UsedInRelationships`, `UsedInHierarchies`,
  `UsedInLevels`, `UsedInSortBy`, `UsedInVariations`, `ObjectLevelSecurity[role]`, ...
- measures: `Expression`, `FormatString`, `FormatStringExpression`, `DetailRowsExpression`, `KPI`, ...
- tables: `Columns`, `Measures`, `Hierarchies`, `Partitions`, `RowLevelSecurity[role]`, `SourceExpression`, ...
- the model: `Tables`, `AllColumns`, `AllMeasures`, `AllHierarchies`, `AllLevels`, `AllPartitions`,
  `AllCalculationItems`, `Relationships`, `Perspectives`, `Cultures`, `Roles`, `Expressions`, `DataSources`,
  `Functions`, `Database.CompatibilityLevel`, ...
- relationships: `FromTable`, `FromColumn`, `ToTable`, `ToColumn`, `FromCardinality`, `ToCardinality`,
  `CrossFilteringBehavior`, `IsActive`, ...

Enum properties compare with strings or enum members (`DataType = "Double"`, `DataType == DataType.Double`).
Collections support `Count`, `[0]`, `["name"]` and LINQ (`Any`, `All`, `Count(...)`, `Where`, ...).

TE's own additions to the expression language also work:
- `outerIt` is the enclosing object, and `current` is the object being checked.
- Type names are case-insensitive (`RegEx.IsMatch`).
- `""` escapes a quote inside a string, and backslashes are literal (`"\s*"`).
- In `Tokenize()` rules, TE's DAX token names (`DIV`, `INTEGER_LITERAL`, `IFERROR`, ...) stand for their codes.

Member access is null-safe, as in TE: a property of a null object is the property type's default, and a null
string behaves as `""`.

`DependsOn` and `ReferencedBy` come from the same analysis as `deps`, with two differences:
- They only hold DAX references. Sort-by, hierarchy and relationship links have their own `UsedIn...`
  properties.
- In DAX a table owns (a calculated table's expression, detail rows, calculation group selection
  expressions), an unqualified `[X]` never refers to the table's own columns, as in TE.

`ReferencedBy` has typed views (`Measures`, `Columns`, `Tables`, `Roles`, and `AllMeasures` and friends for
indirect references), plus `Deep()` and `AnyVisible`. `DependsOn` is a dictionary from object to references.
Each reference has `FullyQualified`, `Property` and `Line`. A KPI's expressions belong to the KPI, and a row
filter belongs to its table permission.

If an expression doesn't compile for one of its scopes, the rule is reported as unsupported, with the parser's
error. A runtime error fails just that rule. Neither stops the run.

## Fixing findings (`--fix`)

```
tess bpa ./Sales.pbip --fix                        # show what the fix expressions would change (a dry run)
tess bpa ./Sales.pbip -r BPARules.json --fix --save
```

`--fix` applies each finding's rule's `FixExpression` to the object it was found on. It is an
[editing command](editing.md#editing-commands): a dry run unless `--save` or `--save-to`, with `--stat`, `--name-only`,
`--force`, `--json` (the change set) and the save gate as for the others. It fixes the findings the rule switches,
`--category`, `--min-severity` and `--path` select; findings ignored by annotations are left alone. `--fail-on`,
`--ci`, `--trx`, `--sarif` and `--list-rules` report findings and are refused with `--fix` (exit 2), as are the
editing switches without it.

A fix expression is a C# statement about the object: Tabular Editor 2 runs `<object>.` + the expression + `;` as a
script (its BPA window, `GetFixScript`). tess doesn't run C#. It applies the statements the published rule files
use, with the side effects TE's object model gives them, and refuses everything else:

| Fix expression | What tess does |
|---|---|
| `IsHidden = true`, `IsAvailableInMDX = false`, `IsKey = true` | sets the property (`isHidden`, `isAvailableInMdx`, `isKey`). `IsKey = true` also clears `IsKey` on the table's other columns |
| `DataType = DataType.Decimal`, `SummarizeBy = AggregateFunction.None` | sets the enum property; the type must be the property's (`DataType`, `AggregateFunction`) |
| `FormatString = "#,0"`, `Description = "..."`, `DisplayFolder = "..."` | sets the string (C#'s escapes apply: `"\u003B"` is `;`; `null` clears). A changed format string removes the object's `Format` annotation, as TE does |
| `Delete()` | removes the object as `tess remove` does, with the relationships, levels, sort-by links, perspectives, security and translations that point at it. Unlike TE, it is refused while other DAX still refers to the object, instead of leaving that DAX broken, and (unless `--force`) while a PBIR report next to the model uses it ([reports](editing.md#removing-objects-remove)) |
| `TranslatedDisplayFolders.Reset()` | sets every culture's display folder translation to the object's own display folder |

Refused, with a warning naming the rule, the objects and why: anything else (`Name = ...`: use `tess rename`; values
computed from the object, such as `string.Concat(it.Name...)`; other methods; other properties), an expression
containing `{`, `}` or `;` (TE skips those too), a property the object doesn't have or can't set at its
compatibility level, a value it doesn't take, a finding on an object tess can't edit by itself (a KPI, a table
permission, a role member, a variation), and two rules setting one property of one object to different values
(neither is applied; the same value is applied once). Findings of rules without a fix expression are counted in a
warning.

One run applies the fixes of one analysis. Fixes can make other rules fire (removing a column can leave another
unused), and rules from different files can disagree (BPARules-standard's `DISABLE_ATTRIBUTE_HIERACHIES` and
Microsoft's `SET_ISAVAILABLEINMDX_TO_TRUE_ON_NECESSARY_COLUMNS`), so run `bpa` again afterwards.

Of the community files' fix expressions (TabularEditor/BestPracticeRules and microsoft/Analysis-Services), tess
applies all but four: `AVOID_INVALID_NAME_CHARACTERS` and `AVOID_INVALID_DESCRIPTION_CHARACTERS` (computed values),
`PARTITION_NAME_SHOULD_MATCH_TABLE_NAME_FOR_SINGLE_PARTITION_TABLES` (another object's property) and
BPARules-standard-lax's `TranslatedDisplayFolder.Reset()` (a typo TE's script wouldn't compile either).

## Ignoring findings

As in TE, a `BestPracticeAnalyzer_IgnoreRules` annotation holds `{"RuleIDs": ["RULE_ID", ...]}`:
- On an object, it suppresses that object's findings for those rules. They are counted as ignored, and listed
  under `ignored` in JSON.
- On the model, it turns those rules off entirely.

TE's older spelling, `BestPractizeAnalyzer_IgnoreRules`, is read too. TOM calculation items have no
annotations, so ignore their findings on the model. `tess bpa rules ignore` writes these annotations.

## VertiPaq statistics (`--vpax`)

Rules that read VertiPaq Analyzer statistics (`GetAnnotation("Vertipaq_RowCount")` and the rest of that family)
find nothing on a model without them. `--vpax <file.vpax>` puts a file's statistics on the model as those
annotations for this run, in memory; `tess vertipaq ... --annotate --save` keeps them in the model. See
[VertiPaq statistics](model-analysis.md#vertipaq-statistics-vertipaq).

## Managing rules (`bpa rules`)

```sh
tess bpa rules list <model> [--layer builtin,model,config,env,cli]   # the rules in effect
tess bpa rules show <model> <rule-id>
tess bpa rules add <model> --id ID --scope Measure -e 'Name.Length > 40' [--to model|<file>] --save
tess bpa rules add <model> --from team.json --to model --save        # copy a rule file's rules into the model
tess bpa rules remove <model> <rule-id>... [--to model|<file>] --save
tess bpa rules ignore <model> <rule-id>[,<rule-id>...] [<object>...] --save
tess bpa rules unignore <model> <rule-id>[,<rule-id>...] [<object>...] --save
```

`list` and `show` take the same rule switches as `bpa` (`--rules`, `--no-builtin`, `--builtin-only`,
`--no-model-rules`, `--no-config`, `--allow-external-rules`), after the subcommand. Each rule shows its layer, its
source and the lower layers' definitions it overrides (`overrides` in JSON), and whether the model's ignore
annotation turns it off (`disabled`).

`add` builds a rule from `--id`, `--name`, `--category`, `--description`, `--severity` (default 1), `--scope`,
`--expression` (`-e`), `--fix-expression`, `--compatibility-level` (default 1200) and `--learn-more-url`, or
copies the rules of a TE rule file (`--from`). A rule that doesn't compile is added with a warning, as TE allows.
An ID that's already there is refused (exit 3) unless `--replace`. `remove` takes one or more IDs (`--from` is
the same as `--to`); removing a built-in rule is refused, with a pointer to `ignore`.

Where they write, `--to`:
- `model` (the default): the model's `BestPracticeAnalyzer` annotation, as TE writes it (an indented JSON
  array; the annotation is removed when it empties). This is a model edit and follows the
  [editing conventions](editing.md#editing-commands): a dry run unless `--save` or `--save-to`. The change shows as a
  diff of the annotation.
- a file path: a TE rule file, created if it's missing (`.tessera/bpa.json` is one). A dry run shows the rules
  added (`+`) and removed (`-`); `--save` writes the file. The file is edited as JSON, so other rules and fields
  tess doesn't know stay as they were, and its line endings are kept; comments and trailing commas are not
  (a note says so). `--save-to`, `--force`, `--stat` and `--name-only` are model-only.

`ignore` adds the IDs to each object's `BestPracticeAnalyzer_IgnoreRules` annotation, in TE's format
(`{"RuleIDs":["A","B"]}`), and `unignore` removes them, removing the annotation when it empties (TE would leave
`{"RuleIDs":[]}`, which means the same). Objects take any form `get` accepts. Without objects, the rule is
ignored on the model, which turns it off for the whole model. Both are model edits, as above. IDs that no rule in
effect has are warned about but still written, since TE accepts any ID. An object with no annotations
(calculation items) is refused (exit 3).

To manage rules for a model that is literally in a folder named `rules`, write `tess bpa ./rules`.

## Built-in rules

tessera ships its own rule set; `tess bpa <model> --list-rules` shows it with descriptions in `--json`.

| ID | Severity | What it flags |
|---|---|---|
| `TESS_UNUSED_HIDDEN_COLUMN` | 2 | hidden columns that no DAX, relationship, hierarchy, sort-by or variation uses |
| `TESS_UNUSED_HIDDEN_MEASURE` | 1 | hidden measures no other DAX uses |
| `TESS_MEASURE_FORMAT_STRING` | 2 | visible measures without a format string (or format string expression) |
| `TESS_MEASURE_DESCRIPTION` | 1 | visible measures without a description |
| `TESS_NUMERIC_COLUMN_SUMMARIZED` | 1 | visible numeric columns that summarize by default |
| `TESS_FLOATING_POINT_COLUMN` | 2 | `Double` columns |
| `TESS_RELATIONSHIP_DATA_TYPES` | 3 | relationships between columns of different data types |
| `TESS_RELATIONSHIP_BIDIRECTIONAL` | 2 | relationships that filter both ways |
| `TESS_RELATIONSHIP_MANY_TO_MANY` | 2 | many-to-many relationships |
| `TESS_RELATIONSHIP_INTEGER_KEYS` | 1 | relationships whose key isn't a whole number |
| `TESS_HIDE_FOREIGN_KEYS` | 2 | visible columns on the many side of a relationship |
| `TESS_TEXT_MONTH_SORT` | 2 | text month columns without a sort-by column |
| `TESS_MARK_DATE_TABLE` | 2 | models with date columns but no table marked as a date table |
| `TESS_AUTO_DATE_TABLE` | 2 | Power BI auto date/time tables |
| `TESS_IFERROR` | 2 | `IFERROR` in DAX |
| `TESS_DIVIDE` | 2 | `/` by anything but a constant (use `DIVIDE`) |
| `TESS_QUALIFY_COLUMNS` | 2 | column references without their table |
| `TESS_UNQUALIFIED_MEASURES` | 2 | measure references with a table |
| `TESS_EVALUATEANDLOG` | 3 | `EVALUATEANDLOG` left in DAX |
| `TESS_CALCULATED_COLUMN_AS_MEASURE` | 1 | numeric calculated columns that are only ever aggregated |
| `TESS_EMPTY_EXPRESSION` | 3 | measures, calculated columns/tables and calculation items with no expression |
| `TESS_EMPTY_CALCULATION_GROUP` | 2 | calculation groups without items |
| `TESS_NAME_WHITESPACE` | 2 | names with leading or trailing spaces |
| `TESS_TODO_COMMENT` | 1 | `TODO` in DAX comments |
| `TESS_EMPTY_PERSPECTIVE` | 1 | perspectives with no tables |

## Compatibility

All 145 rules in the community files `BPARules-standard.json`, `BPARules-standard-lax.json` and
`BPARules-PowerBI.json` (TabularEditor/BestPracticeRules) and Microsoft's `BPARules.json`
(microsoft/Analysis-Services) compile and run. On the SpaceParts sample model, and on synthetic models covering
roles, translations, perspectives, KPIs and calculation groups, they find the same objects as TE2's own
analyzer. Known differences:
- Rules that need data from a deployed model (e.g. a measure's `DataType`, or `Vertipaq_*` annotations that
  VertiPaq Analyzer writes) behave as they do in TE on a model loaded from files.
- Object names in the output use tess references, e.g. `table:Sales`.
- Rule expressions see only read-only members, so they can't call TE's methods that change the model. Fix
  expressions are applied as [above](#fixing-findings---fix), not run as C#.
