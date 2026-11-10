# LabVIEW Skill without an MCP server

The CLI router calls the existing attributed C# tool methods directly. It
does not start an MCP host, open a stdio transport, send MCP messages or use
an MCP client. The SDK remains a dependency for tool metadata/schema
generation; this is not an SDK-free build. LabVIEW backends (pylabview,
ActiveX and NI's localhost gRPC service) are unchanged.

The original no-argument MCP mode remains available for compatibility.
**Do not install/register the MCP plugin or launch the executable without
arguments when using the Skill.** CLI use and underlying automation still
require your organization's approval; this interface is not a policy bypass.

## Use the source checkout

Build the development executable normally:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
```

The workspace Skill is [SKILL.md](../../.github/skills/labview/SKILL.md).
VS Code/Copilot can discover it under `.github\skills\labview`; reopen the
session if necessary. Its [launcher](../../.github/skills/labview/scripts/labview.ps1)
finds this checkout's Debug build without registering MCP:

```powershell
$launcher = '.\.github\skills\labview\scripts\labview.ps1'
powershell -NoProfile -File $launcher -DescribeTool lvai_generate_vi

[IO.File]::WriteAllText("$env:TEMP\pane.json", '{"pattern":4833}',
    [Text.UTF8Encoding]::new($false))
powershell -NoProfile -File $launcher -Invoke lvai_connector_pane `
    -ArgsFile "$env:TEMP\pane.json"
```

The pane lookup needs no running LabVIEW. `pylv_status` diagnoses the bundle;
use an argument file containing `{}`. Starting LabVIEW, changing files and
running VIs require separate approval.

## Portable installation

CI attaches a separate `labview-skill.zip` and `labview-skill.sha256` to new
releases. Verify the archive's digest, then extract its `labview` directory to
one supported Skill location:

- Copilot workspace: `<project>\.github\skills\labview`
- Copilot personal: `%USERPROFILE%\.copilot\skills\labview`
- Codex: `<project>\.agents\skills\labview` or `%USERPROFILE%\.agents\skills\labview`
- Claude Code: `<project>\.claude\skills\labview` or `%USERPROFILE%\.claude\skills\labview`

Do not install the MCP archive, agents, hooks or `.mcp.json` as part of this
route. The Skill archive contains a guarded launcher, generated tool index
and full schemas, plus the executable's dependencies, scripts, docs and
pylabview runtime. The launcher defaults to the packaged executable.
`-Executable <absolute path>` or `LABVIEW_SKILL_EXE` explicitly overrides it;
use a reviewed build supporting the new CLI flags, not an older MCP-only
executable.

For local development or internal packaging, export an already built
runtime into a **new** directory:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Export-LabviewSkill.ps1 `
    -Executable src\LabVIEWMCP\bin\Debug\net8.0\LabVIEWMCP.exe `
    -Destination skill-staging\labview
```

Local exports are development artifacts, not official releases. Release
exports use the CI-published self-contained executable. A local
framework-dependent export still requires .NET 8. The exporter refuses an
existing destination; it never replaces an installed Skill silently.
`references\tools.json` and `references\tool-index.md` are generated from
the **copied executable**, so they describe the runtime actually packaged.
Regenerate the package after updating it. No hard-coded tool count or
per-tool CLI wrapper needs maintenance.

## Direct CLI contract

```powershell
$exe = '.\src\LabVIEWMCP\bin\Debug\net8.0\LabVIEWMCP.exe'
& $exe --list-tools --out "$env:TEMP\tools.json" | Out-Null
& $exe --describe-tool lvai_convert_vi_to_aixml
& $exe --invoke lvai_connector_pane --args-file "$env:TEMP\pane.json"
```

- `--list-tools` returns `{ "tools": [...] }`, sorted by exact tool name.
  Entries include descriptions, JSON input schemas and safety annotations.
- `--describe-tool <name>` returns one descriptor. Metadata discovery needs
  no LabVIEW, makes no backend connection and does not migrate settings.
- `--invoke <name> --args-file <path>` binds the JSON object and invokes
  only that declared tool method through normal dependency injection.
  Optional arguments keep their C# defaults. Cancellation tokens are not
  exposed as user arguments.
- Argument spelling follows existing diagnostics: case, `_` and `-` near
  misses are folded. Unknown keys, duplicates, missing required values,
  forbidden nulls and invalid JSON/types are refused before invocation.
  For parameters declared as strings containing JSON, an object/array can
  be supplied and is passed as JSON text; bool/numeric parameters stay typed.
- Valid JSON tool results are returned unchanged; prose is wrapped as
  `{ "text": "..." }`. Stdout contains one JSON value, with diagnostics on
  stderr. `--out <path>` additionally writes the answer as UTF-8 without
  BOM. It can overwrite that output file; choose the path deliberately.
- `--port`, `--aixml-backend` and `--lvversion` retain their existing
  meanings. Invocation initializes settings and checks cache compatibility
  like MCP startup, without its background index/worker prewarming.
- `--timeout <seconds>` is a cooperative whole-call budget (default 300,
  range 1-86400). Ctrl+C requests cancellation too. This is separate from
  any tool argument named `timeoutSeconds`. It is not a hard termination
  or transactional rollback: uncancellable work may finish later.
- Invalid/mixed router options never fall through to MCP. The guarded
  launcher accepts only the three router modes and refuses no-mode calls.

| Exit code | Meaning |
|---|---|
| 0 | Tool returned without a recognized top-level failure marker |
| 1 | Tool result reports failure, invocation failed, or output could not be written |
| 2 | Invalid CLI request, arguments file, argument binding or unknown tool |
| 130 | Cancellation/whole-call timeout |

Tool failures retain their original JSON (`ok: false`, `errorCode`,
`errorKind`, `failedAtStep`, etc.). Exit zero alone does not prove a generated
VI is executable or correct: inspect the tool's documented result and
nested/partial results, and verify persisted artifacts and behavior.

## State and limits

Each invocation is a new process. Projects and files persist in LabVIEW/on
disk, but in-process subscriptions, cached connections, monitor sessions and
Python workers do not persist across separate CLI commands. Long-running
tools retain their existing internal limits; this change does not remove
the MCP-era wait clamps or redesign stateful workflows. Keep related
streaming work within one tool invocation.

The tool catalogue/schema parity test covers every registered tool. CLI
tests cover static/offline tools, argument binding, errors, real subprocess
execution, cancellation, and streaming against a local fake NI gRPC
service. This is not a claim that every tool has been behaviorally tested
on a real LabVIEW installation. In particular, authorization and hardware
safety are not enforced by metadata or the launcher.
