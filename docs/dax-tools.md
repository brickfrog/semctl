# DAX scripts, linting and formatting

## DAX scripts (`script`)

A DAX script is Tabular Editor 3's text format for many objects' DAX and properties in one file: export it, edit
it, apply it back. `sm script export <model> [objects...]` writes one; `sm script apply <model> <script>`
creates or updates each object it defines.

```
-------------------
-- Measure: [Total]
-------------------
MEASURE 'Sales'[Total] = SUM ( Sales[Amount] )
    Description = "Revenue, before returns"
    FormatString = "#,0.00"
    DisplayFolder = "KPIs"
    Visible = FALSE

-----------------------------------------
-- Calculation Group: 'Time Intelligence'
-----------------------------------------
CALCULATIONGROUP 'Time Intelligence'[Period]
    Precedence = 0

    CALCULATIONITEM "YTD" =
        TOTALYTD (
            SELECTEDMEASURE (),
            'Date'[Date]
        )
        FormatString = "0.00"
        Ordinal = 0
```

```sh
sm script export ./Sales.pbip                          # every DAX object, to stdout
sm script export ./Sales.pbip "[Total]" Sales -o x.dax  # a table stands for its measures, calculated columns and own DAX; an existing file needs --force
sm script export ./Sales.pbip -k measure,calcitem -t Sales --json
sm script apply  ./Sales.pbip x.dax                    # dry run: the changes, as `diff` shows them
sm script apply  ./Sales.pbip - --save < x.dax         # from stdin
```

What a script holds, and the properties each object takes (names are case-insensitive):

| Object | Written as | Properties |
|---|---|---|
| measure | `MEASURE 'Table'[Name] = <DAX>` | `Description`, `FormatString`, `DisplayFolder`, `Visible`, `DetailRows`, `KpiStatusExpression`, `KpiStatusDescription`, `KpiStatusGraphic`, `KpiTrendExpression`, `KpiTrendDescription`, `KpiTrendGraphic`, `KpiTargetExpression`, `KpiTargetDescription`, `KpiTargetFormatString`, `FormatStringDefinition` |
| calculated column | `COLUMN 'Table'[Name] = <DAX>` | `Description`, `FormatString`, `DisplayFolder`, `Visible`, `Datatype` |
| calculated table | `TABLE 'Name' = <DAX>` | `Description`, `Visible`, `DetailRows` |
| calculation group | `CALCULATIONGROUP 'Table'[Column]` (the column its items are listed in) | `Description`, `Visible`, `Precedence` |
| calculation item | `CALCULATIONITEM "Name" = <DAX>`, after its group | `Description`, `FormatString`, `Ordinal` |
| function | `FUNCTION Name = (params) => <DAX>` | `Description`, `Visible` |

Values:
- **Strings** are in double quotes, with `""` for a quote. They may span lines.
- **`Visible`** takes `TRUE` or `FALSE`; **`Ordinal`** and **`Precedence`** take whole numbers.
- **`Datatype`** takes `BOOLEAN`, `DOUBLE`, `INTEGER`, `DATETIME`, `CURRENCY` (decimal) or `STRING`.
- **DAX properties:** `DetailRows`, the `Kpi*Expression`s, and a calculation item's `FormatString` take DAX.
- **A measure's `FormatString`:** a lone string is the static format string; any other DAX is a dynamic format
  string. `FormatStringDefinition = <DAX>` (a sm extension) is always the dynamic one. export writes it when the
  DAX is a lone string, or when the measure also has a static format string. A static `FormatString` removes a
  dynamic one, unless the same object also sets `FormatStringDefinition`.

How a script is read:
- **Where DAX ends.** An expression runs until:
  - a comma outside parentheses;
  - a line that starts with a property name, `=` and a value of that property's kind (so the DAX line
    `Visible = 1` is not the `Visible` property, which takes `TRUE` or `FALSE`), outside parentheses;
  - or the next object keyword.
- **Commas.** Properties may be written with a leading comma, as Tabular Editor 3 does after an empty expression.
- **Comments.** A comment after an expression belongs to it if it's on the expression's last line, or indented
  deeper than the object's keyword. The banners at the margin don't belong to any expression.
- **Layout.** An expression that starts on the line after `=` loses the line break and its common indentation.
  Leading and trailing whitespace is not part of any expression, and neither is the kind of line break.
- **DAX queries.** The `DEFINE` section of a DAX query is a script (`DEFINE MEASURE 'T'[M] = ...`,
  `DEFINE COLUMN ...`, `DEFINE TABLE ...`, `DEFINE FUNCTION ...`); its `EVALUATE` sections are skipped. Query
  `VAR`s are not supported.
- **Errors.** Syntax errors, and objects that can't be applied, are reported as `script.dax:line:column: message`.
  With `--json`, the error also has `source`, `line` and `column`.

`apply` follows the [editing conventions](editing.md#editing-commands): a dry run unless `--save` or `--save-to`, the changes
as a diff, new errors refused unless `--force`. It works like this:
- **New or existing.** Each object is updated if it exists and created if it doesn't, the way `add` creates it.
  Objects and properties the script doesn't mention are left alone.
- **Forward references.** All objects are applied before the model is validated, so they may refer to objects
  defined later in the script.
- **`=` omitted.** Without `=`, an existing object keeps its expression and only its properties change.
- **Empty values.** `=` followed by nothing, or an empty value, clears that expression or property: `DetailRows`
  and dynamic format strings are removed.
- **No-op detection.** Expressions that differ only in leading and trailing whitespace or line breaks count as
  unchanged. So exporting and applying back changes nothing, even for a `.bim` file with `\r\n` in its DAX.
- **Ordinals.** `Ordinal` sets an item's ordinal as given; other items are not renumbered.
- **Refusals.** `apply` refuses:
  - a new object without an expression;
  - an expression on a data column, or on a table that isn't calculated (properties alone are fine);
  - a calculation group whose `[Column]` isn't its items' column (use `sm rename` for that);
  - a measure name that another table already uses;
  - a new object in a table that doesn't exist.

`export` writes objects in model order: for each table, its own DAX (or its calculation group with its items, by
ordinal), then its calculated columns and measures; functions come last. Expressions are written as stored, with
`\n` line breaks. Multi-line ones start on the line after `=` and are indented. `export` reads the script back
before writing it. An object whose DAX would not read back exactly is named in a warning. Examples are a line that
reads like a property (`Visible = TRUE`) or a comma outside parentheses (`1,1`, from a locale that writes decimals
that way).

## Linting DAX (`lint`)

`lint` checks every DAX expression in the model for code that can be written better: Tabular Editor 3's
[code actions](https://docs.tabulareditor.com/te3/features/code-actions.html), offline. Rules that match one of
TE3's code actions use its id and name; `TS` ids are semctl's own. It reads measures, calculated columns and
tables, calculation items, KPIs, format string and detail rows expressions, row filters and functions.

```
sm lint ./Sales.pbip                             # report (exit 1 on errors, like validate)
sm lint ./Sales.pbip --rule DI003,DI004 --fail-on warning --ci github
sm lint ./Sales.pbip --fix                       # show the rewrites (a dry run)
sm lint ./Sales.pbip --fix --rule DI001 --save   # remove unused variables, and save
```

| ID | Severity | Name | Before | After |
|---|---|---|---|---|
| `TS001` | error | Replace non-standard whitespace | `1 + 2`, with U+00A0 before the `+` | `1 + 2` |
| `DI001` | warning | Remove unused variable | `VAR a = 1 VAR b = 2 RETURN b` | `VAR b = 2 RETURN b` |
| `DI002` | warning | Remove all unused variables | `VAR a = 1 VAR b = a RETURN 123` | `123` |
| `DI003` | warning | Remove table name from measure references | `'Sales'[Total] * 1.25` | `[Total] * 1.25` |
| `DI004` | warning | Add table name to column references | `SUMX(Sales, [Amount])` | `SUMX(Sales, 'Sales'[Amount])` |
| `DI008` | warning | Remove superfluous CALCULATE | `VAR x = [Total] RETURN CALCULATE(x, 'Date'[Year] = 2025)` | (not fixed) |
| `DI014` | warning | Replace IFERROR with DIVIDE | `IFERROR(([A] - [B]) / [B], BLANK())` | `DIVIDE([A] - [B], [B])` |
| `DR014` | info | Simplify using IN | `'Doc'[Type] = "Invoice" \|\| 'Doc'[Type] = "Credit"` | `'Doc'[Type] IN {"Invoice", "Credit"}` |

What each rule reports, and when it doesn't fix:
- **`TS001`:** no-break, zero-width, typographic and other non-standard spaces (U+00A0, U+200B, U+FEFF, ...)
  between tokens. Each becomes a regular space. Strings, quoted names, `[names]` and comments are left alone,
  since names can contain such characters. TE3 3.27.1 rejects these characters, and `validate` reports the
  zero-width ones as `invalid-token`.
- **`DI001`, `DI002`:** `DI001` is a `VAR` that nothing refers to. `DI002` is one used only by other unused
  variables. The fix removes every variable the `RETURN` doesn't need, so fixing either id removes both kinds.
  A definition on lines of its own is removed with its lines, including a comment at the end of its last line.
  Comments on other lines stay. When every variable goes, the block becomes its `RETURN` expression. That
  isn't done where the block is an operand (`2 * VAR a = 1 RETURN 3 + 4`), because it would change how the
  expression parses. Variables whose name is defined twice in one expression, or is also a function parameter,
  are not analysed.
- **`DI003`, `DI004`:** the fix writes `[Measure]` or `'Table'[Column]`. The table is always quoted, like TE's
  `DaxObjectFullName`. Columns resolve against the expression's own table, as in `deps`. A column isn't fixed
  when a measure anywhere has the same name, since `[Name]` might mean either. References inside `NAMEOF(...)`
  are left as written, because field parameters store the name NAMEOF returns. These match `bpa`'s
  `SM_QUALIFY_COLUMNS` and `SM_UNQUALIFIED_MEASURES`, and after `lint --fix` those rules have nothing left to
  report, except measures inside `NAMEOF`, which `bpa` still flags.
- **`DI008`:** `CALCULATE(x, ...)` or `CALCULATETABLE(x, ...)` where `x` is a variable. A variable is evaluated
  where it is defined, so CALCULATE's filters don't change it. That is almost always a mistake, so it is
  reported, not fixed: the fix TE3 offers (removing CALCULATE) would hide it. UDF parameters are not variables:
  an `EXPR` parameter is evaluated inside CALCULATE.
- **`DI014`:** only when `a / b` is the whole first argument, with a single top-level `/` and nothing that binds
  more loosely around it (`*` before the division, `^` and signs are fine). `IFERROR(..., BLANK())` loses its
  `BLANK()`, which is DIVIDE's default. **This fix changes edge-case results.** DIVIDE returns `x` when `b` is
  zero or blank, where `a / b` gives infinity (or NaN for 0/0) and IFERROR passes that through. DIVIDE also
  doesn't catch errors raised inside `a` or `b`. Usually that is what the IFERROR was meant to do. Use
  `--rule` to leave `DI014` out if not.
- **`DR014`:** two or more `=` comparisons of the same column, measure or variable, joined by `||` or by
  `OR(...)` (nested `OR`s count, and are rewritten whole), as a whole expression or argument. The values must be non-zero numbers or non-empty strings, all of one kind. `=`
  treats BLANK as equal to `0`, `""` and `FALSE`, but `IN` doesn't, so for these values the two agree.
  Chains containing comments are reported but not fixed.

Options:
- `--rule`, `--min-severity`, `--fail-on` and `--ci` work as for [`validate`](model-analysis.md#validation-validate).
  `--rule` ids are case-insensitive.
- Text output has `validate`'s layout, plus a line saying how many findings `--fix` would fix.
- `--json` writes the [findings envelope](ci.md#findings-json-and-ci-annotations). Each finding has `ruleName`, a
  `category` (TE3's `Improvement` or `Readability`, or `Correctness` for `TS001`) and `fixable`. The envelope
  adds a top-level `fixable` count, and `ignored`: the findings suppressed by annotations (see below).

With `--fix`, `lint` is an [editing command](editing.md#editing-commands):
- It is a dry run unless `--save` or `--save-to`. `--stat` and `--name-only` work as for the other editing
  commands, and `--json` writes the change set.
- It fixes the findings that `--rule` and `--min-severity` select.
- Only the rewritten tokens change. Fixes repeat until nothing more applies (removing one variable can leave
  another unused), so a second `--fix` changes nothing.
- Every fix is checked before it is applied: the rewritten DAX must lex at least as cleanly, keep a VAR
  structure that parses, and refer to the same objects. A fix that fails the check is reported with
  `fixable: false`.
- Findings left unfixed are counted in a warning on stderr. `--fail-on` and `--ci` are for reports, and are
  refused with `--fix` (exit 2), as are `--save`, `--save-to`, `--force`, `--stat` and `--name-only`
  without it.

To suppress findings, give the object a `SemctlLint_IgnoreRules` annotation listing rule ids, in the format of
TE's BPA ignore annotation: `{"RuleIDs":["DI004","DR014"]}` (TE's plain array form, `["DI004"]`, works too; ids are
case-insensitive). It covers every expression the object holds (a measure's KPI and format string expressions
too, and a role's row filters). On the model, it turns the rules off for every expression; that is also the way
to ignore a rule on a calculation item, which TOM gives no annotations. Ignored findings are counted under the
report (`ignored` in JSON), don't count for `--fail-on`, and `--fix` leaves them alone. TE has no convention for
suppressing its code actions, so the annotation is sm's own; TE leaves it alone. To set it:

```
sm set ./Sales.pbip 'measure:Sales/Margin' -p 'annotations.SemctlLint_IgnoreRules={"RuleIDs":["DI004"]}' --save
```

## Formatting DAX (`format`)

`format` lays DAX out in the style of SQLBI's
[Rules for DAX code formatting](https://www.sqlbi.com/articles/rules-for-dax-code-formatting/), the style
[DAX Formatter](https://www.daxformatter.com) popularised. It runs entirely offline: nothing is sent anywhere.

```
sm format --expr "calculate(sum(Sales[Amount]),all('Date'))"   # loose DAX, to stdout
sm format - < measure.dax                                     # stdin
sm format query.dax --json                                    # {success, formatted, errors}
sm format query.dax --save                                    # format the file in place
sm format ./Sales.pbip                                        # every DAX expression in a model (a dry run)
sm format ./Sales.pbip "[Revenue]" -t Date --save             # some of them, and save
sm format ./Sales.pbip --check                                # CI: exit 1 if anything isn't formatted
```

Before and after:

```
calculate(sumx(Orders,Orders[Amount]),filter(all(Customers),calculate(countrows(Sales),all(Calendar[Date]))>42+8-25*(3-1)+2-1+2-1+calculate(2+2-2+2-2)-calculate(4)))
```

```
CALCULATE (
    SUMX ( Orders, Orders[Amount] ),
    FILTER (
        ALL ( Customers ),
        CALCULATE ( COUNTROWS ( Sales ), ALL ( Calendar[Date] ) )
            > 42 + 8 - 25 * ( 3 - 1 ) + 2 - 1 + 2 - 1
                + CALCULATE ( 2 + 2 - 2 + 2 - 2 )
                - CALCULATE ( 4 )
    )
)
```

```
var total = sum(Sales[Amount]) // all rows
var top = calculate(sum(Sales[Amount]), topn(10, values(Product[Name]), [Revenue]))
return if(total > 0, divide(top, total))
```

```
VAR total = SUM ( Sales[Amount] ) // all rows
VAR top = CALCULATE ( SUM ( Sales[Amount] ), TOPN ( 10, VALUES ( Product[Name] ), [Revenue] ) )
RETURN
    IF ( total > 0, DIVIDE ( top, total ) )
```

The style:
- **Calls:** built-in function names in upper case, with a space before `(` and spaces inside the parentheses:
  `SUM ( Sales[Amount] )`, `BLANK ()`. A call that fits on its line stays on one line. Otherwise each argument
  goes on a line of its own, indented 4 from where the call starts, with the comma at the end of the line and
  `)` under the function name. `SWITCH` keeps each value and its result on one line when they fit.
- **Operators** are spaced. A chain of operators that doesn't fit is split before an operator, and continues
  one indent in. Arithmetic and `&` keep as many operands on a line as fit, but an operand containing a function
  call starts a line of its own. `&&`, `||` and comparisons put each operand on a line. After a multi-line
  operand, the next operator follows its closing bracket: `) / 'Budget Rate'[Rate]`, unless the operand after it
  starts with a function call, which starts a row of its own, as in SQLBI's example (`- CALCULATE ( 4 )`). An operand
  with comments on the lines before it still follows the bracket, so its operator isn't left alone on a line.
- **VAR/RETURN:** a `VAR`'s value stays on the `VAR` line if it fits, otherwise it goes on the next line,
  indented. `RETURN` is on a line of its own, with its expression indented below it.
- **Queries:** `DEFINE`, `EVALUATE`, `ORDER BY` and `START AT` start lines. Definitions are indented under
  `DEFINE`, and a `MEASURE`, `COLUMN`, `TABLE` or `FUNCTION` definition is on the line before its expression,
  as SQLBI's rules say.
- **Keywords** (`VAR`, `RETURN`, `IN`, `NOT`, `TRUE`, `ASC`, `DEFINE`, ...) are upper case. So are words that
  are only sometimes keywords (`YEAR`, `DESC`, `ROWS`, `BLANKS FIRST`, UDF types like `TABLE EXPR`), but only
  where they're in a keyword's place: a whole argument of a built-in function, words side by side, or a type
  after `:`. Calls to user-defined functions, and the model's table, calendar and variable names, keep their
  case.
- **Kept as written:** strings, quoted and bracketed names, numbers, how tables are quoted (`Sales[x]` stays
  unquoted, `'Sales'[x]` stays quoted), and comments. A comment on a token's line stays on that line, and a
  comment on a line of its own stays on a line of its own. A comment after an argument moves past its comma.
  One blank line is kept where the source had blank lines between `VAR`s, definitions and comments.
- **A model expression that starts on the next line** (a line break before its first token, as Power BI and
  Tabular Editor often store measures) keeps one line break there, as SQLBI's rules put the definition "in the row
  before" and as Tabular Editor does
  ([TabularEditor#638](https://github.com/TabularEditor/TabularEditor/issues/638)).
- Line endings follow the input (CRLF in, CRLF out).

Options:
- `--width N`: the line width to fit (default 100; the article doesn't give one).
- `--long`: SQLBI's strict rule. A call with two or more arguments, or whose only argument is a function call,
  always has its arguments on separate lines, even when it would fit, inside an operator chain or a `VAR` too.
- `--no-space-after-function`: `SUM( x )` rather than `SUM ( x )`.
- `--semicolons` (loose DAX only): the input separates arguments with `;` and writes decimals with `,`
  (`IF ( [a] > 1,5; 2; 3 )`). The output keeps that dialect. Without the option, a `;` is an error, so
  semicolon-separated DAX is never silently read as comma-separated (where `1,5` would be two arguments).
  A model's DAX is always comma-separated.

What it reads:
- `-` reads stdin, `--expr` takes the DAX as an argument, and any file that isn't a model file is read as DAX.
  Model files (`.bim`, `.pbip`, `.pbism`, `.pbir`, `.tmdl`) and folders are [models](model-files.md#accepted-model-paths).
  Loose DAX can be an expression, a function (`(x: INT64) => x * 2`) or a query (`DEFINE` / `EVALUATE`).
- In a model: measures (with their format string, detail rows and KPI expressions), calculated columns, calculated
  tables, calculation items and calculation group expressions, detail rows expressions, row filters and functions.
  M (partitions and named expressions) is left alone.
- With a model, objects after the path limit what's formatted (any form `get` accepts; a table stands for every
  expression in it, row filters on it included), and so do `-k` and `-t`, as for `list`.

Safety: the formatter changes only whitespace and the case of keywords and built-in function names. Before it
returns anything, it lexes the result and checks that it has the input's tokens in the input's order (keywords
and built-in function names may differ in case), with the same comments in the same order, and that lexemes the
lexer splits (`==`, a `1,5` decimal) weren't pulled apart. In a model, it also checks that every reference
resolves to the same object as before. A result that fails a check is refused, never written, and reported as a
semctl bug (exit 70 for loose DAX; a warning for an expression in a model). Formatting is idempotent: formatting formatted DAX changes nothing.

Output and exit codes:
- Loose DAX: the formatted DAX on stdout. `--json` writes `{"success", "formatted", "errors"}`, like
  `te util format-dax`. Each error has `line`, `column` (1-based) and `message`. DAX that can't be read
  (unbalanced brackets, an unterminated string, an operator without an operand, `VAR` without `RETURN`, ...) exits
  3 with its errors on stderr, nothing on stdout, and the input file left alone.
- A DAX file with `--save`: the formatted DAX is written back to the file, in its encoding (UTF-8, or UTF-16 when
  it starts with that byte order mark), with its byte order mark and line endings, and a line on stderr says so.
  Nothing is written if the file is formatted already or can't be read, and a file that isn't valid UTF-8 or
  UTF-16 is refused (exit 3) rather than rewritten. `--json` adds `"saved": {"path"}` when it wrote. `--save` with `-` or `--expr`, or with `--check`, is a usage
  error; `--save-to`, `--force`, `--stat` and `--name-only` are for models.
- A model is an [editing command](editing.md#editing-commands): a dry run unless `--save` or `--save-to`, the change shown
  as a diff (`--stat`, `--name-only`, `--json` as usual). Expressions that can't be read are left as they are,
  with a warning each.
- `--check` changes nothing. It exits 1 if anything would change, like `prettier --check` or `fantomas --check`.
  For a model it lists the expressions that would change, one `<ref> <property>` per line. With `--json` it
  writes `{"check": true, "expressions", "unformatted": [{ref, kind, property}], "failed", "warnings"}`. For loose
  DAX, `--json` adds `"changed"`.

Regression corpus: [tests/format-corpus](../tests/format-corpus) holds cases from public formatter bug reports
(Tabular Editor, DAX Studio, DAX Formatter) and from SQLBI's rules, each citing its source, including the known
differences from the rules (quotes around table names, the article's hand-broken example).

Not done: the formatter doesn't rewrite code. It doesn't qualify column references or remove table names from
measure references, as SQLBI's rules also ask; `lint` does those. It doesn't add or remove quotes around table
names either. Comments laid out as a column to the right of the code (`--|` boxes) keep their text and order,
but not their alignment.

## DAX packages (`pkg`)

[daxlib.org](https://daxlib.org) publishes libraries of DAX user-defined functions ("packages"). `sm pkg`
installs them into a model the way Tabular Editor 3's
[DAX Package Manager](https://docs.tabulareditor.com/en/features/dax-package-manager.html) (3.24+) does, so a
model either tool touched shows the same installed packages in the other.

```sh
sm pkg list ./Sales.pbip                                   # installed packages (reads only the model)
sm pkg search svg --from ~/src/daxlib                      # a daxlib checkout, offline
sm pkg show DaxLib.SVG --from ./packages                   # versions, functions, dependencies
sm pkg install ./Sales.pbip DaxLib.SVG --from ./packages   # dry run: the diff
sm pkg install ./Sales.pbip DaxLib.SVG@1.0.0 --from ./packages --save
sm pkg update ./Sales.pbip --from ./packages               # every package to its latest version: the diff
sm pkg remove ./Sales.pbip DaxLib.SVG --save               # reads only the model
sm pkg search svg --from daxlib                            # online: daxlib.org's API (see below)
```

Packages hold functions, which need compatibility level 1702 or higher; a lower model is refused, not upgraded.
`install`, `update` and `remove` are editing commands (dry run unless `--save`, `--save-to`, the save gate,
`--stat`, `--name-only`, `--json` as for [the other editing commands](editing.md#editing-commands)). Several packages can be
named at once; they apply in order as one change.

**Sources.** A package is a folder holding `manifest.daxlib` and `lib/functions.tmdl`, or the same as a `.daxpkg`
(zip) file. `--from` takes one package (folder or `.daxpkg`), a `.zip` of many (every folder in it holding a
`manifest.daxlib`: GitHub's "Download ZIP" of [github.com/daxlib/daxlib](https://github.com/daxlib/daxlib)), or a
folder searched for any of these (up to six levels deep, hidden folders skipped): a checkout of daxlib, or a
folder of downloaded `.daxpkg` files. Package files over 16 MB and zips over 256 MB are refused. Without `--from`, the `SM_PKG_SOURCE` environment variable names the
source (a path, or `daxlib`). With neither, the command fails: sm never picks a source, and never goes online, on
its own. `list` and `remove` need no source at all.

**Online (daxlib.org).** Only `--from daxlib` or `--from https://api.daxlib.org` (or the same in `SM_PKG_SOURCE`)
goes online, and `--offline` refuses it even then. `daxlib` while a file or folder of that name is in the current
directory (a clone is called that) is refused as ambiguous: pass `./daxlib` for the folder, or the URL. sm then sends HTTPS GET requests to `https://api.daxlib.org` and nowhere else (another host, plain
http, a non-default port or a redirect is refused), with no cookies, no credentials, no body, and the User-Agent
`semctl`. Nothing about the model is sent except, by `update`, the IDs of the packages being updated: with no
IDs named, every installed package recorded under daxlib.org's provider (one recorded under another provider is
skipped with a warning, its ID never sent). The requests are the ones daxlib's own client makes:

| What | URL |
|---|---|
| `search <text>` | `https://api.daxlib.org/v1/query-packages?q=<text>&skip=<n>&take=100&prerelease=<true\|false>` (pages of 100, at most 20) |
| a package's versions | `https://api.daxlib.org/v1/package/<id>/versions.json` |
| a package version | `https://api.daxlib.org/v1/package/<id>/<version>/<id>.<version>.daxpkg` |

IDs and versions are lower-cased in the URLs. `show`, `install` and `update` fetch the versions, then the package;
`update` does so for each package it updates. Requests time out after 30 seconds; a response over 16 MB is
refused. The tests never go online: they use local folders and a fake of the API.

**What is recorded** (TE3's format, checked against a model TE3 wrote,
[spaceparts-analytics](https://github.com/TabularEditor/spaceparts-analytics)):
- on the model, the extended property `TabularEditor_ModelDaxPkgTable` (JSON): one entry per installed package
  under a numeric key, `{"Provider": "https://api.daxlib.org", "PackageId": ..., "Version": ...}`, indented as TE3
  writes it, and `TabularEditor_ModelDaxPkgSeq`, the next key (it goes up by one per install);
- on each of the package's functions, `TabularEditor_ObjDaxPkgHandle` (its package's key) and
  `TabularEditor_ObjDaxPkgContentHash`, the upper-case hex SHA-256 of the function's expression as installed (for
  spaceparts' unchanged functions, installing the same package versions with sm gives the same hashes, keys,
  table and sequence, byte for byte).

The provider recorded is `https://api.daxlib.org` for packages from a local source too (they are daxlib's
packages), so TE3 offers their updates. Everything else in the package's TMDL (descriptions, annotations such as
`DAXLIB_PackageId`) is copied as it is; tracking properties a package carries itself are dropped.

**Conflicts**, refused unless a flag says what to do (TE3 asks the same questions in a dialog):
- a package function whose name the model already has: `--overwrite` replaces the model's (keeping its lineage
  tag), `--skip-existing` keeps it;
- on `update` and `remove`, package functions changed since they were installed (their expression no longer
  matches the hash; line endings don't count, as in TE3 3.24.1): `--discard-changes` overwrites or removes them,
  `--keep-changes` leaves them (on `remove`, no longer linked to the package);
- functions that other DAX still calls, on `remove` or when an update drops them: refused as by
  [`remove`](editing.md#removing-objects-remove), unless `--force` (each call left behind is then an `unknown-function`
  error, shown as introduced and reported by `validate` afterwards);
- a package that depends on packages the model doesn't have: `--ignore-dependencies` installs it anyway
  (dependencies are not installed for you);
- installing a package that is installed already: use `update`, which also takes `<id>@<version>` to move to an
  older version. An update keeps the package's key.

`list --json` gives `packages[]` (`id`, `version`, `provider`, `key`, `functions[]` with `name`, `ref` and `state`:
`unchanged`, `changed` or `unknown` when there is no hash) and `orphans[]` (functions with a key the table
doesn't have). Error codes: `no-package-source` (exit 2), `package-not-found`, `package-version-not-found`,
`invalid-package`, `package-source-unreadable`, `package-conflict`, `package-not-installed`,
`invalid-package-metadata` (exit 3), `package-fetch-failed` (exit 5).

## C# scripts (`csharp`)

`sm csharp` runs a C# script against the model's TOM objects, then shows what it changed, as the
[editing commands](editing.md#editing-commands) do: a dry run unless `--save` or `--save-to`, with the save gate,
`--stat`, `--name-only` and `--json`. The command's name is provisional (`script` is taken by DAX scripts).

```sh
sm csharp ./Sales.pbip format-measures.csx           # dry run: the diff
sm csharp ./Sales.pbip format-measures.csx --save
```

```csharp
// format-measures.csx
foreach (var m in Model.Tables.SelectMany(t => t.Measures).Where(m => m.Name.EndsWith("%")))
{
    m.FormatString = "0.0%";
    Info($"formatted {m.Name}");
}
```

- **The script** is a C# file of top-level statements. `using` directives, local functions and classes work as in
  any C# file.
- **References:** the whole .NET 10 shared framework, TOM and Semctl.Core. `#r` and NuGet packages are not
  supported.
- **Names TOM shares with the framework** are ambiguous, because TOM's namespace is imported: write
  `System.Text.Json.JsonSerializer`, not `JsonSerializer`.
- **In scope:**
  - `System`, `System.Linq`, `System.Collections.Generic` and `Microsoft.AnalysisServices.Tabular`;
  - `Model`, the model's TOM `Model`;
  - `Info(text)`, `Warning(text)` and `Output(value)`, which write to stderr;
  - `Error(text)`, which fails the script.
- **The `SM` symbol** is defined, so `#if SM` tells a script it runs under sm.
- **TE scripts:** scripts written against Tabular Editor's own object model, such as `Model.AllMeasures`,
  `Selected` or `FormatDax`, don't compile here. Only scripts that stay within raw TOM run unchanged.
- **Output:** everything the script writes, its `Console` output included, goes to stderr, so `--json` on stdout
  stays clean.
- **Errors:**
  - a compile error is reported at the script's own file, line and column (`script-compile-failed`, exit 3);
  - an exception or `Error()` is `script-failed` (exit 3);
  - a failed script saves nothing.

**Security.** The script must be a local file: sm never fetches one (a URL is refused) and never runs one on its
own. It runs inside sm with your rights and can do anything you can, network included. sm's offline guarantee
covers sm, not the code you give it. Treat a script like any program you run. It is compiled in memory with
Roslyn's C# compiler and needs no files of its own on disk, so it works in the single-file binary. The
measurements and the design are in [docs/scripting-design.md](scripting-design.md). It has been run on
Linux only so far.
