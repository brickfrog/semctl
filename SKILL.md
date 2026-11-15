---
name: tess
description: Drive tess (tessera), an offline CLI for Power BI / Analysis Services semantic models (PBIP, TMDL, .bim, database.json). Use it to inspect, validate, lint, format, edit and test a model, and to run DAX on fixture data with the local engine, all without a network. Read this before running tess from an agent or a script.
---

# Driving tess

tess reads and edits semantic models on disk: a PBIP project or `X.SemanticModel` folder, a TMDL folder, a
`.bim` file or a Tabular Editor `database.json` folder. Run `tess --help` and `tess <command> --help` for every
option; the README has the details.

## Rules that matter to an agent

- **Use `--json` (`-j`).** Every command writes one JSON document to stdout, errors included:
  `{"error": {"code", "message"}}`, plus `"suggestions"` when tess has close matches ("did you mean"). The one
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
  | 70 | internal error (a tessera bug) |

- **Edits are dry runs.** `rename`, `set`, `add`, `remove`, `move`, `script apply`, `translations import`,
  `perspective add|remove`, `lint --fix`, `bpa rules ...` and `format` on a model show the change and write
  nothing. Add `--save` to write the model back in place, or `--save-to <path>` to write elsewhere (plus `--force`
  to overwrite a non-empty destination). `--save` refuses a change that introduces errors (exit 1) unless you pass
  `--force`. Look at the dry run first, then save. `--stat` and `--name-only` shorten the diff. With `--json`, the
  change set includes `dryRun`, `introducedErrors`, `warnings`, `notes`, and `saved` once something was written.
- **No network.** tess makes no network call unless the command names a server: an `xmla:<server>;<database>`
  model argument (any command), `connect`, the server `deploy` compares with and deploys to (even on a dry run),
  or `bpa --allow-external-rules` with a rule URL. There's no telemetry and no update check. `format` formats
  locally and never calls daxformatter.com. A `query`, `preview` or `test run` with `--data` stays on the
  machine.
- **Credentials come from the environment** (`TESS_XMLA_TOKEN`, `TESS_XMLA_CLIENT_ID` / `_TENANT_ID` /
  `_CLIENT_SECRET`, ...), never from the command line: a secret in an argument is refused. tess never prints a
  secret.
- **Object references** paste between commands: `measure:Sales/Margin`, `column:Sales/Amount`, `table:Date`,
  `calendar:Date/Fiscal`, or DAX forms (`'Sales'[Amount]`, `[Margin]`). Findings carry an `objectPath` in the
  first form.

## Common tasks

```sh
tess info ./Sales.pbip --json                        # what's in the model
tess list ./Sales.pbip -k measure --json             # objects, filtered by kind (-k) or table (-t)
tess get ./Sales.pbip "[Margin]" --json              # one object
tess deps ./Sales.pbip "[Margin]" --json             # what it uses, and what uses it
tess validate ./Sales.pbip --json                    # broken references, cycles, ... (exit 1: issues)
tess bpa ./Sales.pbip --json                         # Best Practice Analyzer (built-in rules work offline)
tess lint ./Sales.pbip --json                        # DAX code actions; --fix --save rewrites them
tess format ./Sales.pbip --check --json              # exit 1 if any DAX isn't formatted
tess format ./Sales.pbip --save                      # format every expression, write it back
tess format query.dax --save                         # format a DAX file in place
tess add ./Sales.pbip measure:Sales/Margin -e "[Revenue] - [Cost]" --json          # dry run
tess add ./Sales.pbip measure:Sales/Margin -e "[Revenue] - [Cost]" --save
tess rename ./Sales.pbip "[Revenue]" "Net Revenue" --save                           # fixes every reference
tess init ./New.SemanticModel --json                 # a new model; re-running it on the same model exits 0
```

The local DAX engine answers queries offline over fixture data (a folder of CSV/JSON files, one per table):

```sh
tess data init ./Sales.pbip ./fixture                 # scaffold the fixture files
tess data check ./Sales.pbip ./fixture --json         # does the data fit the model?
tess query ./Sales.pbip --data ./fixture "EVALUATE SUMMARIZECOLUMNS ( 'Date'[Year], \"Sales\", [Sales] )" --json
tess preview ./Sales.pbip Customer --data ./fixture --rows 5 --json
tess query ./Sales.pbip --coverage --json             # which measures the engine can evaluate
tess test init ./Sales.pbip                           # scaffold a .tess-tests/*.test.yaml suite
tess test run ./Sales.pbip --data ./fixture --json    # run the suites offline (exit 1: a test failed)
```

The engine never returns a wrong answer: anything it doesn't implement exactly is an error with the code
`not-supported`, naming what it refused. Treat that as "unknown", not as a failure of the model.

A session holds one model in memory and stages edits until `save` (the same staged model `tess mcp` serves).
Pipe a script into it: it stops at the first failing command with that command's exit code, and input that ends
with unsaved edits fails with exit 1 (`unsaved-changes`), so end with `save` or `exit --force`:

```sh
printf 'rename [Revenue] "Net Revenue"\nvalidate --fail-on error\nstatus --json\nsave\n' | tess interactive ./Sales.pbip
```

Inside it, commands take no model (`get [Margin] --json`); `--dry-run` shows an edit without staging it; `undo`,
`revert`, `diff` and `status` work on what is staged; `-` (stdin) is refused. Words group with `"..."` (a doubled
`""` is a literal quote); `[Total]` and `'Sales'[Total]` stay whole.

`remove` (and `bpa --fix` Delete(), `mcp` remove) refuses to delete what a PBIR report next to the model uses:
exit 3, error code `used-by-report`, the visuals in `usages`. Change the report, or pass `--force` knowingly.

Profiles and the active connection are local state, never secrets: `tess connect <model or server>` makes it this
terminal's active connection, named `active:` as a model argument; `tess profile set <name> --model <path>` (or
`--server ... --database ...`) names one, used as `profile:<name>` or with `tess connect --profile <name>`. Set
`TESS_SESSION=<name>` when your commands don't run from one terminal (an agent's shell may start a new one each
time), and `TESS_STATE_DIR` / `TESS_CONFIG` to keep the state somewhere of your own.

## Good habits

- Run `validate` after an edit you saved, and `format --check` / `lint` before committing DAX.
- Prefer `--save-to` to a scratch path when you're unsure, then `tess diff <before> <after> --json`.
- Quote references and DAX for the shell; `-` reads a value from stdin where an option documents it
  (`format -`, `add ... -e -`, `query <model> -`).
