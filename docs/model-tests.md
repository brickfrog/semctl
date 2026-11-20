# Testing a model (`test`)

`tess test` runs DAX assertion tests for a model. On model files it evaluates them with
[the local DAX engine](dax-engine.md#the-local-dax-engine-query---data) over [fixture data](fixture-data.md#fixture-data-data): no server, no
Power BI licence, no network, so a model's measures can be tested in CI. Given an `xmla:` model, the same suites
run on the server instead.

```sh
tess test init    ./Sales.bim --data ./fixtures     # a suite with one pending test per measure, to fill in
tess test run     ./Sales.bim                       # run .tess-tests/*.test.yaml next to the model
tess test run     ./Sales.bim --tag revenue --ci github --trx results.trx
tess test run     "xmla:powerbi://api.powerbi.com/v1.0/myorg/Finance;Sales"     # the same suites, on the server
tess test snapshot ./Sales.bim --save               # record the results of the snapshot tests
tess test compare ./Sales.bim --against ./Sales-refactored.bim                  # same results from both?
tess test compare ./Sales.bim --against "xmla:...;Sales"                        # the local engine against the server
tess test list                                      # check the suites, without a model
```

## A worked example

`Sales.bim` has a table `Sales` (Id, Region, Year, Amount) and three measures: `Revenue = SUM ( Sales[Amount] )`,
`Average Sale` and `Revenue Label = FORMAT ( [Revenue], "#,0" )`. `fixtures/Sales.csv` holds three rows:

```
Id,Region,Year,Amount
1,North,2023,100.50
2,North,2024,200
3,South,2024,49.50
```

`.tess-tests/revenue.test.yaml`:

```yaml
suite: Revenue
data: ../fixtures                 # the fixture directory, relative to this file
defaults:
  tags: [revenue]
tests:
  - name: total revenue
    measure: Revenue
    expect: 350

  - name: revenue in {year}       # one test per matrix entry
    matrix:
      - { year: 2023, revenue: 100.5 }
      - { year: 2024, revenue: 249.5 }
    measure: Revenue
    filters:
      Sales[Year]: "{year}"
    expect: "{revenue}"

  - name: average sale, to the cent
    measure: Average Sale
    expect: { value: 116.67, tolerance_abs: 0.005 }

  - name: revenue by region
    query: |
      EVALUATE SUMMARIZECOLUMNS ( Sales[Region], "Revenue", [Revenue] )
    expect:
      table:
        - { Region: North, Revenue: 300.5 }
        - { Region: South, Revenue: 49.5 }

  - name: no negative sales
    query: EVALUATE FILTER ( Sales, Sales[Amount] < 0 )
    expect: { empty: true }

  - name: an empty model has no revenue
    tables:
      Sales: []                   # inline rows instead of the fixture's file
    assert: ISBLANK ( [Revenue] )

  - name: the label
    measure: Revenue Label
    expect: "350"
```

```
$ tess test run ./Sales.bim
Revenue  (/work/.tess-tests/revenue.test.yaml)
  pass         total revenue
  pass         revenue in 2023
  pass         revenue in 2024
  pass         average sale, to the cent
  pass         revenue by region
  pass         no negative sales
  pass         an empty model has no revenue
  unsupported  the label
               Not supported by the local DAX engine: the function FORMAT (measure 'Sales'[Revenue Label] 1:1)

8 tests: 7 passed, 1 unsupported (0.4 s)
```

A failing test says what differs:

```
  FAIL         total revenue
               [Value] is 350.0, expected 351
```

## Outcomes and the exit code

| Outcome | Means | Finding |
|---|---|---|
| `passed` | the expectation holds | none |
| `failed` | it doesn't: the message and `differences` say how, value by value | `TEST_FAIL`, error |
| `error` | the query failed and the test doesn't expect it to, or the test can't be run as written | `TEST_ERROR`, error |
| `unsupported` | the local engine refused: the test uses something it doesn't implement (it names what) | `TEST_UNSUPPORTED`, warning |
| `skipped` | not run: `pending`, `skip`, after a failure with `--fail-fast`, or a test with its own data on a server | none |

`test run` exits 1 when any test failed or errored, when a suite is invalid, and **when no test reached a verdict**
(none passed and none failed: `TEST_NONE_RAN`). An unsupported test is not a verdict. By default it doesn't fail the
run: the engine is a subset, a refusal says nothing about your model, and failing would leave a team with a red
build they can't fix. It is never silent, though: it is counted in the summary line, it is a warning finding (and a
`--ci` annotation), and a run where nothing but unsupported or pending tests is left exits 1. `--strict` (the same
as `--fail-on warning`) makes any unsupported test fail the run, for a suite that is meant to be fully covered.

What stops a run altogether, with its usual exit code and no report: no suites found (3, `no-test-suites`), a
model that can't be loaded (3), a server that can't be reached (5), a `--trx` / `--junit` file that can't be
written (4), and, when the first test that needs it gets there, a fixture that can't be loaded or has errors (3),
a role the model doesn't have (3, `role-not-found`) or roles none of which can read (3, `role-cannot-read`)
or a snapshot file that can't be read (3). An invalid suite stops it too, as findings (exit 1): **suites are checked before the model is
loaded or a connection made**, and nothing runs if one of them is wrong, so a broken file can't pass as "no tests
failed".

## Where suites are

`tess test run <model> [suites...]` takes suite files or directories (searched recursively for `*.test.yaml`,
`*.test.yml` and `*.test.json`). With none given it uses the `.tess-tests` directory next to the model: beside a
`.bim` or `.pbip` file, beside a model folder (inside it, for a folder that holds a `.pbip`); failing that,
`.tess-tests` in the current directory (the only place looked in for an `xmla:` model).

## The suite format

The format follows the Tabular Editor CLI's `.te-tests/*.test.yaml` where it can, so a suite written for `te test`
runs here: `suite`, `description`, `defaults` (`tolerance`, `tolerance_abs`, `tags`), and tests with `name`,
`query` / `query_file`, `tags`, `tolerance` / `tolerance_abs`, `matrix` and `expect` (a column's value, `gt` / `gte`
/ `lt` / `lte`, `rows`, `empty`, `columns`, `error`). What tess adds is marked below. A JSON file (`*.test.json`)
with the same keys works too (comments and trailing commas allowed).

Suite keys:

| Key | |
|---|---|
| `suite` | the suite's name (default: the file's name) |
| `description` | |
| `data` | *(tess)* the fixture directory the tests evaluate over, relative to the suite file. `--data` replaces it |
| `tables` | *(tess)* inline rows, for every test of the suite: see below |
| `defaults` | `tolerance`, `tolerance_abs`, `tags`, and *(tess)* `filters`, for every test |
| `now` | *(tess)* the instant `TODAY`, `NOW`, `UTCTODAY` and `UTCNOW` return, as `tess query --now` takes it: `2024-03-05`, `2024-03-05T14:30:00`, `2024-03-05T14:30:00+02:00` (the offset only moves the UTC functions). Without it, the machine's clock |
| `roles` | *(tess)* the model's roles every test queries as (`roles: Europe`, or a list), as `tess query --role`: see [roles](dax-engine.md#roles-row-level-security) |
| `identity` | *(tess)* who is asking, for every test: `{ user_name, user_principal_name, user_object_id, user_culture, custom_data }`, as `tess query`'s identity options |
| `tests` | the list of tests |
| `connection` | te's; read and not used (a note on stderr says so): the model is the one on the command line |

Test keys:

| Key | |
|---|---|
| `name` | required; unique in the suite, ignoring case |
| `description`, `tags` | `tags` add to the defaults' |
| `query` / `query_file` | a DAX query with one `EVALUATE` (`query_file` is relative to the suite) |
| `measure` | *(tess)* a measure's name, exactly: `measure: Revenue` evaluates `[Revenue]` |
| `expression` | *(tess)* any scalar DAX expression |
| `assert` | *(tess)* a scalar DAX expression that must be `TRUE`; takes no `expect` |
| `expect` | what must hold: see below. Required, except for `assert` and pending tests |
| `filters` | *(tess)* the filter context: `Table[Column]: value` or a list of values |
| `matrix` | run the test once per combination of values |
| `data`, `tables` | *(tess)* the test's own fixture directory, and its own inline rows |
| `tolerance`, `tolerance_abs` | for this test's numbers |
| `pending: true`, `skip: <reason>` | *(tess)* don't run it (it is reported as skipped) |
| `now` | *(tess)* the test's own clock, replacing the suite's. A test with one is skipped on a server, whose clock can't be set |
| `roles`, `identity` | *(tess)* the test's own, replacing the suite's (`roles: []` for none). On a server the test has a connection of its own with `Roles=`, `EffectiveUserName=` and `CustomData=`; a test with `user_object_id` or `user_culture` is skipped there |
| `compare` | te's; read and not used |

A test has exactly one of `query`, `query_file`, `measure`, `expression`, `assert`. An unknown key is an error, with
its line and the key that was probably meant: a misspelt `expect` must not turn a test into one that checks nothing.

**Values** are typed as YAML types them: `350` and `1.5` are numbers, `true` a boolean, `null` (or `~`) BLANK,
`2024-01-31` (unquoted) a date, anything quoted is text. BLANK only equals BLANK, never 0 or `""`; text compares
exactly; a whole number equals the same decimal number (`350` matches a decimal `350.0`).

**`expect`** for `measure` and `expression` is the value itself (`expect: 350`, `expect: null`), or a mapping:
`value`, `gt` / `gte` / `lt` / `lte`, `tolerance` / `tolerance_abs`, or `error`, or `snapshot: true`.

**`expect`** for a query is a mapping. Every key that isn't one of these is a column of the result:

| Key | |
|---|---|
| *column*: value, or `{ value, gt, gte, lt, lte, tolerance, tolerance_abs }` | the column's value in a result of exactly one row (`EVALUATE ROW ( "A", ..., "B", ... )` asserts several) |
| `rows: 5` or `rows: { gte: 1, lte: 100 }` | the number of rows |
| `empty: true` / `false` | no rows / at least one |
| `columns: [Region, Revenue]` | the result has these columns |
| `table:` a list of rows (`- { Region: North, Revenue: 300.5 }`) | *(tess)* the whole result: these rows and columns, no others |
| `file: expected.csv` (or `.json`) | *(tess)* the whole result, from a file next to the suite: a CSV with a header row, a JSON array of row objects, or a result as `tess query --json` writes one |
| `ordered: true` / `false` | *(tess)* whether row order matters for `table`, `file` and `snapshot`. Default: only when the query has an `ORDER BY` |
| `snapshot: true` | *(tess)* the result recorded by `tess test snapshot` |
| `error: true`, `error: <code>`, or `error: { code, message }` | the query must fail; with the error's code, and/or text its message contains |

Columns are named as you'd say them: `Revenue` is the result's `[Revenue]`, `Region` its `Sales[Region]` (when only
one column ends that way), and the full name always works. In a CSV file an empty field is BLANK and `""` the empty
string, as in fixtures, and each field is read as the type of the result column it is compared with.

**Tolerance.** `tolerance` is relative (0.01 is 1%, of the larger of the two numbers), `tolerance_abs` absolute; with
both, either is enough. They apply to every number, whole and fixed decimal included. A test's replaces the
defaults', a column's replaces the test's. With neither, decimal numbers (doubles) are equal to about 9 significant
digits and whole and fixed decimal numbers exactly.

**Errors.** The codes are tess's: `dax-syntax`, `dax-invalid` (an unknown name, the wrong arguments), `dax-error`
(an error DAX raises: `ERROR ( )`, a failed conversion) locally; on a server every query error is `query-failed`,
so match the `message` in a suite that runs on both. A test the local engine refuses is `unsupported` even when it
expects an error, unless it expects `error: not-supported` by name.

**Filters** build the filter context the test is evaluated in, as a visual's filters would:

```yaml
filters:
  Date[Year]: 2024
  "'Product Category'[Name]": [Bikes, Helmets]      # a name with a space needs its quotes, and the YAML key its own
  Sales[Order Date]: { dax: "DATE ( 2024, 1, 31 )" }   # any DAX scalar expression
```

Each is a `TREATAS ( { values }, column )` filter of a `CALCULATE` around the expression (for a query, of a
`CALCULATETABLE` around each `EVALUATE`'s table; `DEFINE` and `ORDER BY` stay). `tess test list --json` shows the
query each test runs as. The local engine refuses `SUMMARIZECOLUMNS` under an outer filter, so a filtered query
that uses it is `unsupported` there: put the filter in the query (`SUMMARIZECOLUMNS ( ..., TREATAS ( ... ), ... )`).

**Matrix.** A mapping of names to values runs the test for every combination; a list of mappings runs those
combinations, which lets each carry its expected value (as in the example). `{name}` is replaced in every value of
the test (the query, a query file's text, filters, expectations): a value that is exactly `"{name}"` becomes the
variable's value with its type; inside a longer text it becomes the value's text. Only declared names are replaced,
so `{ 1, 2 }` in a query is safe. Each test is named `name [year=2024, ...]`, unless the name has a placeholder of
its own.

**Inline rows** (`tables`) give a table's rows in the suite, as the [fixture JSON format](fixture-data.md#the-format) has them,
instead of a file: for small tests whose data should sit next to the assertion. They go on top of the `data`
directory if there is one (a table given inline replaces its file); with no directory, every other table is empty.
A test's `tables` replace the suite's for the same table. They go through the same loader and checks as a fixture
directory, and a problem stops the run naming the suite file, the table's line and the row.

## `test run`

`tess test run <model> [suites...]`, with:

| Option | |
|---|---|
| `--data <dir>` | the fixture directory, instead of the suites' `data` (a test's own `data` stays). Model files only |
| `--filter <text>` | only tests whose name contains the text, or that have it as a tag (repeatable, comma-separated) |
| `--tag <tag>` | only tests with the tag |
| `--fail-fast` | stop at the first failure or error; the rest are skipped |
| `--fail-on error\|warning`, `--strict` | see [outcomes](#outcomes-and-the-exit-code) |
| `--ci github\|azdo` | annotations on stderr, as for `validate` |
| `--trx <file>`, `--junit <file>` | also write the results as a Visual Studio TRX file and/or JUnit XML, for CI dashboards |
| `--force` | let `--trx` / `--junit` replace a file that isn't a test report (they replace an earlier report freely) |

`--json` writes the [findings envelope](ci.md#findings-json-and-ci-annotations) with `"command": "test run"`, plus
`"source"`, `"suites": [{name, file, summary, tests: [{name, outcome, line, tags, durationMs, code?, message?,
differences?, query}]}]`, `"invalidSuites": [{file, validationErrors}]` and `"testSummary": {passed, failed,
errored, unsupported, skipped, invalidSuites, durationMs, total}`. In the report files an unsupported test is "not
executed" (TRX) or skipped (JUnit), and a failure with `--strict`.

**Against a server** (`xmla:<server>;<database>`) each test's query is sent as it is, over one connection, with the
credentials of [connected mode](xmla.md#connected-mode-connect-query-deploy-remote-models). The server has its own data:
`--data` is refused, and a test with its own `data` or `tables` is skipped, with the reason. To test a deployed
model against known data, deploy it with the fixture inside (`tess data embed`).

## `test init`

`tess test init <model>` writes `.tess-tests/measures.test.yaml` next to the model (`-o <file>` for another place;
the name must end in `.test.yaml`): one test per measure, `pending: true`, to fill in. `--data <dir>` writes the
suite's `data` (relative to the suite). `--snapshot` writes snapshot tests instead of pending ones, which is the
quick way to guard a refactor: `init --snapshot`, `snapshot --save`, change the model, `run`. It never overwrites a
file (exit 4). A suite of pending tests doesn't pass: `run` exits 1 until at least one test reaches a verdict.

## `test snapshot`

A test with `expect: { snapshot: true }` passes when its result is the one recorded in `<suite>.snapshot.json`,
next to `<suite>.test.yaml`. `tess test snapshot <model> [suites...]` evaluates those tests and shows what
recording would change (`+` new, `~` changed with the differences, `=` unchanged, `-` recorded but no longer a
test, `!` can't be recorded); **it is a dry run unless `--save` is given**. `--filter` / `--tag` record some tests
and leave the other snapshots alone.

The file is meant for version control: tests by name, a row per line, rows sorted unless the query has an `ORDER
BY`, LF line ends, no timestamps; recording an unchanged model changes no byte. A test that errors or is
unsupported is never recorded (exit 1, and what was recorded stays). In `run`, a snapshot test with nothing
recorded fails, saying how to record it. A snapshot compares types too (a whole number that became a decimal is a
difference), unless the test has a tolerance, which compares numbers by value.

## `test compare`

`tess test compare <model> --against <other> [suites...]` runs each test's query on both and reports the tests
whose results differ: `<other>` is another version of the model files (the refactor check; `--against-data` gives
it another fixture) or an `xmla:` model (the local engine against the deployed model, or `xmla:` against `xmla:`
for two deployments). The tests' expectations play no part, except that a test expecting an error passes when both
sides fail. A difference reads "*against's value*, expected *the model's value*". `--tolerance` / `--tolerance-abs`
replace the tests' tolerances; a value that differs in type only (a whole number 1, a decimal 1.0) is not a
difference unless `--types` is given. The other options, the JSON (`"command": "test compare"`, with `"against"`)
and the exit codes are those of `run`; a test either side's local engine refuses is `unsupported`.
An unordered comparison that exceeds the row-matching work limit reports an inconclusive comparison and fails the test.

## Differences from `te test`

- **It runs offline.** `te test` needs a deployed model; `data`, `tables`, `filters`, `measure`, `expression`,
  `assert`, `table`, `file`, `snapshot`, `pending` and the `unsupported` outcome are tess's.
- The model is a positional argument, as everywhere in tess, not `--model` / `-s` / `-d`; a suite's `connection`
  is not used.
- Suites live in `.tess-tests`, not `.te-tests` (give `.te-tests` as an argument to run a te suite).
- Snapshots are per test and live next to the suite, written by `snapshot --save`; te's `snapshot -o baseline.json`
  / `--diff` over every measure is `init --snapshot` plus `snapshot`.
- `compare --against` takes model files as well as servers; te's `--source-a` / `--source-b` take two deployed
  models.
- Finding codes `TEST_FAIL`, `TEST_ERROR` and `TEST_SUITE_INVALID` are te's; `TEST_UNSUPPORTED` and `TEST_NONE_RAN`
  are tess's. `testSummary` adds `unsupported`.
- **BLANK is not 0.** `expect: 0` fails for a BLANK result (`COUNTROWS` of no rows is BLANK, not 0): write
  `expect: null`, or `COUNTROWS ( ... ) + 0` in the query. te's own example suite expects `0` from such a query, so
  te may be laxer here; tess stays strict because a measure that went BLANK is the regression a test is for.
- Not there: `te test spec` and the JSON Schema for editors, and `test use`.
- The suite format was matched to te's published reference, not to its binary: `matrix` (both shapes are accepted
  here) and `compare` (read and ignored) are the parts that reference doesn't spell out.
