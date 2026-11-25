# AI agents (`mcp`)

`sm mcp <model>` serves one model to an AI agent (Claude Code, VS Code, Cursor, Codex, ...) as a
[Model Context Protocol](https://modelcontextprotocol.io) server over stdio: the agent's client starts sm, and
they talk JSON-RPC 2.0 over its stdin and stdout (MCP 2025-06-18; clients asking for 2025-03-26 or 2024-11-05 get
that version). There is no HTTP transport: sm opens no port and makes no network call of its own.

```sh
claude mcp add sm -- sm mcp ./Sales.pbip                                   # read only
claude mcp add sm -- sm mcp ./Sales.pbip --allow metadata-write --save    # may change and save it
claude mcp add sm -- sm mcp ./Sales.pbip --allow data-read --data ./fixtures --audit-log ./sm-mcp.jsonl
```

```json
{ "mcpServers": { "sm": { "command": "sm", "args": ["mcp", "./Sales.pbip", "--allow", "read,bpa"] } } }
```

**Permissions.** `--allow` (repeatable or comma-separated) decides which tools exist. A tool outside the grants is
never registered: it isn't listed, and calling it fails exactly like calling a tool that doesn't exist.

| Grant | Tools |
|---|---|
| `read` (the default) | `model_overview`, `list_objects`, `search_model`, `get_objects`, `list_properties`, `get_dependencies`, `validate_model`, `lint_dax`, `format_dax`, `get_full_model` |
| `metadata-write` (implies `read`) | `apply_changes`; with `--save`, also `save_model` |
| `data-read` | `run_query`, over a fixture with the local engine (`--data <dir>`) or on a server model you name (`--server xmla:<server>;<database>`) |
| `bpa` | `list_bpa_rules`, `run_bpa` |

**`data-read` hands model rows to whatever client launched the server**, and through it to the agent's AI
provider. It is off unless you grant it, and it needs `--data` or `--server` to say where rows come from. The
other grants send metadata only: names, expressions, descriptions, annotations, findings.

**The tools**, from cheap to detailed (the server's instructions tell the agent to go in this order):
- `model_overview`: the model's level and object counts, every table with its column, measure, hierarchy and
  partition counts, every relationship.
- `list_objects` (a container, kinds, a name wildcard, [`--where`](model-files.md#filtering-objects-get---where) conditions) and
  `search_model` (`find`): refs to pass on. At most 500 per call, and the result says when it was cut.
- `get_objects` (TOM JSON, TMDL, or chosen property paths such as `expression`), `list_properties` (`get
  --properties`), `get_dependencies` (`deps`).
- `validate_model`, `lint_dax`, `format_dax` (formats text or an object's expression; changes nothing),
  `list_bpa_rules`, `run_bpa`: the same JSON as the commands' `--json`. BPA rules come from the built-in set, the
  model's annotations and your rule files; `run_bpa` never fetches a URL.
- `get_full_model`: the whole model as TMDL. Its description warns the agent that it costs a great many tokens.
- `run_query`: 100 rows per result by default, at most 500 (`maxRows`); a result cut short is marked `truncated`
  with a note. On a server the limit is applied while reading.

**Changes.** `apply_changes` takes a batch of operations and applies them all or none: if one fails, the model is
left exactly as it was and the error names the operation (`operation`: its index).

| `op` | Arguments | Like |
|---|---|---|
| `set` | `object`, `properties` (`{"<path>": value or null}`), `unset` | `sm set` |
| `rename` | `object`, `newName` | `sm rename` (fixes the DAX that refers to it, and the PBIR reports next to the model: staged, written by `save_model`) |
| `add` | `kind` (`measure`, `calculatedColumn`, `dataColumn`, `calculatedTable`, `relationship`), `table`, `name`, `expression`, `dataType`, `sourceColumn`, `from`, `to`, `active`, `description`, `displayFolder`, `formatString`, `hidden` | `sm add` |
| `remove` | `objects`, `cascade`, `force` | `sm remove` (refused while other DAX or a report's visuals use them, unless `force`) |
| `move` | `measure`, `table`, `displayFolder` | `sm move` |
| `script` | `dax`: `DEFINE MEASURE`/`COLUMN`/`TABLE` blocks, created or updated | `sm script apply` |

The result is the editing commands' JSON: `changes[]`, `introducedErrors` (errors the batch introduced),
`resolvedErrors` (errors it fixed), `errorsIntroducedSinceLoad`, notes, warnings and `unsavedChanges`. Changes stay
in memory for the session. Only with `--save` is there a `save_model` tool, which writes the model back where it
was loaded from (the same format and line endings, as `--save` does), then the report files its renames rewrote
(`reportFiles` in the results). In-place saves check the report originals before writing and roll back file edits if a later write fails. It refuses while the session's changes have
introduced errors that weren't there when the model was loaded, unless called with `force: true`. A model loaded
from a server is never saved (`sm deploy` deploys files). There is no tool for raw TMSL or XMLA.

**The audit log.** `--audit-log <file>` appends one JSON line per tool call: `time` (UTC), `tool`, `objects` (the
object names in the arguments), `outcome` (`ok`, `error`, `unknown-tool`) and, for an error, `errorCode` (never
the message, which can quote a value). A first line (`event: start`) records the model, the grants and the tools
offered. Property values, DAX, query text and query results are never written.

**stdout is the protocol.** It carries JSON-RPC frames only, one per line, UTF-8 without a BOM. sm's own
messages (the start-up line, option errors even with `--json`, `--help`, sign-in prompts for `--server`) go to
stderr. sm serves until its input ends.

**Not done:** resources, prompts, sampling, progress notifications and cancellation (requests are answered one
at a time, in order); `add` covers five kinds (the rest through a `script` operation, or `sm add`); there is no undo
within a session other than not saving. Errors keep their CLI wording, which may mention a `sm` command.
