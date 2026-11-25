# Command-line reference

## Usage

```sh
sm init  ./Sales.SemanticModel                # new, empty model (PBIP; .bim / TMDL by path)
sm info  ./MyReport.pbip                       # summary: compatibility level, object counts
sm list  ./MyReport.pbip -k measure -t Sales   # one object reference per line
sm list  ./model.bim --name '*Margin*' --json  # machine-readable
sm get   ./MyReport.pbip "[Total Sales]"         # one object, as TMDL
sm get   ./MyReport.pbip "'Sales'[Total]" -p expression   # just the DAX
sm get   ./MyReport.pbip Sales --json          # TOM JSON (add --children for columns etc.)
sm find  ./MyReport.pbip "Budget Rate"          # names, DAX, M, descriptions, annotations...
sm find  ./MyReport.pbip 'CALCULATE\s*\(' -E -k measure --in expression
sm diff  ./main/Sales.SemanticModel ./branch/Sales.SemanticModel   # object-level changes
sm diff  ./server-export.bim ./Sales.pbip --ignore lineageTag --json
sm deps  ./MyReport.pbip "[Gross Margin %]"     # what it uses, and what uses it
sm deps  ./MyReport.pbip "Date[Date]" --referenced-by --depth 0
sm deps  ./MyReport.pbip --unused               # hidden objects nothing uses
sm validate ./MyReport.pbip                     # broken references, cycles, ... (exit 1 on errors)
sm validate ./model.bim --fail-on warning --rule unresolved-reference --json
sm rename ./MyReport.pbip "[Revenue]" "Net Revenue"          # preview: the rename + every DAX reference it fixes
sm rename ./MyReport.pbip "[Revenue]" "Net Revenue" --save   # ... and write it back
sm set   ./MyReport.pbip "[Revenue]" -p formatString='#,0.00' -p isHidden=true --save   # set properties
sm get   ./MyReport.pbip "[Revenue]" --properties   # what `set` can write there, with types and values
sm add   ./MyReport.pbip measure:Sales/Margin -e "[Revenue] - [Cost]" --save   # create an object
sm add   ./MyReport.pbip relationship --from "'Sales'[DateKey]" --to "'Date'[DateKey]" --save
sm remove ./MyReport.pbip "Sales[Old Key]" "[Unused]" --save  # refused while other DAX uses them
sm move   ./MyReport.pbip "[Revenue]" "Measures" --save       # rewrites 'Sales'[Revenue] references
sm bpa   ./MyReport.pbip                       # Best Practice Analyzer: built-in rules + the model's own
sm bpa   ./model.bim -r BPARules.json --min-severity 2 --trx bpa.trx   # TE rule files, CI output
sm bpa rules list ./MyReport.pbip              # the rules in effect, and where each comes from
sm bpa rules ignore ./MyReport.pbip SM_DIVIDE "[Ratio]" --save   # suppress one finding (TE's annotation)
sm script export ./MyReport.pbip -t Sales -o sales.dax   # DAX objects as a DAX script (TE3's format)
sm script apply  ./MyReport.pbip sales.dax --save        # ... edited, and applied back
sm convert ./model.bim ./Sales.SemanticModel   # .bim -> PBIP (TMDL + definition.pbism + .platform)
sm convert ./Sales.SemanticModel ./model.bim   # PBIP -> .bim
sm convert ./tmdl ./tmdl --force                # re-serialize in place (keeps its line endings)
sm translations export ./MyReport.pbip --culture da-DK -o da-DK.csv   # a file for the translator
sm translations import ./MyReport.pbip da-DK.csv --save              # ... and the filled-in file back
sm perspective add ./MyReport.pbip Finance Sales "[Budget]" --save   # perspective membership
sm perspective list ./MyReport.pbip Finance
sm lint  ./MyReport.pbip                       # DAX code actions: unused VARs, unqualified columns, ...
sm lint  ./MyReport.pbip --fix --save          # ... and rewrite what can be fixed safely
sm format --expr "calculate(sum(Sales[Amount]),all('Date'))"   # format DAX, SQLBI style, offline
sm format ./MyReport.pbip --save               # ... every DAX expression in a model (--check for CI)
sm interactive ./MyReport.pbip                 # a shell: commands without the model, edits staged until `save`
sm connect "powerbi://api.powerbi.com/v1.0/myorg/Finance"       # check a server connection, list its models; then `active:`
sm profile set prod --server "powerbi://.../Finance" --database Sales   # named connections (no secrets)
sm info  "xmla:powerbi://api.powerbi.com/v1.0/myorg/Finance;Sales"   # read-only commands on a deployed model
sm query "xmla:powerbi://api.powerbi.com/v1.0/myorg/Finance;Sales" "EVALUATE 'Date'" --json
sm deploy ./MyReport.pbip "powerbi://api.powerbi.com/v1.0/myorg/Finance" Sales   # dry run; --execute to deploy
sm refresh "xmla:powerbi://api.powerbi.com/v1.0/myorg/Finance;Sales" Orders --type full   # dry run; --execute to refresh
sm data init ./MyReport.pbip ./fixtures --rows 100   # fixture data for the local DAX engine: CSV per table
sm data check ./MyReport.pbip ./fixtures       # ... checked against the model's columns, types and relationships
sm query ./MyReport.pbip --data ./fixtures "EVALUATE SUMMARIZECOLUMNS('Date'[Year], \"Sales\", [Total])"   # offline
sm query ./MyReport.pbip --coverage            # which measures the local engine can evaluate, and what the rest need
sm test init ./MyReport.pbip --data ./fixtures  # a test suite to fill in: one test per measure
sm test run ./MyReport.pbip --ci github         # run .sm-tests/*.test.yaml offline, over the fixture data
sm schemacheck ./MyReport.pbip --source "sql:myserver.database.windows.net;SalesDb"   # columns vs. the source's schema
sm validate ./MyReport.pbip --watch            # again on every save, until Ctrl+C (also lint, bpa, test run)
sm completion bash > ~/.local/share/bash-completion/completions/sm   # shell completion (zsh, fish, pwsh too)
sm vertipaq ./MyReport.pbip --data ./fixtures --columns --top 10   # VertiPaq statistics; sizes estimated offline
```

## Shell completion (`completion`)

`sm completion bash|zsh|fish|pwsh` prints a completion script for that shell. It knows every command,
subcommand and option (it is generated from sm's own argument definitions), the values of options that take one
of a fixed list (`--fail-on`, `--ci`, `--format`, `--layer`, ...), and completes file names where a command takes a
path. For a model's objects (`get <model> measure:<Tab>`, `list <model> --table <Tab>`, `query <model> --role <Tab>`,
perspectives) it asks sm, which reads the model given on the command line. That only ever reads local files: for
an `xmla:` model it offers nothing, and it never prints errors into your shell.

sm writes nothing to your shell's configuration: install the script yourself, and generate it again after
upgrading sm.

| Shell | Install |
|---|---|
| bash | `sm completion bash > ~/.local/share/bash-completion/completions/sm` (with the bash-completion package), or add `source <(sm completion bash)` to `~/.bashrc` |
| zsh | `sm completion zsh > "${fpath[1]}/_sm"` (any folder on `$fpath`, before `compinit` runs), or add `source <(sm completion zsh)` to `~/.zshrc` after `compinit` |
| fish | `sm completion fish > ~/.config/fish/completions/sm.fish` |
| PowerShell | add `sm completion pwsh \| Out-String \| Invoke-Expression` to your `$PROFILE` (PowerShell 7) |

Object names with spaces and symbols are quoted or escaped for the shell. Completing an object loads the model,
so it takes about as long as `sm list` on it (under a second for a typical model).

## JSON and exit codes

`--json` (or `-j`) writes one JSON document to stdout, errors included (`{"error": {"code", "message"}}`).

**Plain output:** sm writes plain text only: no colour, no spinner, no line redrawn in place. Progress (`refresh
--progress`) is one line per event on stderr, so logs read the same in CI as in a terminal. Should sm ever
decorate output, it does so only on a terminal, and never when `CI` or `NO_COLOR` is set or `TERM` is `dumb`
(`Output.plainOutput`).

**"Did you mean":** when something isn't found or recognised, the error names the closest matches (within about a
third of the length in edits, ignoring case; swapped letters count as one edit), at most three, and JSON lists them
as the error's `suggestions`. That covers an object reference (any form: `measure:Sales/Totl`, `'Sales'[Totl]`,
`[Totl]`, `Sles`), a command or subcommand (`sm vaildate`), an option (`--fial-on`), and option values: `--ci`,
`--fail-on`, `--min-severity`, `--kind`, and `--rule` for `validate`, `lint` and `data check`. `bpa rules show`
does the same for a rule ID, and `bpa rules ignore` warns with one. The message alone carries the hint for
`--format`, `--eol`, `refresh --type` / `--progress`, `bpa rules list --layer` and `test --ci`.

A key with no value is left out rather than written as `null`, in every command (as in the Tabular Editor CLI's
findings): `list`'s `table`, `hidden` and `detail`, `deps --unused`'s `note`, `info`'s `semanticModelFolder`, an
edit's `saved` on a dry run, and so on. `null` appears only where it is itself the value: a property that holds
nothing (`get --properties`'s `value`, a change's `before` / `after`) and a BLANK cell in `query` results.

| Code | Meaning |
|---|---|
| 0 | success |
| 1 | the "negative" result of the Unix tool a command mirrors: `find` found nothing (like grep); `diff` found differences (like diff); `bpa`/`validate`/`lint`/`schemacheck` found issues at or above `--fail-on` (default `error`); `test run` had a failed or errored test, an invalid suite, or no test that reached a verdict; `test compare` found differences; `format --check` found DAX that isn't formatted; an edit's `--save` refused because it would introduce errors |
| 2 | usage error, including invalid arguments for an editing command: an invalid new name (`rename`, `add`), a property `set` can't write or a value it can't take, or an option `add` doesn't take for that kind |
| 3 | input error: model not found, unrecognised, ambiguous or failed to load. For the editing commands also a name that's taken, a missing object or parent, an object that can't be created, removed or moved there, an object others still refer to (`remove`), an edit that would change what other references mean (`rename`, `move`), a translation file that can't be applied (`translations import`), or an object a perspective can't hold. For `script`, also a script that can't be read or applied, or an object a script can't hold. For `bpa`, also a rule file you named that can't be read; for `bpa rules`, a rule ID that isn't there (or already is, for `add`) or an object that can't carry an ignore annotation. For `format`, DAX that can't be read (malformed). For `schemacheck`, a `--source` that can't be used (a missing or malformed schema file, a bad `sql:` address) |
| 4 | output error (destination not empty, write failed); `deploy --execute` on a database that exists, without `--overwrite` |
| 5 | a server couldn't be reached, refused the credentials (or none are configured), has no such database, or reported an error (a failed `query`, `deploy` or `refresh`), including `schemacheck`'s SQL server |
| 70 | internal error |
