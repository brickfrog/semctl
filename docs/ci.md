# Continuous integration

## Findings JSON and CI annotations

`validate`, `bpa`, `lint`, `test` and `schemacheck` report findings in one shape, the one the Tabular
Editor CLI uses, so tooling written for one reads the other:

```json
{ "command": "validate", "durationMs": 361,
  "summary": { "errors": 1, "warnings": 0, "info": 0, "total": 1 },
  "findings": [ { "severity": "error", "source": "validate", "code": "unresolved-reference",
                  "message": "'Nope'[Revenue] does not exist: there is no table 'Nope'",
                  "object": "Gross Margin %", "objectType": "Measure",
                  "objectPath": "measure:Invoiced Sales/Gross Margin %",
                  "expressionPosition": { "property": "expression", "lineNumber": 5, "column": 26 },
                  "fixable": false } ],
  "valid": false }
```

How the fields work:
- **Severity:** `error`, `warning` or `info`.
- **`code`:** stable. It's the `validate` rule id, the BPA rule ID, or the `lint` rule id (`DI001`...).
- **`objectPath`:** a semctl reference, so it pastes into any command.
- **`objectType`:** one of `Model`, `Table`, `Column`, `Measure`, `Hierarchy`, `Level`, `Partition`,
  `CalculationItem`, `Calendar`, `Relationship`, `Role`, `Perspective`, `Culture`, `NamedExpression`, `Function`,
  `DataSource`; and, for `test`, `Test` and `TestSuite` (which have no `objectPath`: they aren't model objects).
- **Optional keys** (`objectPath`, `expressionPosition`, `ruleName`, `category`) are left out rather than written
  as `null`. Where the position is only known to the line, `expressionPosition` has no `column`.
- **`fixable`:** whether `sm lint --fix` fixes it. Always `false` for `validate` and `bpa`, which don't
  apply fixes.

`--ci github` writes one `::error` / `::warning` / `::notice` line per finding to stderr. `--ci azdo` writes
`##vso[task.logissue ...]` lines, with info findings as plain lines so info-only runs stay green. Each line carries
the code as its title.

## SARIF (`--sarif`)

`validate`, `bpa`, `lint`, `data check` and `schemacheck` take `--sarif <file>`: they also write their findings as a
[SARIF 2.1.0](https://docs.oasis-open.org/sarif/sarif/v2.1.0/sarif-v2.1.0.html) log, the format code scanning
UIs (GitHub's among them) read. Like `--trx`, it replaces the file, and it is written on a clean run too (with no
results). It doesn't change the command's own output or exit code.

- **One run, one result per finding**, in the command's own order (`validate` lists every issue, not just the root
  causes). `level` is `error`, `warning` or `note` (for info). `message` starts with the object's reference.
- **Rules** (`tool.driver.rules`): the rules that ran, with their description and, for `bpa` and `lint`, their
  severity and category (as a tag). `helpUri` only where the rule itself has a URL (a BPA rule's `LearnMoreUrl`);
  sm adds none.
- **Locations:** the file and line where the object is declared: the TMDL file and the line of `measure 'X'`, or
  the line of the object's `"name"` in a .bim. A `data check` issue in a fixture file points at its line there.
  Each result also has a logical location whose `fullyQualifiedName` is the object's semctl reference. When the
  declaration can't be found (or a name is declared twice), the result points at the nearest enclosing object (a
  measure's table), else line 1 of the model file, and its `properties.exactLocation` is `false`. A remote
  (`xmla:`) model has no files, so its results have only the logical location.
- **Paths** are relative to the repository root (the nearest folder above the model that holds `.git`, else the
  current folder), with `/` and percent-encoding, under `uriBaseId` `SRCROOT`; a file outside that folder gets an
  absolute `file:` URI. No machine-specific paths otherwise.
- **Deterministic:** no timestamps or machine names; the same model gives the same bytes. sm writes no
  `partialFingerprints`: GitHub's upload action computes its own.
- `bpa`: findings an object's ignore annotation hides are left out, as they are from `findings` (GitHub doesn't
  document SARIF suppressions, so they could show as open alerts); rules that couldn't run are
  `toolConfigurationNotifications`. `properties` on a result carry `objectType`, `fixable`, `exactLocation` and,
  where known, `expressionPosition` (the line within the expression; the region is the declaration's line).

sm only writes the file; uploading it is your CI's job. With GitHub Actions (sm exits 1 on errors, so the upload
step runs `if: always()`):

```yaml
permissions:
  contents: read
  security-events: write   # to upload code scanning results

jobs:
  model-checks:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v6
      - name: Install sm (a release binary, see Install)
        env:
          GH_TOKEN: ${{ github.token }}
        run: |
          gh release download --repo brickfrog/semctl --pattern 'sm-*-linux-x64.tar.gz'
          tar -xzf sm-*-linux-x64.tar.gz
          echo "$PWD/$(ls -d sm-*-linux-x64/)" >> "$GITHUB_PATH"
      - run: sm bpa "Sales.SemanticModel" --sarif bpa.sarif
      - uses: github/codeql-action/upload-sarif@v4
        if: always()
        with:
          sarif_file: bpa.sarif
          category: sm-bpa   # one category per command, if you upload several
```

## Re-running on save (`--watch`)

`validate`, `lint`, `bpa` and `test run` take `--watch`: run once, then again whenever the model's files change,
until Ctrl+C (which exits 0).

```sh
sm validate Sales.SemanticModel --watch
sm bpa model.bim --rules team.json --watch --json | jq -c .summary
```

- **What it watches:** the model's files. A `.bim` is that one file; a TMDL folder, `.SemanticModel` folder or
  `.pbip` project is the semantic model folder (not the report). `bpa` also watches its `--rules` files; `test run`
  also watches its suite files (or the default `.sm-tests` folders) and its `--data` folder.
- **What it ignores:** files outside those, hidden folders (`.git`, `.pbi`, `.vs`), editors' temporary and backup
  files (`*~`, `.#*`, `*.swp`, `*.tmp`, `4913`, JetBrains `___jb_` files...), and the command's own outputs
  (`--trx`, `--junit`).
- **When it runs:** 300 ms after the last change of a burst, so an editor's save-rename, or a tool rewriting a
  whole folder, is one run. A change during a run means one more run after it.
- **How it notices:** a file system watcher, and, as a safety net for file systems that don't report changes
  (network drives, some container and WSL mounts) or a watcher that overflows, a check of file sizes and times every
  second.
- **Output:** in text mode the screen is cleared before each run (only on a terminal), and a status line goes to
  stderr. With `--json`, each run writes one JSON document on one line (JSON Lines), and no status line (`--ci`
  annotations still go to stderr).
- **Errors:** a usage error, or a model path that isn't there, stops it at once with the usual exit code. A model
  that fails to load once it's running (a file saved half-way) is reported, and the watch goes on.
- Not for server models (`xmla:`), and not with `lint --fix --save` / `--save-to`, which would change the files it
  watches.
