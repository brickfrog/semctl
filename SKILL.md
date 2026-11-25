---
name: sm
description: Drive sm (semctl), an offline CLI for Power BI / Analysis Services semantic models (PBIP, TMDL, .bim, database.json). Use it to inspect, validate, lint, format, edit and test a model, and to run DAX on fixture data with the local engine, all without a network. Read this before running sm from an agent or a script.
---

# Driving sm

sm reads and edits semantic models on disk: a PBIP project or `X.SemanticModel` folder, a TMDL folder, a
`.bim` file or a Tabular Editor `database.json` folder. Run `sm --help` and `sm <command> --help` for every
option. See the [command reference](https://github.com/brickfrog/semctl/tree/master/docs) for details.

## Rules that matter to an agent

- **Use `--json` (`-j`).** Every command writes one JSON document to stdout, errors included:
  `{"error": {"code", "message"}}`, plus `"suggestions"` when sm has close matches ("did you mean"). The one
  exception is `format` on loose DAX (`-`, `--expr`, a .dax file), which writes `{"success", "formatted",
  "errors": [{"line", "column", "message"}]}` like `te util format-dax`, and exits 3 when the DAX can't be read. Keys with
  no value are left out, never `null` (`null` appears only where it is the value, such as a BLANK cell). Notes and
  warnings go to stderr. Output is plain text: no colour, no progress redrawn in place.
- **Branch on the exit code, then read the JSON.**

  | Code | Meaning |
  |---|---|
  | 0 | success |
  | 1 | the "negative" result: `find` found nothing, `diff` found differences, `validate`/`bpa`/`lint` found issues at or above `--fail-on`, `test run` had a failed or errored test or none reached a verdict, `format --check` found unformatted DAX, or an edit's `--save` refused because it would introduce errors |
  | 2 | usage error (bad arguments, an invalid name, an option the kind doesn't take) |
  | 3 | input error: model not found or not loadable, an object that doesn't exist or can't be created/removed/moved, malformed DAX |
  | 4 | output error: the destination isn't empty (use `--force`), a write failed |
  | 5 | a server couldn't be reached, refused the credentials, or reported an error |
  | 70 | internal error (a semctl bug) |

- **Edits are dry runs.** `rename`, `set`, `add`, `remove`, `move`, `script apply`, `translations import`,
  `perspective add|remove`, `lint --fix`, `bpa rules ...` and `format` on a model show the change and write
  nothing. Add `--save` to write the model back in place, or `--save-to <path>` to write elsewhere (plus `--force`
  to overwrite a non-empty destination). `--save` refuses a change that introduces errors (exit 1) unless you pass
  `--force`. Look at the dry run first, then save. `--stat` and `--name-only` shorten the diff. With `--json`, the
  change set includes `dryRun`, `introducedErrors`, `warnings`, `notes`, and `saved` once something was written.
- **No network.** sm makes no network call unless the command names a server: an `xmla:<server>;<database>`
  model argument (any command), `active:` or `profile:<name>` when that connection is a server, `connect`, the
  server `deploy` compares with and deploys to (even on a dry run), `bpa --allow-external-rules` with a rule URL,
  `schemacheck --source sql:<server>;<database>`, or `pkg` with `--from daxlib` (or `SM_PKG_SOURCE=daxlib`),
  which reaches only https://api.daxlib.org. There's no telemetry and no update check. `format` formats locally
  and never calls daxformatter.com. A `query`, `preview`, `test run` or `vertipaq` with `--data` stays on the
  machine, and so does `vertipaq --import`.
- **Credentials come from the environment** (`SM_XMLA_TOKEN`, `SM_XMLA_CLIENT_ID` / `_TENANT_ID` /
  `_CLIENT_SECRET`, ...; for `schemacheck`'s SQL source `SM_SQL_TOKEN`, `SM_SQL_USER` / `SM_SQL_PASSWORD`), never from the command line: a secret in an argument is refused. sm never prints a
  secret.
- **Object references** paste between commands: `measure:Sales/Margin`, `column:Sales/Amount`, `table:Date`,
  `calendar:Date/Fiscal`, or DAX forms (`'Sales'[Amount]`, `[Margin]`). Findings carry an `objectPath` in the
  first form.

## Common tasks

```sh
sm info ./Sales.pbip --json                        # what's in the model
sm list ./Sales.pbip -k measure --json             # objects, filtered by kind (-k) or table (-t)
sm get ./Sales.pbip "[Margin]" --json              # one object
sm deps ./Sales.pbip "[Margin]" --json             # what it uses, and what uses it
sm validate ./Sales.pbip --json                    # broken references, cycles, unknown functions, ... (exit 1: issues)
sm bpa ./Sales.pbip --json                         # Best Practice Analyzer (built-in rules work offline)
sm lint ./Sales.pbip --json                        # DAX code actions; --fix --save rewrites them
sm format ./Sales.pbip --check --json              # exit 1 if any DAX isn't formatted
sm format ./Sales.pbip --save                      # format every expression, write it back
sm format query.dax --save                         # format a DAX file in place
sm add ./Sales.pbip measure:Sales/Margin -e "[Revenue] - [Cost]" --json          # dry run
sm add ./Sales.pbip measure:Sales/Margin -e "[Revenue] - [Cost]" --save
sm rename ./Sales.pbip "[Revenue]" "Net Revenue" --save                           # fixes every reference
sm init ./New.SemanticModel --json                 # a new model; re-running it on the same model exits 0
```

`schemacheck` compares the model's columns with their source's schema (exit 1: a missing column or a query error;
new and retyped columns are warnings). Offline it reads a schema file or a fixture folder; `sql:` asks the server
to describe each table's query (only exact `Sql.Database` + navigation M, or SQL query partitions; the rest is
`SCHEMA_TABLE_SKIPPED`). `--update` applies new columns and changed types (dry run unless `--save`):

```sh
sm schemacheck ./Sales.pbip --source ./schema.json --json        # {"tables": {"Sales": [{"name", "type"}]}}
sm schemacheck ./Sales.pbip Sales --source "sql:srv.database.windows.net;Db" --json
sm schemacheck ./Sales.pbip --source ./schema.json --update      # then --save; --drop-removed-columns to remove
```

The local DAX engine answers queries offline over fixture data (a folder of CSV/JSON files, one per table):

```sh
sm data init ./Sales.pbip ./fixture                 # scaffold the fixture files
sm data check ./Sales.pbip ./fixture --json         # does the data fit the model?
sm query ./Sales.pbip --data ./fixture "EVALUATE SUMMARIZECOLUMNS ( 'Date'[Year], \"Sales\", [Sales] )" --json
sm preview ./Sales.pbip Customer --data ./fixture --rows 5 --json
sm query ./Sales.pbip --coverage --json             # which measures the engine can evaluate
sm test init ./Sales.pbip                           # scaffold a .sm-tests/*.test.yaml suite
sm test run ./Sales.pbip --data ./fixture --json    # run the suites offline (exit 1: a test failed)
```

The engine never returns a wrong answer: anything it doesn't implement exactly is an error with the code
`not-supported`, naming what it refused. Treat that as "unknown", not as a failure of the model.

VertiPaq statistics (rows, cardinality, sizes, referential-integrity violations), as VertiPaq Analyzer shows them:

```sh
sm vertipaq ./Sales.pbip --data ./fixture --all --json      # offline: counts exact, sizes estimated
sm vertipaq --import stats.vpax --columns --top 20 --json   # a .vpax from DAX Studio, Bravo or Tabular Editor 3
sm vertipaq ./Sales.pbip --import stats.vpax --annotate --save   # Vertipaq_* annotations, for BPA rules
sm bpa ./Sales.pbip --vpax stats.vpax --json                # the same statistics, in memory only
sm vertipaq ./Sales.pbip --data ./fixture --export out.vpax [--obfuscate]
```

A statistic sm couldn't read is left out of the JSON (never 0). `"estimated": true` means the sizes are
estimates from fixture data, not VertiPaq's: compare them with each other, not with a server. `--obfuscate` writes
`<name>.dict` next to the file; it holds the real names, so don't send it with the `.vpax`.

A session holds one model in memory and stages edits until `save` (the same staged model `sm mcp` serves).
Pipe a script into it: it stops at the first failing command with that command's exit code, and input that ends
with unsaved edits fails with exit 1 (`unsaved-changes`), so end with `save` or `exit --force`:

```sh
printf 'rename [Revenue] "Net Revenue"\nvalidate --fail-on error\nstatus --json\nsave\n' | sm interactive ./Sales.pbip
```

Inside it, commands take no model (`get [Margin] --json`); `--dry-run` shows an edit without staging it; `undo`,
`revert`, `diff` and `status` work on what is staged; `-` (stdin) is refused. Words group with `"..."` (a doubled
`""` is a literal quote); `[Total]` and `'Sales'[Total]` stay whole.

`remove` (and `bpa --fix` Delete(), `mcp` remove) refuses to delete what a PBIR report next to the model uses:
exit 3, error code `used-by-report`, the visuals in `usages`. Change the report, or pass `--force` knowingly.

Profiles and the active connection are local state, never secrets: `sm connect <model or server>` makes it this
terminal's active connection, named `active:` as a model argument; `sm profile set <name> --model <path>` (or
`--server ... --database ...`) names one, used as `profile:<name>` or with `sm connect --profile <name>`. Set
`SM_SESSION=<name>` when your commands don't run from one terminal (an agent's shell may start a new one each
time), and `SM_STATE_DIR` / `SM_CONFIG` to keep the state somewhere of your own.

DAX function packages from daxlib.org, installed as Tabular Editor 3 installs them (a source is always named):

```sh
sm pkg list ./Sales.pbip --json                                  # installed packages, changed functions
sm pkg search svg --from ./daxlib --json                         # a local daxlib checkout or .daxpkg folder
sm pkg install ./Sales.pbip DaxLib.SVG --from ./daxlib --json    # dry run; --save writes
sm pkg update ./Sales.pbip --from daxlib --json                  # `daxlib` = online (daxlib.org); --offline refuses it
sm pkg remove ./Sales.pbip DaxLib.SVG --save                     # refused while DAX calls its functions
```

C# scripts against the model's TOM (`Model`), shown as a diff like any edit; output goes to stderr:

```sh
sm csharp ./Sales.pbip fix.csx --json               # dry run; --save writes; a compile error names the script's line
```

## Good habits

- Run `validate` after an edit you saved, and `format --check` / `lint` before committing DAX.
- Prefer `--save-to` to a scratch path when you're unsure, then `sm diff <before> <after> --json`.
- Quote references and DAX for the shell; `-` reads a value from stdin where an option documents it
  (`format -`, `add ... -e -`, `query <model> -`).
