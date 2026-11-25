# Connecting to a server

## Connected mode (`connect`, `query`, `deploy`, remote models)

sm works on files and stays offline unless a command is given a server. Then it talks to that server over XMLA,
through Microsoft's own client libraries (AMO/TOM and ADOMD.NET). **This part has been built and tested against a
fake server only; it has not yet been run against a real one.** `docs/xmla-manual-test.md` is the checklist for
that.

```sh
export SM_XMLA_TOKEN="$(az account get-access-token --resource https://analysis.windows.net/powerbi/api --query accessToken -o tsv)"
sm connect "powerbi://api.powerbi.com/v1.0/myorg/Finance"            # can I get in? which models are there?
sm info "xmla:powerbi://api.powerbi.com/v1.0/myorg/Finance;Sales"    # any read-only command, on the deployed model
sm diff "xmla:powerbi://api.powerbi.com/v1.0/myorg/Finance;Sales" ./Sales.SemanticModel
sm convert "xmla:powerbi://api.powerbi.com/v1.0/myorg/Finance;Sales" ./Sales.SemanticModel   # download as files
sm query "xmla:powerbi://api.powerbi.com/v1.0/myorg/Finance;Sales" "EVALUATE TOPN(10, 'Date')"
sm query "xmla:...;Sales" -f totals.dax --json
sm deploy ./Sales.SemanticModel "powerbi://api.powerbi.com/v1.0/myorg/Finance" Sales          # dry run
sm deploy ./Sales.SemanticModel "powerbi://api.powerbi.com/v1.0/myorg/Finance" Sales --execute --overwrite
sm refresh "xmla:powerbi://api.powerbi.com/v1.0/myorg/Finance;Sales" Orders --type full       # dry run
sm refresh "xmla:powerbi://api.powerbi.com/v1.0/myorg/Finance;Sales" Orders --type full --execute
```

### Naming a server model

A model on a server is written `xmla:<server>;<database>`, wherever a command takes a model path. Quote it: `;`
ends a command in every shell, and workspace names often have spaces. The prefix is required, so a path can never
be mistaken for a server (`localhost:51234` and `host\instance` look exactly like paths) and no command opens a
connection unless its argument says so: `xmla:...`, or `active:` / `profile:<name>` when the active connection or
the profile is a server. `connect` and `deploy` take a server as its own argument, where the
prefix is optional.

| Server | Form | Works on |
|---|---|---|
| Power BI Premium / PPU / Fabric workspace | `powerbi://api.powerbi.com/v1.0/myorg/<workspace>` | Linux, macOS, Windows |
| Azure Analysis Services | `asazure://<region>.asazure.windows.net/<server>` | Linux, macOS, Windows |
| SSAS over HTTP (IIS `msmdpump.dll`) | `https://<host>/olap/msmdpump.dll` | Windows; elsewhere only with a user name and password |
| SSAS over TCP | `<host>`, `<host>:<port>`, `<host>\<instance>` | Windows only |
| Power BI Desktop's local model | `localhost:<port>` | Windows only |

The Windows-only rows are a limit of Microsoft's client library, which has no TCP transport on other systems.
sm says so before trying.

The database can be left out when the server has exactly one (as Power BI Desktop does). A wrong name is answered
with the names that exist. A full connection string also works after the prefix
(`xmla:Data Source=...;Initial Catalog=...;Timeout=60`); its extra properties are passed on. A connection string
with a `Password`, `User ID` or anything token-like is refused (exit 2): credentials never go on the command line.

### Signing in

Credentials come from environment variables, or from files (or standard input) that variables point at. They are
never read from the command line, never printed, never written to JSON output, and removed from every error message
(sm scrubs the secrets it holds, and anything shaped like a password, bearer token or JWT).

| To sign in with | Set |
|---|---|
| an access token you already have | `SM_XMLA_TOKEN`, or `SM_XMLA_TOKEN_FILE=<path>` (`-` for standard input) |
| a service principal | `SM_XMLA_CLIENT_ID`, `SM_XMLA_TENANT_ID`, and `SM_XMLA_CLIENT_SECRET` or `SM_XMLA_CLIENT_SECRET_FILE`. Without those, `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` and `AZURE_CLIENT_SECRET` are used |
| your own account (device code) | `SM_XMLA_AUTH=device-code` and `SM_XMLA_CLIENT_ID` (your own app registration); optionally `SM_XMLA_TENANT_ID` |
| a user name and password (SSAS) | `SM_XMLA_USER`, and `SM_XMLA_PASSWORD` or `SM_XMLA_PASSWORD_FILE` |
| the current Windows account (SSAS) | nothing |

`SM_XMLA_AUTH` picks one explicitly: `auto` (the default), `token`, `spn`, `device-code`, `basic` or `integrated`.
With `auto`, a cloud server (`powerbi://`, `asazure://`) uses the token if there is one, then the service principal;
with neither, sm stops and says what to set. It never starts an interactive sign-in by itself. Other servers use
the user name and password if both are set, otherwise the current Windows account. A token or service principal in
the environment is never sent to a non-cloud server.

A token must be issued for the server's resource: `https://analysis.windows.net/powerbi/api` for Power BI,
`https://*.asazure.windows.net` for Azure Analysis Services. A static token isn't renewed; sm reads its expiry from
the token only to tell the client library.

Device-code sign-in prints a URL and a code on stderr and waits for you to enter the code in a browser, on any
device. It needs `SM_XMLA_CLIENT_ID`: the application (client) ID of a public client app registration of your
own, with the Power BI Service delegated permission and "Allow public client flows" on. sm ships no application
ID. The sign-in lasts for that one command, unless you opt in to keeping it:

```sh
export SM_XMLA_AUTH=device-code SM_XMLA_CLIENT_ID=<your app> SM_XMLA_TOKEN_CACHE=~/.cache/semctl/xmla-token-cache
```

`SM_XMLA_TOKEN_CACHE` names a file where the sign-in (including a refresh token) is kept between runs. It is
created with mode 0600 on Linux and macOS; on Windows it gets its folder's permissions, so keep it in your profile.
Nothing else in sm ever writes a credential to disk. Delete the file to sign out.

### What gets contacted

Only commands given a server contact anything, and only these, all through the client libraries:

- the server in the address;
- for `powerbi://` and `asazure://`, Microsoft's sign-in service (`login.microsoftonline.com`, or the national
  cloud's), unless you supply a token;
- the service's own name resolution: the Power BI workspace lookup on the address's host, and for `asazure://`
  the region's host and `global.asazure.windows.net`.

sm adds no telemetry and no other calls. The test suite checks, on the compiled assemblies, that
`Semctl.Core` uses no network type outside the explicit BPA rule download and `pkg`'s daxlib.org fetch, that the
CLI uses none itself, and that in `Semctl.Xmla` only the live connector (and `schemacheck`'s live SQL describer,
`LiveSql`) does.

### Remote models in other commands

`info`, `list`, `get`, `find`, `diff`, `deps`, `validate`, `bpa`, `lint`, `script export` and `convert` accept a
remote model as their source. The model is read once and the connection closed; the result is the same as for the
model's files. `convert` from a remote model downloads it. `bpa` uses the built-in, user and `--rules` layers for a
remote model, but no project config (it has no folder).

The editing commands show their dry run against a remote model and can `--save-to` files, but `--save` is refused:
`deploy` is how a model gets onto a server. Commands that load models only from files (`format --save`,
`translations`, `perspective`, `script apply`, `bpa rules`) say so (exit 3) without connecting.

### `connect`

`sm connect <server>` connects, and prints the product and version, how it signed in (never the credential), and
the databases with their compatibility levels. With `--json`, each database also has a `source` you can paste as a
model argument. It then becomes this terminal's **active connection** (`active:`; see
[profiles and the active connection](#profiles-and-the-active-connection-profile-session-connect)), unless
`--no-remember`. Every command still connects by itself: nothing but the address is kept.

### `query`

```sh
sm query <xmla-model> "<DAX>"        # or: -f <file>, or - for standard input
sm query <xmla-model> -f q.dax --csv > out.csv
sm query <xmla-model> "EVALUATE 'Date'" --max-rows 0 --json
```

A query may hold several `EVALUATE` statements; each gives one result. The default output is an aligned table per
result, `--csv` is RFC 4180 with results separated by an empty line, and `--json` is described below. `--max-rows`
caps each result (default 1000, 0 for all); a capped result is marked `truncated` and stderr says so. Queries to a
server are sent as they are: sm doesn't check them first. On model files, `query` needs `--data <fixture>` and
runs offline: see [the local DAX engine](dax-engine.md#the-local-dax-engine-query---data).
`--role <name>` (repeatable), `--user-name` / `--user-principal-name` and `--custom-data` query as someone else:
they become the connection's `Roles=`, `EffectiveUserName=` and `CustomData=` (see
[roles](dax-engine.md#roles-row-level-security)).

The JSON shape is shared with the local DAX engine, so the two can be compared value by value:

```json
{ "command": "query",
  "source": "xmla:powerbi://api.powerbi.com/v1.0/myorg/Finance;Sales",
  "results": [
    { "columns": [ { "name": "Date[Year]", "type": "int64" }, { "name": "[Total]", "type": "decimal" } ],
      "rows": [ [ 2023, 1234.5 ], [ 2024, null ] ],
      "rowCount": 2,
      "truncated": false } ],
  "durationMs": 212 }
```

- **`results`:** one entry per `EVALUATE`, in order.
- **`columns[].name`:** exactly as the engine names it (`Table[Column]`, `[Measure or expression name]`).
- **`columns[].type`:** `string`, `int64`, `double`, `decimal` (DAX currency), `dateTime`, `boolean`, `binary`, or
  `variant` when the engine doesn't declare one type for the column.
- **`rows`:** one array per row, values in column order. Row order is the engine's; without `ORDER BY` it is not
  defined, so compare such results as multisets.
- **`rowCount`:** the rows present. **`truncated`:** `--max-rows` cut the result short.

Each value has exactly one spelling:

| Value | JSON |
|---|---|
| BLANK | `null` (an empty string is `""`: they stay distinct) |
| `int64` | an integer: `42` |
| `double` | a number with a `.` or exponent, shortest digits that round-trip: `1.0`, `0.1`, `1E+21`. `NaN`, `Infinity` and `-Infinity` are those strings |
| `decimal` | a number with exact digits and at least one decimal place, no exponent, no trailing zeros: `1234.5`, `12.0` |
| `dateTime` | a string, ISO 8601 with no zone: `"2024-01-31T00:00:00"`; a fraction only if there is one |
| `boolean` | `true` / `false` |
| `string` | a string |
| `binary` | a base64 string |

With `--formatted` (local engine only: a server's DAX query result carries no format strings), a column whose cells
have format strings gets `"formatString"` when every row has the same one, otherwise `"formatStrings"` (one per row,
`null` where a cell has none), and each result gets `"formatted"`, parallel to `"rows"`: each cell's value formatted
with its format string, `null` where it has none. `rows` keeps the values. Without the flag none of these keys are
there. See [format strings](dax-engine.md#format-strings).

Read `decimal` values with a decimal type; a parser that goes through binary floating point can lose digits. In a
`variant` column a number with a decimal point may be a `double` or a `decimal`. In text and CSV output BLANK is
empty, booleans are `TRUE`/`FALSE`, and CSV writes an empty string as `""`. The type is `Semctl.QueryResult` in
`Semctl.Core`; `QueryValue.toJson` is the encoder.

### `deploy`

```sh
sm deploy <model> <server> <database>            # dry run: connects and reads, changes nothing
sm deploy <model> <server> <database> --execute  # deploys; an existing database also needs --overwrite
```

`deploy` always connects, reads the database if it exists, and works out the model the server would hold
afterwards. A dry run prints what would change (the same diff as `sm diff`, with `--stat`, `--name-only` and
`--ignore`), then the TMSL `createOrReplace` script it would run. `--tmsl` prints only the script; `--json` gives
`changes`, `kept`, `warnings`, `introducedErrors`, `tmsl`, `creates`, `dryRun` and `deployed` in one document.
`<model>` can be another `xmla:` model, to copy between servers.

By default a deployment replaces the model's metadata and leaves alone what belongs to the server:

| Kept from the server unless | Option |
|---|---|
| partitions and refresh policies of tables it already has, so refresh state and incremental-refresh partitions survive | `--deploy-partitions` (with `--skip-refresh-policy`: still keep them for tables with a refresh policy) |
| data sources | `--deploy-connections` |
| shared (M) expressions | `--deploy-shared-expressions` |
| roles | `--deploy-roles` |
| role members | `--deploy-role-members` (implies `--deploy-roles`) |

`--deploy-full` is all of them. The changes shown are against that merged model, so a dry run doesn't report
partitions or roles that would in fact stay. Tables new to the server get the model's partitions. A database that
doesn't exist yet takes everything from the model except role members.

Two things stop `--execute`:
- the database exists and `--overwrite` isn't given (exit 4);
- the deployed model would have errors from any `validate` rule that the current one doesn't, without `--force` (exit 1). The dry run
  lists them.

If the server rejects the script or reports errors running it, that is exit 5 with its messages. After a
deployment, new or changed tables may need a refresh: see `refresh` below (`deploy` doesn't run one).

Where this differs from the Tabular Editor tools, and why:

- **Arguments.** `te deploy` names the source with `-s`/`-d` and the target with `--target-server`. sm takes the
  model as its first argument, like every other command, then the server and database.
- **`--execute` doesn't prompt.** `te` asks for confirmation and wants `--execute --force` in CI. sm never prompts;
  `--force` means only "despite new errors".
- **Overwriting needs `--overwrite`**, as in TE2 (`-O`). `te` overwrites unless `--create-only`.
- **A new database gets the model's roles** even without `--deploy-roles`. TE2 would create it without roles,
  leaving row-level security off.
- **The server's data sources are kept even when the model has none.** TE2 keeps them only if the model has some.
- **Passwords in the model's data sources are not deployed.** The script is written without restricted
  information, so a dry run never prints a credential; TE2 includes it. After `--deploy-connections` (or creating a
  database with data sources), set their credentials on the server.
- **Not done:** TE2's two-step deployment for a table or column that changes between calculated and loaded (sm
  warns instead), a BPA gate, skipping linguistic metadata, deployment profiles, and refreshing afterwards.

### `refresh`

```sh
sm refresh "xmla:<server>;<database>"                               # dry run: the whole model, type automatic
sm refresh "xmla:<server>;<database>" Sales "partition:Orders/2024" --type full
sm refresh "xmla:<server>;<database>" Sales --type full --execute   # refreshes
```

`refresh` connects, reads the model to find what the selectors name, and prints what the refresh covers and the
TMSL script it would run. Nothing is refreshed without `--execute`. `--tmsl` prints only the script, and `--json`
gives one document either way. It only runs on a server model: model files hold no data (exit 2, without
connecting). **Like the rest of connected mode, `refresh` has never been run against a real server.**

What to refresh comes after the model, in the selector syntax of the other commands. Nothing, or `model`, is the
whole model. A table is `Sales`, `'Sales'` or `table:Sales`. A partition is `partition:Sales/2024` or
`'Sales'[2024]`; a bare name that no table has is taken as a partition if exactly one has it. A table and its only
partition often share a name: the bare name is the table. Names are matched without regard to case and written
into the script as the server has them. A selector that names something else (a measure, a column) is exit 3.
A partition of a table that is also named is dropped (with a warning), as is everything else when the model is.

| `--type` | Does | Takes |
|---|---|---|
| `automatic` (default) | loads and recalculates only what isn't in a ready state | model, tables, partitions |
| `full` | reloads the data and recalculates everything that depends on it | model, tables, partitions |
| `dataOnly` | reloads the data, without recalculating | model, tables, partitions |
| `calculate` | recalculates what needs it; loads nothing | model, tables, partitions |
| `clearValues` | removes the data | model, tables, partitions |
| `defragment` | cleans up column dictionaries | model, tables |
| `add` | appends what a partition's query returns to the rows it has | partitions |

Those are all the types TMSL documents; any other is a usage error. The "Takes" column is Microsoft's reference,
and sm refuses the rest (exit 3) before running anything, where the client library would script it and leave it
to the server.

- **`--max-parallelism <n>`** wraps the refresh in a TMSL `sequence` with `maxParallelism`, so at most `n` objects
  are processed at once. Without it the server decides.
- **`--apply-refresh-policy` / `--no-apply-refresh-policy`** write `applyRefreshPolicy` into the script. With
  neither, the property is left out and the server applies the policy of tables that have one (its default). With
  `--no-apply-refresh-policy`, a table's partitions are refreshed as they are, which is how to reload a whole
  incremental-refresh table. Power BI only.
- **`--effective-date YYYY-MM-DD`** is the date an applied policy treats as today (and implies applying it). It is
  written as `MM/DD/YYYY`, the form in Microsoft's reference.
- The refresh-policy options apply to tables, so they can't be combined with a partition (exit 3). The dry run
  warns when a policy will be applied (the server may add, merge and remove partitions), and when the options are
  given but can't matter (no table in the refresh has a policy, or the type ignores policies).

With `--execute`, sm writes `Refreshing...` to stderr, then progress as the server reports it, then
`Refreshed '<database>' on <server> in <seconds>`. If the server rejects the script or reports errors running it,
that is exit 5 with its messages (`refresh-failed` in JSON, where the document still has `progress`).

Progress comes from a trace of sm's own session (the client library's `Server.SessionTrace`). `--progress` picks
how much:

| `--progress` | Shows |
|---|---|
| `objects` (default) | the steps that finish once per table or partition (the source query, rows read, processing, the commit) and every error |
| `all` | every "Progress Report" event the server sends (begin, current, end, error), including the per-column ones |
| `none` | nothing: the session isn't traced, and only the start, finish and duration are reported |

```
Refreshing...
  [ReadData] Finished reading data for the 'Sales' table. (1,234,567 rows, 12.3s)
  [Process] Finished processing partition 'Sales' of table 'Sales'. (13.0s)
Refreshed 'Sales' on powerbi://api.powerbi.com/v1.0/myorg/Finance in 14.2s.
```

The text in each line is the server's own. If the trace can't be started or stops, the refresh still runs and sm
says why there is no progress (`traceError` in JSON). Which events a server sends, and what they say, has only been
checked against a fake: use `--progress all` to see what yours sends, and `--progress none` if the trace itself
gets in the way.

`--json` has `source`, `server`, `database`, `type`, `dryRun`, `refreshed`, `objects` (what the script names, each
with a `ref`), `covers` (per table: `partitions`, `wholeTable`, `refreshPolicy`), `applyRefreshPolicy`,
`effectiveDate` (ISO), `maxParallelism`, `warnings`, `tmsl`, `serverMessages`, `progress` (each event's `phase`,
`step`, `object`, `objectPath`, `text`, `integerData`, `durationMs`), `traceError` and `durationMs`. With `--json`
nothing is written to stderr.

Where this differs from `te refresh`, and why:

- **Objects are selectors after the model**, not `--table` and `--partition T.P`, as in every other sm command.
- **`--execute` doesn't prompt**, as with `deploy`.
- **`--apply-refresh-policy` takes no value.** `te` accepts `true`, `false` or a table name; sm has the pair of
  flags, and one script applies one setting to every table in it. Run two refreshes to treat tables differently.
- **Not done:** refresh override profiles (TMSL `overrides`: other data sources, queries or parameters for one
  refresh), writing the trace to a file (`--trace <path>`), VertiPaq statistics after a refresh, cancelling a
  running refresh (interrupting sm closes the session; what the server then does hasn't been observed), and
  waiting for trace events the server sends after the script returns (they are dropped).

### Not in sm yet

A workspace mirror (`te connect -w`: saves written locally, then to the server), interactive
browser sign-in, and certificate and managed-identity sign-in.

What each needs, for whoever picks it up:
- **Refresh overrides:** `Refresh.Request` (Core) would carry the TMSL `overrides` array; the builder writes it
  after `objects`. `IXmlaSession.ExecuteTraced` is the trace seam, if something else needs server events.
- **Interactive sign-in:** needs the MSAL broker the single-file builds leave out (see `Semctl.Cli.fsproj`).

## Profiles and the active connection (`profile`, `session`, `connect`)

Local state only: nothing here contacts a server or stores a secret.

**The active connection.** `sm connect <model or server>` checks it (a server: connects and lists its databases;
a local model: loads it) and makes it this terminal's active connection. Any command then takes `active:` as its
model argument:

```sh
sm connect ./Sales.pbip                       # or: sm connect "powerbi://.../Finance;Sales"
sm validate active:
sm interactive                                # a local active connection; a server needs `sm interactive active:`
sm connect                                    # show it
sm connect --clear                            # forget it
```

"This terminal" is the terminal's session leader on Linux and macOS (`getsid`: the shell the terminal started,
which pipelines and subshells share) and the parent process on Windows, together with its start time, so a later
process that gets the same id doesn't inherit it. `SM_SESSION=<name>` names a session to share between terminals
(or to keep one when sm runs under a wrapper such as `dotnet tool run`, whose parent changes every run). State
files live in `$SM_STATE_DIR`, else `%LOCALAPPDATA%\semctl\sessions` (Windows),
`$XDG_STATE_HOME/semctl/sessions`, or `~/.local/state/semctl/sessions`; each holds the model argument, the profile
it came from, the time and the terminal's process id and start time. `sm session` shows this session's (`list`:
every session's, marking terminals that are gone; `clear`; `prune` deletes those of terminals that are gone).

**Profiles** are named connections in the user config file: `$SM_CONFIG`, else `%APPDATA%\semctl\config.json`
(Windows), `$XDG_CONFIG_HOME/semctl/config.json`, or `~/.config/semctl/config.json`.

```sh
sm profile set dev --model ./Sales.pbip --description "Local TMDL"
sm profile set prod --server "powerbi://api.powerbi.com/v1.0/myorg/Finance" --database Sales --setting auth=device-code
sm profile list
sm profile show prod --json
sm connect --profile prod                     # make it the active connection (nothing is contacted)
sm query profile:prod "EVALUATE ..."          # one command, without changing the active connection
sm profile remove dev
```

```json
{ "formatVersion": 1,
  "profiles": { "prod": { "server": "powerbi://api.powerbi.com/v1.0/myorg/Finance", "database": "Sales",
                          "settings": { "auth": "device-code" } } } }
```

A profile names a local model (`--model`) or a server and database (`--server`, `--database`). Its settings are
behaviour overrides: defaults for the non-secret `SM_*` variables, for the commands that use the profile
(`profile:<name>`, or `active:` when the active connection came from it). A variable set in the environment wins.

| Setting | Variable |
|---|---|
| `auth` | `SM_XMLA_AUTH` (`token`, `spn`, `device-code`, `basic`, `integrated`) |
| `clientId`, `tenantId` | `SM_XMLA_CLIENT_ID`, `SM_XMLA_TENANT_ID` |
| `user` | `SM_XMLA_USER` |
| `tokenCache` | `SM_XMLA_TOKEN_CACHE` (the opt-in sign-in cache's file) |
| `bpaRules` | `SM_BPA_RULES` |

**Never a secret.** Tokens, client secrets and passwords stay in the environment (and the opt-in token cache) as
without a profile: a server string with credentials in it is refused as on the command line, a setting sm doesn't
know is refused, and so is any value with credentials written the way a connection string writes them
(`Password=`, `Pwd=`, `...Secret=`, `...Token=`). The state files hold
only the model argument.

`active:` and `profile:<name>` are replaced before the command line is read, wherever they appear as a whole
argument (a model argument, or `diff`'s second model). sm reads the config and state files only for a command
line that uses one of them, and for `connect`, `profile`, `session` and a bare `interactive`. (An `@name` form, as
in `te`, is avoided: PowerShell reads `@name` as splatting.)
