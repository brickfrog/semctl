# Development

```sh
dotnet build
dotnet test                                         # Expecto + FsCheck
dotnet run --project tests/Tessera.Tests -- --summary   # same tests, Expecto's own runner
dotnet run --project src/Tessera.Cli -- info <path>
dotnet tool restore && dotnet fantomas src tests    # format
dotnet pack src/Tessera.Cli -c Release -o artifacts && dotnet tool install -g tessera --add-source ./artifacts
```

CI (`.github/workflows/ci.yml`) checks formatting, then builds and tests on Linux, Windows and macOS. A further job
runs the suite against a pinned clone of the sample model below.
Set `TESSERA_BPA_RULES=<file>[:<file>...]` to run BPA rule files (e.g. from TabularEditor/BestPracticeRules)
in a test that reports how many rules are supported. (This is a test setting; `tess bpa` itself reads
`TESS_BPA_RULES`, which the CLI tests hide, along with `HOME`, `XDG_CONFIG_HOME` and `APPDATA`.) It runs against `TESSERA_SAMPLE_MODEL` if that is set,
otherwise against a fixture.

Set `TESSERA_XMLA_SERVER` and `TESSERA_XMLA_DATABASE` (with credentials in the `TESS_XMLA_*` variables) to also run
read-only tests against a real server; see `docs/xmla-manual-test.md`. The CLI tests never see real credentials,
and fail if a command that should be offline asks for a connection.

Set `TESSERA_SAMPLE_MODEL=<path>` to also run a round-trip test against a real model, such as a clone of
[TabularEditor/spaceparts-analytics](https://github.com/TabularEditor/spaceparts-analytics).

Releases: push a tag such as `v0.2.0`. `.github/workflows/release.yml` then:
- runs the tests
- builds the self-contained single-file binaries and the tool package, stamped with the tag's version
  (`-p:Version=0.2.0`; `Directory.Build.props` holds the default for local builds)
- attaches them all to a GitHub release

`tess --version` prints that version. The `+<commit>` suffix .NET adds to the informational version is left out.
To build a binary locally:

```sh
dotnet publish src/Tessera.Cli -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o publish
```

Single-file binaries are compressed and not trimmed, because TOM relies on reflection. They leave out MSAL's native
sign-in broker, which tess never uses (it signs in with a token, a service principal or a device code, all in
managed code), and the optional `msasxpress.dll` transport compression on Windows.

Layout:

- `src/Tessera.Core` holds the model logic and never prints anything. Commands return typed results and
  `TessError` values.
- `src/Tessera.Xmla` is the only assembly that opens connections to servers. Everything goes through
  `IXmlaConnector` / `IXmlaSession` (`Session.fs`); `Live.fs` is the one implementation that uses the client
  libraries' connection types, and tests use a fake. `Remote.fs` holds the logic on top, `Credentials.fs` reads the
  `TESS_XMLA_*` variables, and `Failures.fs` turns client-library errors into `TessError`s with secrets scrubbed.
- `src/Tessera.Cli` handles argument parsing (Argu) and text/JSON rendering:
  - `Output.fs` holds the JSON helpers.
  - `Common.fs` holds exit codes, `Outcome`, `Ctx` and argument helpers.
  - `Commands/<Name>Command.fs` holds one command each: its `summary`, `Args`, renderers and `run`.

To add a command:
1. Write the logic in Core, with tests.
2. Add `src/Tessera.Cli/Commands/<Name>Command.fs`, following an existing command.
3. Add it to `Tessera.Cli.fsproj`, before `Args.fs`.
4. Add a case to `CliArgs` in `Args.fs`, plus its usage line (`<Name>Command.summary`).
5. Add a dispatch line in `App.fs`.
6. Put its CLI tests in `tests/Tessera.Tests/<Name>CommandTests.fs`, using `CliHarness` (`tess`, `json`,
   `project`).

## The DAX parser (`Tessera.Core`)

A real DAX parser, the first step of a local DAX engine. It has no command of its own yet: it is the library the
evaluator is written against. Three modules, none of which knows about a model:

- `DaxAst` is the tree. Every node carries the span (offset and length) of its source text. Names are unresolved,
  exactly as written: `Sales` (a table, a variable or a parameter), `'Sales'`, `Sales[Amount]` (a column or a
  measure), `[Amount]`. Parentheses the source had are kept as nodes, so a tree's shape is the source's grouping.
- `DaxParser` reads text into it and never throws:

  ```fsharp
  DaxParser.parseExpression "CALCULATE ( [Total], 'Date'[Year] = 2024 )"   // Result<Expr, ParseError list>
  DaxParser.parseFunction   "(amount: NUMERIC, rate = 0.1) => amount * rate" // Result<FunctionDef, ...>
  DaxParser.parseQuery      "DEFINE MEASURE Sales[X] = 1 EVALUATE Sales ORDER BY [X] DESC"
  DaxParser.parse           text    // whichever of the three the text is
  ```

  Each has a `...With { Semicolons = true }` form for DAX that separates arguments with `;` and writes decimals
  with `,`. An error has its offset, length, line, column and a message that says what to fix:
  `1:18: expected ')' to close the '(' at 1:4, found the end of the text`.
- `DaxPrinter` writes a tree back as minimal one-line DAX (`Format` remains the formatter). Parsing what it prints
  gives the same tree.

Operator precedence, loosest first: `||`; `&&`; `NOT`; `=` `==` `<>` `<` `<=` `>` `>=` `IN`; `&`; `+` `-`; `*` `/`;
the sign; `^`. Operators of one level group left to right. This is Microsoft's
[operator precedence table](https://learn.microsoft.com/en-us/dax/dax-operator-reference), with `&&` and `||`
placed as the [DAX Guide](https://dax.guide/operators/) places them. `NOT` is always a prefix operator, never a
function: `NOT (a) = b` is `NOT ((a) = b)`. The two sources disagree about `IN` (Microsoft lists it with the
comparisons, the DAX Guide above `^`); the parser follows Microsoft.

Limits, so that nothing can overflow the stack: expressions may nest 256 levels deep, and a tree is at most
`DaxParser.maxDepth` (500) levels deep, which also caps an unparenthesised chain of one operator at 499 operators.
Anything deeper is an error, not a tree.

Things worth knowing when using the tree:

- A keyword argument (`DESC`, `MONTH`, `BOTH`, `INTEGER`) is an ordinary `Identifier`: without the function's
  signature it cannot be told from a table or variable of that name. The `Keyword` pattern reads one in upper case.
  Several keywords side by side (`ASC BLANKS LAST`) are one `Words` node.
- An argument left out (`RANKX ( T, [m], , DESC )`) is an `Omitted` node. `F()` has no arguments.
- A bare `TRUE` is a literal, `TRUE()` a call.
- `DaxAst.references` lists the names an expression refers to, leaving out the VARs in scope. A test checks it
  against `Deps` for every expression, and names every reason the two may differ.

## The DAX conformance corpus

`tests/conformance` is a corpus of small DAX queries with the results a real engine gives, each taken from a
published source (the DAX reference, DAX Guide, SQLBI) and saying which: its format and rules are in
[`tests/conformance/README.md`](../tests/conformance/README.md). It is the independent check on the local DAX
engine: no expected value came from running or reading an engine. **It has not been confirmed against a server
yet**; many cases are worked out by hand from documented rules, and some are marked uncertain.

- `tests/Tessera.Tests/Conformance.fs` reads and checks the corpus, and runs any engine against a case:

  ```fsharp
  Conformance.run : (Database -> DataStore -> string -> Result<QueryResult list, TessError>) -> Case -> Outcome
  // Outcome: Pass | Fail of Mismatch (a value-level diff) | NotSupported of reason | Error of message
  ```

- `dotnet test` checks every case file (the model builds, the rows are a valid fixture, the query parses, the
  expectation is well-formed) and that every model embeds its rows exactly. The `conformance (engine)` test list
  is skipped until the engine exists: set `engine` at the top of `ConformanceTests.fs`. `expectedUnsupported`
  there lists the tags the engine may leave for later. It takes any tag, a function's name included (`round`,
  `format`): the area tags (`math-functions`...) mark the cases in that area's folder, and cases elsewhere call
  such functions too, tagged only with the function.
- `QueryResult.compare` (`Tessera.Core`) is the comparison: columns by name, rows as a multiset unless ordered,
  doubles within a tolerance, int64 and decimal exactly, and differences of type (`1` against `1.0`, a column
  declared `double` instead of `decimal`) reported apart from differences of value. `QueryResult.ofJson` reads
  the `tess query --json` shape back.
- To run the corpus against a real server (it deploys scratch databases named `tessera-conformance-*`):

  ```sh
  TESSERA_XMLA_SERVER='powerbi://api.powerbi.com/v1.0/myorg/<workspace>' \
    dotnet run --project tests/Tessera.Tests -c Release -- conformance-server --out ./conformance-report
  ```

  See `docs/xmla-manual-test.md`, section G. `python3 tests/conformance/check-quotes.py` checks every quoted
  source line against its page (the only part that uses the network, and only when you run it).

## Prior art

- [Tabular Editor 2](https://github.com/TabularEditor/TabularEditor) (MIT) is the inspiration and the reference
  for TOM behaviour; for example, the Power BI compatibility-mode heuristic follows its
  `PowerBiCompatibilityModeHelper`. `bpa` follows its Best Practice Analyzer: the rule format, scopes, ignore
  annotations and the TOMWrapper names that rules use. Its DAX token codes are ported from TE2's lexer.
- tessera is independent of Tabular Editor ApS and its commercial Tabular Editor 3 and `te` CLI.
